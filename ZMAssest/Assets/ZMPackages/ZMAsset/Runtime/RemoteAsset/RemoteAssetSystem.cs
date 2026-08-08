using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 负责远端资源模块初始化、缺失 Bundle 下载和校验后原子提交。
    /// </summary>
    internal sealed class RemoteAssetSystem : Singleton<RemoteAssetSystem>
    {
        private readonly Dictionary<string, RemoteAssetModule> mModules = new Dictionary<string, RemoteAssetModule>(StringComparer.Ordinal);
        
        private readonly object mModuleLock = new object();

        /// <summary>
        /// 进行中下载的实时进度表，key 为 "模块名|文件名"。
        /// 并发字典兜底：进度回调与查询均在主线程，但保持与 DownLoadThread 静态去重路径同等的线程安全假设。
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> mDownloadProgress =
            new System.Collections.Concurrent.ConcurrentDictionary<string, float>();

        private static string BuildProgressKey(string moduleName, string fileName)
        {
            return moduleName + "|" + fileName;
        }

        /// <summary>
        /// 查询进行中下载的实时进度（0~1）；文件未在下载（未开始/已完成/已失败）时返回 false。
        /// 供门面加载与预下载的进度回调轮询。
        /// </summary>
        internal bool TryGetDownloadProgress(string moduleName, string fileName, out float progress)
        {
            return mDownloadProgress.TryGetValue(BuildProgressKey(moduleName, fileName), out progress);
        }

        /// <summary>
        /// 业务进度回调只允许记警告，不允许中断下载主流程。
        /// </summary>
        private static void ReportProgressSafely(Action<float> onProgress, float value, string moduleName)
        {
            try
            {
                onProgress(value);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"远端资源进度回调异常，模块：{moduleName}，异常：{exception}");
            }
        }

        /// <summary>
        /// 初始化远端模块；并发调用会等待同一个模块级互斥锁，异常不会遗留永久等待者。
        /// </summary>
        internal async UniTask<bool> InitializeRemoteModuleAsync(string moduleName)
        {
            RemoteAssetModule module = GetOrCreateModule(moduleName);
            await module.InitializationGate.WaitAsync();
            try
            {
                if (module.IsInitialized)
                    return true;

                if (!await module.RefreshManifestAsync())
                    return false;

                string configName = BundleSettings.Instance.GetBundleCfgName(moduleName);
                if (module.TryGetFileNeedingDownload(configName, out HotFileInfo configFile) &&
                    !await DownloadAndPromoteAsync(module, configFile))
                    return false;

                bool initialized = await AssetBundleManager.Instance.InitializeAssetModule(moduleName);
                if (!initialized)
                    return false;

                await module.CommitManifestAsync();
                module.IsInitialized = true;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"远端资源模块初始化失败，模块：{moduleName}，异常：{exception}");
                return false;
            }
            finally
            {
                module.InitializationGate.Release();
            }
        }

        /// <summary>
        /// 确保模块配置就绪后，下载目标 Bundle 及其同模块依赖。
        /// </summary>
        /// <param name="onProgress">可选的下载进度回调（0~1），覆盖主包与同模块依赖的缺失文件集合。</param>
        internal async UniTask<bool> PrepareAssetAsync(string moduleName, uint crc, Action<float> onProgress = null)
        {
            if (!await InitializeRemoteModuleAsync(moduleName))
                return false;

            RemoteAssetModule module = GetOrCreateModule(moduleName);
            BundleItem item = AssetBundleManager.Instance.GetBundleItemByCrc(crc);
            if (item == null)
            {
                Debug.LogError($"远端资源不存在于模块配置中，模块：{moduleName}，CRC：{crc}");
                return false;
            }

            if (!string.Equals(item.bundleModuleType, moduleName, StringComparison.Ordinal))
            {
                Debug.LogError(
                    $"远端资源模块不匹配，调用模块：{moduleName}，真实模块：{item.bundleModuleType}，CRC：{crc}");
                return false;
            }

            // 无进度回调走原路径；有进度回调时按"主包+同模块依赖缺失集合"与预下载同口径汇报，
            // 保证模块首次加载（索引建立后）也能收到下载进度。
            if (onProgress == null)
            {
                if (!await EnsureBundleReadyAsync(module, item.bundleName))
                    return false;

                List<string> dependencyBundles = CollectCurrentModuleDependencyBundles(item, moduleName);
                foreach (string dependencyBundle in dependencyBundles)
                {
                    if (!await EnsureBundleReadyAsync(module, dependencyBundle))
                        return false;
                }

                return true;
            }

            List<HotFileInfo> pendingFiles = CollectPendingFiles(module, item);
            for (int i = 0; i < pendingFiles.Count; i++)
            {
                HotFileInfo fileInfo = pendingFiles[i];
                int completedCount = i;
                bool succeeded = await EnsureBundleReadyWithProgressAsync(
                    module,
                    fileInfo.abName,
                    value => ReportProgressSafely(onProgress, (completedCount + value) / pendingFiles.Count, moduleName));

                if (!succeeded)
                    return false;

                ReportProgressSafely(onProgress, (i + 1f) / pendingFiles.Count, moduleName);
            }

            return true;
        }

        /// <summary>
        /// 闲时预下载整个模块尚未就绪的远端文件。
        /// 与按需加载并发时通过文件级门互斥：先到先下，后者拿锁重检后直接复用，不会重复下载。
        /// 串行逐个下载是刻意设计：闲时预热不应抢占前台加载的带宽。
        /// </summary>
        internal async UniTask<RemotePreDownloadResult> PreDownloadModuleAsync(string moduleName, Action<float> onProgress = null)
        {
            if (!await InitializeRemoteModuleAsync(moduleName))
            {
                Debug.LogError($"远端资源预下载失败，模块初始化未通过，模块：{moduleName}");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

            RemoteAssetModule module = GetOrCreateModule(moduleName);
            List<HotFileInfo> pendingFiles = module.GetFilesNeedingDownloadSnapshot();
            return await DownloadFilesWithProgressAsync(module, pendingFiles, onProgress);
        }

        /// <summary>
        /// 闲时预下载指定资源的主 Bundle 及其同模块依赖；跨模块依赖由对应模块自行按需准备，不在本模块预下载范围。
        /// </summary>
        internal async UniTask<RemotePreDownloadResult> PreDownloadAssetAsync(string moduleName, uint crc, Action<float> onProgress = null)
        {
            if (!await InitializeRemoteModuleAsync(moduleName))
            {
                Debug.LogError($"远端资源预下载失败，模块初始化未通过，模块：{moduleName}");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

            BundleItem item = AssetBundleManager.Instance.GetBundleItemByCrc(crc);
            if (item == null)
            {
                Debug.LogError($"远端资源预下载失败，资源不存在于模块配置中，模块：{moduleName}，CRC：{crc}");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

            if (!string.Equals(item.bundleModuleType, moduleName, StringComparison.Ordinal))
            {
                Debug.LogError(
                    $"远端资源预下载模块不匹配，调用模块：{moduleName}，真实模块：{item.bundleModuleType}，CRC：{crc}");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

            RemoteAssetModule module = GetOrCreateModule(moduleName);
            List<HotFileInfo> pendingFiles = CollectPendingFiles(module, item);
            return await DownloadFilesWithProgressAsync(module, pendingFiles, onProgress);
        }

        /// <summary>
        /// 收集目标资源主 Bundle 与同模块依赖中仍未就绪的远端文件，按文件名去重。
        /// </summary>
        private static List<HotFileInfo> CollectPendingFiles(RemoteAssetModule module, BundleItem item)
        {
            List<HotFileInfo> pendingFiles = new List<HotFileInfo>();
            HashSet<string> collected = new HashSet<string>(StringComparer.Ordinal);

            void TryAdd(string bundleName)
            {
                if (module.TryGetFileNeedingDownload(bundleName, out HotFileInfo fileInfo) && collected.Add(bundleName))
                    pendingFiles.Add(fileInfo);
            }

            TryAdd(item.bundleName);
            foreach (string dependencyBundle in CollectCurrentModuleDependencyBundles(item, module.ModuleName))
                TryAdd(dependencyBundle);

            return pendingFiles;
        }

        /// <summary>
        /// 逐个下载缺失文件并汇报整体进度；单文件失败不中断其余文件。
        /// 整体进度 = (已完成文件数 + 当前文件实时进度) / 总文件数，文件就绪（含并发就绪）即计入完成。
        /// </summary>
        private async UniTask<RemotePreDownloadResult> DownloadFilesWithProgressAsync(
            RemoteAssetModule module,
            List<HotFileInfo> pendingFiles,
            Action<float> onProgress)
        {
            int totalCount = pendingFiles.Count;
            int successCount = 0;
            List<string> failedFiles = new List<string>();

            for (int i = 0; i < totalCount; i++)
            {
                HotFileInfo fileInfo = pendingFiles[i];
                int completedCount = i;
                bool succeeded = await EnsureBundleReadyWithProgressAsync(
                    module,
                    fileInfo.abName,
                    onProgress == null ? null : value => ReportProgressSafely(onProgress, (completedCount + value) / totalCount, module.ModuleName));

                if (succeeded)
                    successCount++;
                else
                    failedFiles.Add(fileInfo.abName);

                if (onProgress != null) ReportProgressSafely(onProgress, (i + 1f) / totalCount, module.ModuleName);
            }

            return new RemotePreDownloadResult(module.ModuleName, true, totalCount, successCount, failedFiles);
        }

        /// <summary>
        /// 下载单个 Bundle 并按 100ms 间隔汇报该文件实时进度。
        /// 标志位 + 局部函数保证 UniTask 只被单次 await；进度轮询本身不持有任何下载状态。
        /// </summary>
        private async UniTask<bool> EnsureBundleReadyWithProgressAsync(
            RemoteAssetModule module,
            string bundleName,
            Action<float> onFileProgress)
        {
            bool finished = false;

            async UniTask<bool> RunEnsure()
            {
                try
                {
                    return await EnsureBundleReadyAsync(module, bundleName);
                }
                finally
                {
                    finished = true;
                }
            }

            UniTask<bool> ensureTask = RunEnsure();
            while (!finished)
            {
                if (onFileProgress != null &&
                    TryGetDownloadProgress(module.ModuleName, bundleName, out float fileProgress))
                {
                    onFileProgress(fileProgress);
                }

                await UniTask.Delay(100);
            }

            return await ensureTask;
        }

        /// <summary>
        /// 收集需要由当前远端模块准备的依赖 Bundle。
        /// 跨模块依赖由 AssetBundleManager 按完整 ModuleBundleKey 从对应模块目录加载，
        /// 不能拿当前模块的 Manifest 查询 Shared 等模块的 Bundle。
        /// </summary>
        internal static List<string> CollectCurrentModuleDependencyBundles(
            BundleItem item,
            string moduleName)
        {
            List<string> result = new List<string>();
            if (item == null)
                return result;

            if (item.bundleDependencies != null && item.bundleDependencies.Count > 0)
            {
                foreach (ModuleBundleKey dependencyKey in item.bundleDependencies)
                {
                    // 只有物理归属于当前远端模块的依赖，才进入当前模块的按需下载流程。
                    if (string.Equals(dependencyKey.ModuleName, moduleName, StringComparison.Ordinal))
                        result.Add(dependencyKey.BundleName);
                }

                return result;
            }

            // 旧版配置只有 Bundle 名称，没有模块归属；按照旧协议约定，它们均属于当前模块。
            if (item.bundleDependce != null)
                result.AddRange(item.bundleDependce);

            return result;
        }

        /// <summary>
        /// 获取或创建模块状态；字典锁只保护短临界区，不在锁内执行网络或文件操作。
        /// </summary>
        private RemoteAssetModule GetOrCreateModule(string moduleName)
        {
            lock (mModuleLock)
            {
                if (!mModules.TryGetValue(moduleName, out RemoteAssetModule module))
                {
                    module = new RemoteAssetModule(moduleName);
                    mModules.Add(moduleName, module);
                }

                return module;
            }
        }

        /// <summary>
        /// Manifest 未标记为缺失时直接复用本地文件；缺失时下载、MD5 校验并原子切换。
        /// </summary>
        private async UniTask<bool> EnsureBundleReadyAsync(RemoteAssetModule module, string bundleName)
        {
            if (string.IsNullOrWhiteSpace(bundleName))
                return false;

            System.Threading.SemaphoreSlim fileGate = module.GetFileGate(bundleName);
            await fileGate.WaitAsync();
            try
            {
                // 获得锁后必须重新检查，前一个等待者可能已经完成下载和原子切换。
                if (!module.TryGetFileNeedingDownload(bundleName, out HotFileInfo fileInfo))
                {
                    if (!module.TryGetRemoteFile(bundleName, out _))
                    {
                        Debug.LogError($"远端 Manifest 未声明 Bundle，模块：{module.ModuleName}，文件：{bundleName}");
                        return false;
                    }

                    return true;
                }

                return await DownloadAndPromoteAsync(module, fileInfo);
            }
            finally
            {
                fileGate.Release();
            }
        }

        /// <summary>
        /// 下载器只写临时目录并完成 MD5 校验，随后再将同卷文件原子替换到正式目录。
        /// </summary>
        private async UniTask<bool> DownloadAndPromoteAsync(RemoteAssetModule module, HotFileInfo fileInfo)
        {
            Directory.CreateDirectory(module.StagingPath);
            string stagingPath = Path.Combine(module.StagingPath, fileInfo.abName);
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);

            DownLoadThread download = new DownLoadThread(
                module.ModuleName,
                fileInfo,
                module.DownloadUrl,
                module.StagingPath);

            // 进度只反映"本次下载尝试"；任务结束（成功/失败/异常）都必须移除，避免查询方读到过期进度。
            string progressKey = BuildProgressKey(module.ModuleName, fileInfo.abName);
            // 重试会从头下载同一文件；进度只增不减，避免业务进度条回退跳变。
            download.OnDownloadProgress = (_, _, value) =>
                mDownloadProgress.AddOrUpdate(progressKey, value, (_, oldValue) => Math.Max(oldValue, value));
            bool downloaded;
            try
            {
                downloaded = await download.StartDownLoadAsync();
            }
            finally
            {
                mDownloadProgress.TryRemove(progressKey, out _);
            }

            if (!downloaded)
                return false;

            try
            {
                string destinationPath = Path.Combine(module.AssetSavePath, fileInfo.abName);
                RemoteAssetModule.PromoteVerifiedFile(stagingPath, destinationPath);
                module.MarkFileReady(fileInfo.abName);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"远端资源原子切换失败，模块：{module.ModuleName}，文件：{fileInfo.abName}，异常：{exception}");
                return false;
            }
        }
    }
}
