using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace ZM.ZMAsset
{
    /// <summary>
    /// Native 目录事务实现。
    /// 候选目录与正式目录位于同一文件系统，提交继续使用目录重命名，失败后按备份逆向恢复。
    /// </summary>
    internal sealed class NativeDirectoryCommitStrategy : IHotUpdateCommitStrategy
    {
        internal const string JournalCommittedStage = "JournalCommitted";
        internal const string SnapshotPromotedStage = "SnapshotPromoted";
        internal const string ManifestPromotedStage = "ManifestPromoted";
        internal const string RollbackCompletedStage = "RollbackCompleted";
        internal const string FinalizedStage = "Finalized";
        internal const string VerifiedFilePromotedStage = "VerifiedFilePromoted";
        internal const string VerifiedFileRecoveredStage = "VerifiedFileRecovered";

        private readonly IAssetMetadataStore mMetadataStore;

        [Serializable]
        private sealed class TransactionJournal
        {
            public string transactionId;
            public bool hadFinalSnapshot;
            public bool hadLocalManifest;
        }

        internal NativeDirectoryCommitStrategy(IAssetMetadataStore metadataStore)
        {
            mMetadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        }

        /// <summary>
        /// 测试可订阅该事件冻结关键操作序列；默认无人订阅，不产生运行时日志和额外集合分配。
        /// </summary>
        internal event Action<HotUpdateCommitTrace> TraceRecorded;

        public HotUpdateCommitContext CreateTransaction(
            string moduleName,
            string finalSnapshotPath,
            string localManifestPath)
        {
            ValidateModuleName(moduleName);
            string normalizedFinalPath = NormalizeDirectoryPath(finalSnapshotPath);
            string snapshotParentPath = Directory.GetParent(normalizedFinalPath)?.FullName;
            if (string.IsNullOrEmpty(snapshotParentPath))
                throw new InvalidOperationException($"无法解析热更新目录父路径：{normalizedFinalPath}");

            Directory.CreateDirectory(snapshotParentPath);
            string journalPath = Path.Combine(snapshotParentPath, $".{moduleName}.hotupdate.transaction");
            string transactionId = $"{moduleName}_{Guid.NewGuid():N}";
            HotUpdateCommitContext context = CreateContext(
                moduleName,
                transactionId,
                normalizedFinalPath,
                localManifestPath,
                journalPath,
                Directory.Exists(normalizedFinalPath),
                mMetadataStore.Exists(localManifestPath));

            Directory.CreateDirectory(context.StagingSnapshotPath);
            WriteTransactionJournal(context);
            Record(context.TransactionId, JournalCommittedStage);
            return context;
        }

        public void RecoverInterruptedTransaction(
            string moduleName,
            string finalSnapshotPath,
            string localManifestPath,
            bool forceRollback)
        {
            ValidateModuleName(moduleName);
            string normalizedFinalPath = NormalizeDirectoryPath(finalSnapshotPath);
            string snapshotParentPath = Directory.GetParent(normalizedFinalPath)?.FullName;
            if (string.IsNullOrEmpty(snapshotParentPath))
                throw new InvalidOperationException($"无法解析热更新目录父路径：{normalizedFinalPath}");

            Directory.CreateDirectory(snapshotParentPath);
            string journalPath = Path.Combine(snapshotParentPath, $".{moduleName}.hotupdate.transaction");
            string journalWritingPath = journalPath + ".writing";
            if (!mMetadataStore.Exists(journalPath) && mMetadataStore.Exists(journalWritingPath))
                File.Move(journalWritingPath, journalPath);
            if (!mMetadataStore.Exists(journalPath))
                return;

            TransactionJournal journal;
            try
            {
                journal = JsonConvert.DeserializeObject<TransactionJournal>(mMetadataStore.ReadText(journalPath));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"模块 {moduleName} 的热更新事务日志损坏，已停止覆盖现有资源。", exception);
            }

            if (journal == null || !IsValidTransactionId(moduleName, journal.transactionId))
                throw new InvalidDataException($"模块 {moduleName} 的热更新事务日志标识非法，已停止覆盖现有资源。");

            HotUpdateCommitContext context = CreateContext(
                moduleName,
                journal.transactionId,
                normalizedFinalPath,
                localManifestPath,
                journalPath,
                journal.hadFinalSnapshot,
                journal.hadLocalManifest);

            if (forceRollback)
            {
                if (!Rollback(context, out Exception rollbackFailure))
                    throw new IOException($"模块 {moduleName} 的中断组事务回滚失败。", rollbackFailure);
                return;
            }

            CompleteOrRollbackInterruptedTransaction(context);
        }

        public void PromoteSnapshot(HotUpdateCommitContext context, string manifestJson)
        {
            EnsureContext(context);
            mMetadataStore.WriteText(context.ManifestStagingPath, manifestJson);

            if (context.HadFinalSnapshot)
                Directory.Move(context.FinalSnapshotPath, context.BackupSnapshotPath);
            Directory.Move(context.StagingSnapshotPath, context.FinalSnapshotPath);
            Record(context.TransactionId, SnapshotPromotedStage);

            if (context.HadLocalManifest)
                File.Move(context.LocalManifestPath, context.ManifestBackupPath);
            File.Move(context.ManifestStagingPath, context.LocalManifestPath);
            Record(context.TransactionId, ManifestPromotedStage);
        }

        public bool Rollback(HotUpdateCommitContext context, out Exception failure)
        {
            failure = null;
            if (context == null)
                return true;

            bool rollbackSucceeded = false;
            try
            {
                if (Directory.Exists(context.BackupSnapshotPath))
                {
                    DeleteDirectoryIfExists(context.FinalSnapshotPath);
                    Directory.Move(context.BackupSnapshotPath, context.FinalSnapshotPath);
                }
                else if (!context.HadFinalSnapshot &&
                         Directory.Exists(context.FinalSnapshotPath) &&
                         !Directory.Exists(context.StagingSnapshotPath))
                {
                    DeleteDirectoryIfExists(context.FinalSnapshotPath);
                }

                if (mMetadataStore.Exists(context.ManifestBackupPath))
                {
                    mMetadataStore.DeleteIfExists(context.LocalManifestPath);
                    File.Move(context.ManifestBackupPath, context.LocalManifestPath);
                }
                else if (!context.HadLocalManifest &&
                         mMetadataStore.Exists(context.LocalManifestPath) &&
                         !mMetadataStore.Exists(context.ManifestStagingPath))
                {
                    mMetadataStore.DeleteIfExists(context.LocalManifestPath);
                }

                rollbackSucceeded = true;
                Record(context.TransactionId, RollbackCompletedStage);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                // 回滚失败时保留日志和备份，下一次启动可以从同一现场继续恢复。
                if (rollbackSucceeded)
                {
                    bool stagingRemoved = TryDeleteDirectory(
                        context,
                        context.StagingSnapshotPath,
                        "热更新临时目录");
                    bool manifestStagingRemoved = TryDeleteFile(
                        context,
                        context.ManifestStagingPath,
                        "热更新 Manifest 临时文件");
                    if (stagingRemoved && manifestStagingRemoved)
                        TryDeleteFile(context, context.JournalPath, "热更新事务日志");
                }
            }

            return rollbackSucceeded;
        }

        public void FinalizeTransaction(HotUpdateCommitContext context)
        {
            if (context == null)
                return;

            // 先删除日志声明提交完成；日志无法删除时保留备份，以便重启后继续判定现场。
            if (TryDeleteFile(context, context.JournalPath, "热更新事务日志"))
            {
                TryDeleteDirectory(context, context.BackupSnapshotPath, "热更新备份目录");
                TryDeleteFile(context, context.ManifestBackupPath, "热更新 Manifest 备份");
            }
            Record(context.TransactionId, FinalizedStage);
        }

        public void PromoteVerifiedFile(
            string stagingPath,
            string destinationPath,
            string operationId,
            string moduleName,
            string targetName)
        {
            ValidateVerifiedFileArguments(destinationPath, operationId, moduleName, targetName);
            // 上一次进程可能停在兼容切换的两次 Move 之间；开始新提交前必须先恢复确定状态。
            RecoverVerifiedFile(destinationPath, operationId, moduleName, targetName);

            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            if (!File.Exists(stagingPath))
                throw new FileNotFoundException("待提交的远端资源临时文件不存在。", stagingPath);

            if (!File.Exists(destinationPath))
            {
                File.Move(stagingPath, destinationPath);
                Record(operationId, VerifiedFilePromotedStage);
                return;
            }

            string backupPath = destinationPath + ".remote.backup";
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            try
            {
                File.Replace(stagingPath, destinationPath, backupPath);
            }
            catch (Exception replaceException)
            {
                Debug.LogWarning(
                    $"当前平台不支持直接替换文件，将使用可回滚切换，模块：{moduleName}，目标：{targetName}，" +
                    $"操作：{operationId}，异常：{replaceException.Message}");
                PromoteWithRollback(stagingPath, destinationPath, backupPath);
                Record(operationId, VerifiedFilePromotedStage);
                return;
            }

            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch (Exception cleanupException)
            {
                Debug.LogWarning(
                    $"远端资源旧文件备份清理失败，模块：{moduleName}，目标：{targetName}，操作：{operationId}，" +
                    $"路径：{backupPath}，异常：{cleanupException.Message}");
            }
            Record(operationId, VerifiedFilePromotedStage);
        }

        public void RecoverVerifiedFile(
            string destinationPath,
            string operationId,
            string moduleName,
            string targetName)
        {
            ValidateVerifiedFileArguments(destinationPath, operationId, moduleName, targetName);
            string backupPath = destinationPath + ".remote.backup";
            if (!File.Exists(backupPath))
                return;

            try
            {
                if (File.Exists(destinationPath))
                {
                    // 正式文件存在表示新文件已经可见，残留 backup 只可能来自提交成功后的清理中断。
                    File.Delete(backupPath);
                }
                else
                {
                    // 正式文件缺失表示进程停在“旧文件备份完成、新文件尚未提升”的窗口，恢复旧版本继续可用。
                    File.Move(backupPath, destinationPath);
                }

                Record(operationId, VerifiedFileRecoveredStage);
            }
            catch (Exception exception)
            {
                throw new IOException(
                    $"恢复被中断的远端资源文件失败，模块：{moduleName}，目标：{targetName}，" +
                    $"操作：{operationId}，正式路径：{destinationPath}，备份路径：{backupPath}。",
                    exception);
            }
        }

        private void CompleteOrRollbackInterruptedTransaction(HotUpdateCommitContext context)
        {
            bool directorySwitchCompleted =
                Directory.Exists(context.FinalSnapshotPath) &&
                !Directory.Exists(context.StagingSnapshotPath) &&
                (!context.HadFinalSnapshot || Directory.Exists(context.BackupSnapshotPath));
            if (directorySwitchCompleted && mMetadataStore.Exists(context.ManifestStagingPath))
            {
                if (context.HadLocalManifest &&
                    !mMetadataStore.Exists(context.ManifestBackupPath) &&
                    mMetadataStore.Exists(context.LocalManifestPath))
                {
                    File.Move(context.LocalManifestPath, context.ManifestBackupPath);
                }
                else
                {
                    mMetadataStore.DeleteIfExists(context.LocalManifestPath);
                }
                File.Move(context.ManifestStagingPath, context.LocalManifestPath);
            }

            bool switchCompleted =
                Directory.Exists(context.FinalSnapshotPath) &&
                mMetadataStore.Exists(context.LocalManifestPath) &&
                !Directory.Exists(context.StagingSnapshotPath) &&
                !mMetadataStore.Exists(context.ManifestStagingPath) &&
                (!context.HadFinalSnapshot || Directory.Exists(context.BackupSnapshotPath)) &&
                (!context.HadLocalManifest || mMetadataStore.Exists(context.ManifestBackupPath));
            if (switchCompleted)
            {
                bool journalRemoved = TryDeleteFile(context, context.JournalPath, "中断事务日志");
                if (journalRemoved)
                {
                    TryDeleteDirectory(context, context.BackupSnapshotPath, "中断事务备份目录");
                    TryDeleteFile(context, context.ManifestBackupPath, "中断事务 Manifest 备份");
                }
                if (!journalRemoved)
                    throw new IOException($"模块 {context.ModuleName} 的已提交事务日志无法清理，已停止创建新事务。");
                return;
            }

            if (!Rollback(context, out Exception rollbackFailure))
                throw new IOException($"模块 {context.ModuleName} 的中断事务未能自动回滚，请保留目录并检查磁盘状态。", rollbackFailure);
            if (mMetadataStore.Exists(context.JournalPath))
                throw new IOException($"模块 {context.ModuleName} 的中断事务未能自动回滚，请保留目录并检查磁盘状态。");
        }

        private void WriteTransactionJournal(HotUpdateCommitContext context)
        {
            TransactionJournal journal = new TransactionJournal
            {
                transactionId = context.TransactionId,
                hadFinalSnapshot = context.HadFinalSnapshot,
                hadLocalManifest = context.HadLocalManifest
            };
            string journalWritingPath = context.JournalPath + ".writing";
            mMetadataStore.WriteText(
                journalWritingPath,
                JsonConvert.SerializeObject(journal, Formatting.Indented));
            if (mMetadataStore.Exists(context.JournalPath))
                throw new IOException($"模块 {context.ModuleName} 已存在未处理的热更新事务日志。");
            File.Move(journalWritingPath, context.JournalPath);
        }

        private static HotUpdateCommitContext CreateContext(
            string moduleName,
            string transactionId,
            string finalSnapshotPath,
            string localManifestPath,
            string journalPath,
            bool hadFinalSnapshot,
            bool hadLocalManifest)
        {
            string snapshotParentPath = Directory.GetParent(finalSnapshotPath)?.FullName;
            return new HotUpdateCommitContext
            {
                ModuleName = moduleName,
                TransactionId = transactionId,
                FinalSnapshotPath = finalSnapshotPath,
                StagingSnapshotPath = Path.Combine(snapshotParentPath, $".{transactionId}.staging"),
                BackupSnapshotPath = Path.Combine(snapshotParentPath, $".{transactionId}.backup"),
                LocalManifestPath = localManifestPath,
                ManifestStagingPath = $"{localManifestPath}.{transactionId}.staging",
                ManifestBackupPath = $"{localManifestPath}.{transactionId}.backup",
                JournalPath = journalPath,
                HadFinalSnapshot = hadFinalSnapshot,
                HadLocalManifest = hadLocalManifest
            };
        }

        private static void PromoteWithRollback(string stagingPath, string destinationPath, string backupPath)
        {
            if (!File.Exists(destinationPath))
            {
                if (File.Exists(backupPath))
                    File.Move(backupPath, destinationPath);
                throw new IOException($"远端资源旧文件在切换前意外丢失：{destinationPath}");
            }

            if (File.Exists(backupPath))
                File.Delete(backupPath);

            File.Move(destinationPath, backupPath);
            try
            {
                File.Move(stagingPath, destinationPath);
                File.Delete(backupPath);
            }
            catch
            {
                if (File.Exists(destinationPath))
                    File.Delete(destinationPath);
                if (File.Exists(backupPath))
                    File.Move(backupPath, destinationPath);
                throw;
            }
        }

        private static void EnsureContext(HotUpdateCommitContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
        }

        private static void ValidateVerifiedFileArguments(
            string destinationPath,
            string operationId,
            string moduleName,
            string targetName)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("单文件提交目标路径不能为空。", nameof(destinationPath));
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("单文件提交操作标识不能为空。", nameof(operationId));
            if (string.IsNullOrWhiteSpace(moduleName))
                throw new ArgumentException("单文件提交模块名称不能为空。", nameof(moduleName));
            if (string.IsNullOrWhiteSpace(targetName))
                throw new ArgumentException("单文件提交目标名称不能为空。", nameof(targetName));
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static void ValidateModuleName(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName) ||
                moduleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                moduleName.Contains(Path.DirectorySeparatorChar.ToString()) ||
                moduleName.Contains(Path.AltDirectorySeparatorChar.ToString()))
            {
                throw new InvalidDataException($"非法热更新模块名称：{moduleName}");
            }
        }

        private static bool IsValidTransactionId(string moduleName, string transactionId)
        {
            string prefix = moduleName + "_";
            return !string.IsNullOrEmpty(transactionId) &&
                   transactionId.StartsWith(prefix, StringComparison.Ordinal) &&
                   Guid.TryParseExact(transactionId.Substring(prefix.Length), "N", out _);
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private bool TryDeleteDirectory(HotUpdateCommitContext context, string path, string operationName)
        {
            try
            {
                DeleteDirectoryIfExists(path);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"模块 {context.ModuleName} 清理{operationName}失败，可在下次启动继续回收。" +
                    $"事务：{context.TransactionId}，路径：{path}，异常：{exception}");
                return false;
            }
        }

        private bool TryDeleteFile(HotUpdateCommitContext context, string path, string operationName)
        {
            try
            {
                mMetadataStore.DeleteIfExists(path);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"模块 {context.ModuleName} 清理{operationName}失败，可在下次启动继续回收。" +
                    $"事务：{context.TransactionId}，路径：{path}，异常：{exception}");
                return false;
            }
        }

        private void Record(string operationId, string stage)
        {
            TraceRecorded?.Invoke(new HotUpdateCommitTrace(operationId, stage));
        }
    }
}
