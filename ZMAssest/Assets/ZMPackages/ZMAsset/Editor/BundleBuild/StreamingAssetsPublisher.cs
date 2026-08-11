using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ZM.Asset
{
    /// <summary>
    ///  把多个模块完整复制到同盘临时目录，校验所有文件后再通过一个原子事务切换到 StreamingAssets。
    ///  该类只负责文件系统事务，不读取 Unity 界面状态，便于 EditMode 测试覆盖成功与失败边界。
    /// </summary>
    internal static class StreamingAssetsPublisher
    {
        //00 staging 固定放在 AssetBundle 内嵌根目录下，确保目录 Move 与正式模块目录位于同一磁盘卷。
        private const string StagingDirectoryName = ".zmasset-staging";

        /// <summary>
        ///  描述一个模块的已构建源目录；模块名决定 StreamingAssets 下的最终目录名。
        /// </summary>
        internal sealed class ModuleSource
        {
            internal string ModuleName { get; }
            internal string SourceDirectory { get; }

            internal ModuleSource(string moduleName, string sourceDirectory)
            {
                ModuleName = moduleName;
                SourceDirectory = sourceDirectory;
            }
        }

        /// <summary>
        ///  发布全部模块；任何源目录、复制或校验失败都会发生在正式目录切换之前。
        /// </summary>
        internal static void Publish(
            IReadOnlyList<ModuleSource> moduleSources,
            string streamingAssetBundleRoot,
            Action<string, float> progress = null)
        {
            //00 空模块集合不能被解释为“清空内嵌目录”，因此作为调用错误立即拒绝。
            if (moduleSources == null || moduleSources.Count == 0)
                throw new InvalidOperationException("没有选择任何可内嵌的资源模块。");
            if (string.IsNullOrWhiteSpace(streamingAssetBundleRoot))
                throw new ArgumentException("StreamingAssets 的 AssetBundle 根目录不能为空。", nameof(streamingAssetBundleRoot));

            //00 先完成全部输入预检，确保无效的后续模块不会导致前面模块已经开始复制或发布。
            List<ValidatedModuleSource> validatedSources = ValidateSources(moduleSources);
            string normalizedTargetRoot = Path.GetFullPath(streamingAssetBundleRoot);
            Directory.CreateDirectory(normalizedTargetRoot);

            //00 每次内嵌使用独立事务目录，异常遗留不会与下一次任务共享文件。
            string transactionId = Guid.NewGuid().ToString("N");
            string stagingContainer = Path.Combine(normalizedTargetRoot, StagingDirectoryName);
            string transactionRoot = Path.Combine(stagingContainer, transactionId);
            Directory.CreateDirectory(transactionRoot);

            try
            {
                List<MultiModuleBuildOrchestrator.AtomicPublishItem> publishItems =
                    new List<MultiModuleBuildOrchestrator.AtomicPublishItem>(validatedSources.Count);
                int totalFileCount = validatedSources.Sum(source => source.Files.Count);
                int copiedFileCount = 0;

                foreach (ValidatedModuleSource source in validatedSources)
                {
                    //00 每个模块拥有独立 staging 子目录，最终可作为一个完整目录项参与原子切换。
                    string moduleStagingPath = Path.Combine(transactionRoot, source.ModuleName);
                    Directory.CreateDirectory(moduleStagingPath);
                    foreach (SourceFile sourceFile in source.Files)
                    {
                        //00 保留相对目录结构，避免未来 Bundle 输出包含子目录时因同名文件被压平覆盖。
                        string stagingFilePath = Path.Combine(moduleStagingPath, sourceFile.RelativePath);
                        string stagingParent = Path.GetDirectoryName(stagingFilePath);
                        if (string.IsNullOrWhiteSpace(stagingParent))
                            throw new InvalidOperationException($"无法解析内嵌文件父目录：{stagingFilePath}");
                        Directory.CreateDirectory(stagingParent);
                        File.Copy(sourceFile.AbsolutePath, stagingFilePath, false);
                        copiedFileCount++;
                        progress?.Invoke(
                            $"{source.ModuleName}/{sourceFile.RelativePath.Replace('\\', '/')}",
                            copiedFileCount / (float)Math.Max(1, totalFileCount));
                    }

                    //00 文件数量、相对路径、长度和 SHA-256 必须全部一致，校验通过前不会触碰正式目录。
                    ValidateStagedModule(source, moduleStagingPath);
                    string targetPath = Path.Combine(normalizedTargetRoot, source.ModuleName);
                    publishItems.Add(MultiModuleBuildOrchestrator.AtomicPublishItem.Directory(
                        moduleStagingPath,
                        targetPath));
                }

                //00 所有模块校验完成后一次提交；发布器会统一备份，并在任一 Move 失败时恢复全部旧目录。
                MultiModuleBuildOrchestrator.AtomicPublisher.Publish(publishItems, transactionId);
            }
            finally
            {
                CleanupStaging(transactionRoot, stagingContainer);
            }
        }

        /// <summary>
        ///  清理当前事务临时目录；清理异常只记录告警，不能覆盖更重要的复制、校验或发布原始异常。
        /// </summary>
        private static void CleanupStaging(string transactionRoot, string stagingContainer)
        {
            try
            {
                //00 仅删除当前 GUID 对应的精确临时目录；已经 Move 到正式位置的模块不在该目录中。
                if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, true);
                //00 staging 容器为空时一并清理，避免在 StreamingAssets 中留下无效空目录。
                if (Directory.Exists(stagingContainer) &&
                    Directory.GetFileSystemEntries(stagingContainer).Length == 0)
                    Directory.Delete(stagingContainer);
            }
            catch (Exception cleanupException)
            {
                //00 文件占用时保留精确目录供人工删除；下一次使用新 GUID，不会复用或覆盖残留内容。
                UnityEngine.Debug.LogWarning(
                    $"StreamingAssets 内嵌事务已结束，但临时目录清理失败：{transactionRoot}\n{cleanupException}");
            }
        }

        /// <summary>
        ///  规范化模块名和源目录，并冻结待复制文件清单。
        /// </summary>
        private static List<ValidatedModuleSource> ValidateSources(IReadOnlyList<ModuleSource> moduleSources)
        {
            List<ValidatedModuleSource> validatedSources = new List<ValidatedModuleSource>(moduleSources.Count);
            HashSet<string> moduleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModuleSource source in moduleSources)
            {
                if (source == null) throw new ArgumentException("内嵌模块列表包含空项。", nameof(moduleSources));
                string moduleName = source.ModuleName?.Trim();
                //00 模块名必须是单一目录名，禁止绝对路径和 ../ 等片段逃逸 StreamingAssets 根目录。
                if (string.IsNullOrWhiteSpace(moduleName) ||
                    string.Equals(moduleName, ".", StringComparison.Ordinal) ||
                    string.Equals(moduleName, "..", StringComparison.Ordinal) ||
                    string.Equals(moduleName, StagingDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(moduleName), moduleName, StringComparison.Ordinal) ||
                    moduleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException($"内嵌模块名称无效：{source.ModuleName}", nameof(moduleSources));
                if (!moduleNames.Add(moduleName))
                    throw new InvalidOperationException($"内嵌模块名称重复：{moduleName}");
                if (string.IsNullOrWhiteSpace(source.SourceDirectory))
                    throw new ArgumentException($"模块 {moduleName} 的构建源目录为空。", nameof(moduleSources));

                string sourceDirectory = Path.GetFullPath(source.SourceDirectory);
                if (!Directory.Exists(sourceDirectory))
                    throw new DirectoryNotFoundException($"模块 {moduleName} 尚未生成可内嵌的 AssetBundle：{sourceDirectory}");
                List<SourceFile> files = EnumerateFiles(sourceDirectory);
                if (files.Count == 0)
                    throw new InvalidOperationException($"模块 {moduleName} 的构建目录为空，不能覆盖现有内嵌资源：{sourceDirectory}");
                validatedSources.Add(new ValidatedModuleSource(moduleName, files));
            }
            return validatedSources;
        }

        /// <summary>
        ///  生成按相对路径排序的源文件快照，保证日志、进度和校验结果可复现。
        /// </summary>
        private static List<SourceFile> EnumerateFiles(string sourceDirectory)
        {
            string sourcePrefix = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                  Path.DirectorySeparatorChar;
            return Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetFullPath(path))
                .Select(path => new SourceFile(path, path.Substring(sourcePrefix.Length)))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        ///  对比源快照和 staging，确保复制过程中没有缺失、额外文件或内容变化。
        /// </summary>
        private static void ValidateStagedModule(ValidatedModuleSource source, string moduleStagingPath)
        {
            List<SourceFile> stagedFiles = EnumerateFiles(moduleStagingPath);
            if (source.Files.Count != stagedFiles.Count)
                throw new InvalidDataException(
                    $"模块 {source.ModuleName} 内嵌校验失败：源文件 {source.Files.Count} 个，临时文件 {stagedFiles.Count} 个。");

            Dictionary<string, SourceFile> stagedByPath = stagedFiles.ToDictionary(
                file => file.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            foreach (SourceFile sourceFile in source.Files)
            {
                if (!stagedByPath.TryGetValue(sourceFile.RelativePath, out SourceFile stagedFile))
                    throw new InvalidDataException(
                        $"模块 {source.ModuleName} 内嵌校验失败：临时目录缺少 {sourceFile.RelativePath}。");
                FileInfo sourceInfo = new FileInfo(sourceFile.AbsolutePath);
                FileInfo stagedInfo = new FileInfo(stagedFile.AbsolutePath);
                if (sourceInfo.Length != stagedInfo.Length ||
                    !string.Equals(ComputeSha256(sourceFile.AbsolutePath), ComputeSha256(stagedFile.AbsolutePath),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"模块 {source.ModuleName} 内嵌校验失败：文件内容不一致 {sourceFile.RelativePath}。");
            }
        }

        /// <summary>
        ///  计算文件 SHA-256；流使用顺序扫描并由 using 确定释放，避免大 Bundle 一次性读入内存。
        /// </summary>
        private static string ComputeSha256(string filePath)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = new FileStream(
                       filePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       81920,
                       FileOptions.SequentialScan))
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private sealed class ValidatedModuleSource
        {
            internal string ModuleName { get; }
            internal List<SourceFile> Files { get; }

            internal ValidatedModuleSource(string moduleName, List<SourceFile> files)
            {
                ModuleName = moduleName;
                Files = files;
            }
        }

        private sealed class SourceFile
        {
            internal string AbsolutePath { get; }
            internal string RelativePath { get; }

            internal SourceFile(string absolutePath, string relativePath)
            {
                AbsolutePath = absolutePath;
                RelativePath = relativePath;
            }
        }
    }
}
