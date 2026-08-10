using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using UnityEngine;
using System.Threading;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 单个物理 Bundle 的内部本地状态；公共 API 只暴露聚合后的资源级状态。
    /// </summary>
    internal enum RemoteBundleLocalState
    {
        Ready,
        Missing,
        Invalid,
        Unknown
    }

    /// <summary>
    /// 负责远端资源模块初始化、缺失 Bundle 下载和校验后原子提交。
    /// </summary>
    internal sealed class RemoteAssetSystem : Singleton<RemoteAssetSystem>
    {
        private readonly Dictionary<string, RemoteAssetModule> mModules = new Dictionary<string, RemoteAssetModule>(StringComparer.Ordinal);
        
        private readonly object mModuleLock = new object();

        private readonly IAssetDownloadService mDownloadService =
            AssetRuntimeBackendFactory.Current.DownloadService;

        private readonly IHotUpdateCommitStrategy mCommitStrategy =
            AssetRuntimeBackendFactory.Current.CommitStrategy;

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
            if (module.UsesBrowserCache)
                await module.BrowserInitializationGate.WaitAsync();
            else
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
                if (module.UsesBrowserCache)
                    module.BrowserInitializationGate.Release();
                else
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
        /// 查询资源是否已经具备无需远端下载的完整 Bundle 闭包。该流程不会刷新服务器 Manifest，也不会启动下载。
        /// </summary>
        internal async UniTask<RemoteAssetLocalResult> GetLocalStatusAsync(
            string assetPath,
            string moduleName,
            uint crc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetBundleManager bundleManager = AssetBundleManager.Instance;
            if (!bundleManager.IsAssetModuleInitialized(moduleName))
            {
                return new RemoteAssetLocalResult(
                    assetPath,
                    moduleName,
                    RemoteAssetLocalStatus.ModuleNotInitialized,
                    0,
                    null);
            }

            BundleItem item = bundleManager.GetBundleItemByCrc(crc);
            if (item == null ||
                !string.Equals(item.bundleModuleType, moduleName, StringComparison.Ordinal))
            {
                return new RemoteAssetLocalResult(
                    assetPath,
                    moduleName,
                    RemoteAssetLocalStatus.AssetNotConfigured,
                    0,
                    null);
            }

            List<ModuleBundleKey> requiredBundles = CollectRequiredBundleKeys(item, moduleName);
            if (item.assetBundle != null || !item.isRemoteAsset)
            {
                return new RemoteAssetLocalResult(
                    assetPath,
                    moduleName,
                    RemoteAssetLocalStatus.Ready,
                    requiredBundles.Count,
                    null);
            }

            RemoteAssetModule module = GetOrCreateModule(moduleName);
            RemoteAssetLocalStatus manifestStatus =
                await EnsureLocalManifestAvailableAsync(module, cancellationToken);
            if (manifestStatus != RemoteAssetLocalStatus.Ready)
            {
                return new RemoteAssetLocalResult(
                    assetPath,
                    moduleName,
                    manifestStatus,
                    requiredBundles.Count,
                    null);
            }

            List<RemoteBundleLocalState> bundleStates =
                new List<RemoteBundleLocalState>(requiredBundles.Count);
            List<string> unavailableBundles = new List<string>();
            foreach (ModuleBundleKey bundleKey in requiredBundles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RemoteBundleLocalState bundleState = GetBundleLocalState(bundleKey, module);
                bundleStates.Add(bundleState);
                if (bundleState != RemoteBundleLocalState.Ready)
                    unavailableBundles.Add(bundleKey.ToString());
            }

            return new RemoteAssetLocalResult(
                assetPath,
                moduleName,
                ClassifyLocalBundleStates(bundleStates),
                requiredBundles.Count,
                unavailableBundles);
        }

        private static async UniTask<RemoteAssetLocalStatus> EnsureLocalManifestAvailableAsync(
            RemoteAssetModule module,
            CancellationToken cancellationToken)
        {
            if (module.UsesBrowserCache)
                await module.BrowserInitializationGate.WaitAsync(cancellationToken);
            else
                await module.InitializationGate.WaitAsync(cancellationToken);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await module.TryLoadCommittedManifestForLocalQueryAsync())
                    return RemoteAssetLocalStatus.Ready;

                return module.UsesBrowserCache
                    ? RemoteAssetLocalStatus.Unknown
                    : RemoteAssetLocalStatus.ManifestUnavailable;
            }
            finally
            {
                if (module.UsesBrowserCache)
                    module.BrowserInitializationGate.Release();
                else
                    module.InitializationGate.Release();
            }
        }

        private RemoteBundleLocalState GetBundleLocalState(
            ModuleBundleKey bundleKey,
            RemoteAssetModule requestedModule)
        {
            if (AssetBundleManager.Instance.IsBundleLoaded(bundleKey))
                return RemoteBundleLocalState.Ready;

            if (!string.Equals(
                    bundleKey.ModuleName,
                    requestedModule.ModuleName,
                    StringComparison.Ordinal))
            {
                // Remote 加载链只会按需准备当前模块的 Bundle；跨模块依赖始终从其所属模块的
                // 内嵌/全局热更目录加载，不能拿另一个 Remote Manifest 判断，否则会把合法的
                // Shared 依赖误报为 Invalid。
                return GetBundledDependencyLocalState(bundleKey);
            }

            if (!requestedModule.HasActiveManifest)
                return RemoteBundleLocalState.Unknown;
            if (!requestedModule.TryGetRemoteFile(bundleKey.BundleName, out _))
                return RemoteBundleLocalState.Invalid;
            return requestedModule.TryGetFileNeedingDownload(bundleKey.BundleName, out _)
                ? RemoteBundleLocalState.Missing
                : RemoteBundleLocalState.Ready;
        }

        /// <summary>
        /// 按 AssetBundleManager 的真实来源规则判断跨模块依赖，不加载 Bundle，也不改变引用计数。
        /// WebGL/Android 的内嵌资源不是普通可同步探测文件，未驻留内存时只能保守返回 Unknown。
        /// </summary>
        private static RemoteBundleLocalState GetBundledDependencyLocalState(
            ModuleBundleKey bundleKey)
        {
            AssetRuntimeBackend runtimeBackend = AssetRuntimeBackendFactory.Current;
            if (runtimeBackend.PlatformKind == AssetRuntimePlatformKind.Native &&
                BundleSettings.Instance.bundleHotType == BundleHotEnum.Hot)
            {
                string hotPath =
                    BundleSettings.Instance.GetHotAssetsPath(bundleKey.ModuleName) +
                    bundleKey.BundleName;
                if (File.Exists(hotPath))
                    return RemoteBundleLocalState.Ready;
            }

            if (runtimeBackend.PlatformKind == AssetRuntimePlatformKind.WebGL ||
                Application.platform == RuntimePlatform.Android)
                return RemoteBundleLocalState.Unknown;

            string builtinPath =
                BundleSettings.Instance.GetAssetsBuiltinBundlePath(bundleKey.ModuleName) +
                bundleKey.BundleName;
            return File.Exists(builtinPath)
                ? RemoteBundleLocalState.Ready
                : RemoteBundleLocalState.Missing;
        }

        /// <summary>
        /// 构建资源主 Bundle 与完整配置依赖闭包，并按模块+文件名去重。
        /// </summary>
        internal static List<ModuleBundleKey> CollectRequiredBundleKeys(
            BundleItem item,
            string moduleName)
        {
            List<ModuleBundleKey> result = new List<ModuleBundleKey>();
            if (item == null)
                return result;

            HashSet<ModuleBundleKey> collected = new HashSet<ModuleBundleKey>();
            void Add(ModuleBundleKey bundleKey)
            {
                if (!string.IsNullOrWhiteSpace(bundleKey.ModuleName) &&
                    !string.IsNullOrWhiteSpace(bundleKey.BundleName) &&
                    collected.Add(bundleKey))
                {
                    result.Add(bundleKey);
                }
            }

            Add(new ModuleBundleKey(moduleName, item.bundleName));
            if (item.bundleDependencies != null && item.bundleDependencies.Count > 0)
            {
                foreach (ModuleBundleKey dependency in item.bundleDependencies)
                    Add(dependency);
            }
            else if (item.bundleDependce != null)
            {
                foreach (string dependency in item.bundleDependce)
                    Add(new ModuleBundleKey(moduleName, dependency));
            }

            return result;
        }

        /// <summary>
        /// 将物理 Bundle 状态聚合成业务可消费的资源级状态。Invalid 优先于缺失，Unknown 只在没有确定失败时返回。
        /// </summary>
        internal static RemoteAssetLocalStatus ClassifyLocalBundleStates(
            IReadOnlyList<RemoteBundleLocalState> bundleStates)
        {
            if (bundleStates == null || bundleStates.Count == 0)
                return RemoteAssetLocalStatus.Invalid;

            bool hasInvalid = false;
            bool isMainBundleMissing = false;
            bool hasMissingDependency = false;
            bool hasUnknown = false;
            for (int index = 0; index < bundleStates.Count; index++)
            {
                RemoteBundleLocalState state = bundleStates[index];
                if (state == RemoteBundleLocalState.Invalid)
                {
                    hasInvalid = true;
                }
                else if (state == RemoteBundleLocalState.Missing)
                {
                    if (index == 0)
                        isMainBundleMissing = true;
                    else
                        hasMissingDependency = true;
                }
                else if (state == RemoteBundleLocalState.Unknown)
                {
                    hasUnknown = true;
                }
            }

            if (hasInvalid)
                return RemoteAssetLocalStatus.Invalid;
            if (isMainBundleMissing)
                return RemoteAssetLocalStatus.Missing;
            if (hasMissingDependency)
                return RemoteAssetLocalStatus.Incomplete;
            return hasUnknown ? RemoteAssetLocalStatus.Unknown : RemoteAssetLocalStatus.Ready;
        }

        /// <summary>
        /// 闲时预下载整个模块尚未就绪的远端文件。
        /// 与按需加载并发时通过文件级门互斥：先到先下，后者拿锁重检后直接复用，不会重复下载。
        /// 串行逐个下载是刻意设计：闲时预热不应抢占前台加载的带宽。
        /// </summary>
        internal async UniTask<RemotePreDownloadResult> PreDownloadModuleAsync(string moduleName, Action<float> onProgress = null, CancellationToken cancellationToken = default)
        {
            if (!await InitializeRemoteModuleAsync(moduleName))
            {
                Debug.LogError($"远端资源预下载失败，模块初始化未通过，模块：{moduleName}");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

            RemoteAssetModule module = GetOrCreateModule(moduleName);
            List<HotFileInfo> pendingFiles = module.GetFilesNeedingDownloadSnapshot();
            return await DownloadFilesWithProgressAsync(module, pendingFiles, onProgress, cancellationToken);
        }

        /// <summary>
        /// 闲时预下载指定资源的主 Bundle 及其同模块依赖；跨模块依赖由对应模块自行按需准备，不在本模块预下载范围。
        /// </summary>
        internal async UniTask<RemotePreDownloadResult> PreDownloadAssetAsync(string moduleName, uint crc, Action<float> onProgress = null, CancellationToken cancellationToken = default)
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
            return await DownloadFilesWithProgressAsync(module, pendingFiles, onProgress, cancellationToken);
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
            Action<float> onProgress,
            CancellationToken cancellationToken)
        {
            int totalCount = pendingFiles.Count;
            int successCount = 0;
            List<string> failedFiles = new List<string>();

            for (int i = 0; i < totalCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HotFileInfo fileInfo = pendingFiles[i];
                int completedCount = i;
                bool succeeded = await EnsureBundleReadyWithProgressAsync(
                    module,
                    fileInfo.abName,
                    onProgress == null ? null : value => ReportProgressSafely(onProgress, (completedCount + value) / totalCount, module.ModuleName),
                    cancellationToken);

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
            Action<float> onFileProgress,
            CancellationToken cancellationToken = default)
        {
            bool finished = false;

            async UniTask<bool> RunEnsure()
            {
                try
                {
                    return await EnsureBundleReadyAsync(module, bundleName, cancellationToken);
                }
                finally
                {
                    finished = true;
                }
            }

            UniTask<bool> ensureTask = RunEnsure();
            try
            {
                while (!finished)
                {
                    if (onFileProgress != null &&
                        TryGetDownloadProgress(module.ModuleName, bundleName, out float fileProgress))
                    {
                        onFileProgress(fileProgress);
                    }

                    await UniTask.Delay(100, cancellationToken: cancellationToken);
                }

                return await ensureTask;
            }
            catch (OperationCanceledException)
            {
                // 取消轮询时仍观察底层任务，确保文件门和网络请求均已退出后再向上抛出。
                try
                {
                    await ensureTask;
                }
                catch (OperationCanceledException)
                {
                }
                throw;
            }
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

        internal bool TryResolveRemoteLocation(string moduleName, string bundleName, out AssetBundleLocation location)
        {
            lock (mModuleLock)
            {
                if (mModules.TryGetValue(moduleName, out RemoteAssetModule module) &&
                    module.TryCreateRemoteLocation(bundleName, out location))
                    return true;
            }

            location = default;
            return false;
        }

        /// <summary>
        /// Manifest 未标记为缺失时直接复用本地文件；缺失时下载、MD5 校验并原子切换。
        /// </summary>
        private async UniTask<bool> EnsureBundleReadyAsync(RemoteAssetModule module, string bundleName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(bundleName))
                return false;

            if (module.UsesBrowserCache)
                return await EnsureBrowserBundleReadyAsync(module, bundleName, cancellationToken);

            System.Threading.SemaphoreSlim fileGate = module.GetFileGate(bundleName);
            await fileGate.WaitAsync(cancellationToken);
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

                return await DownloadAndPromoteAsync(module, fileInfo, cancellationToken);
            }
            finally
            {
                fileGate.Release();
            }
        }

        private async UniTask<bool> EnsureBrowserBundleReadyAsync(
            RemoteAssetModule module,
            string bundleName,
            CancellationToken cancellationToken)
        {
            WebGLAsyncGate fileGate = module.GetBrowserFileGate(bundleName);
            await fileGate.WaitAsync(cancellationToken);
            try
            {
                if (!module.TryGetFileNeedingDownload(bundleName, out HotFileInfo fileInfo))
                {
                    if (!module.TryGetRemoteFile(bundleName, out _))
                    {
                        Debug.LogError($"远端 Manifest 未声明 Bundle，模块：{module.ModuleName}，文件：{bundleName}");
                        return false;
                    }

                    return true;
                }

                return await DownloadAndPromoteAsync(module, fileInfo, cancellationToken);
            }
            finally
            {
                fileGate.Release();
            }
        }

        /// <summary>
        /// 下载器只写临时目录并完成 MD5 校验，随后再将同卷文件原子替换到正式目录。
        /// </summary>
        private async UniTask<bool> DownloadAndPromoteAsync(RemoteAssetModule module, HotFileInfo fileInfo, CancellationToken cancellationToken = default)
        {
            if (module.UsesBrowserCache)
            {
                string browserProgressKey = BuildProgressKey(module.ModuleName, fileInfo.abName);
                AssetDownloadFileRequest browserRequest = new AssetDownloadFileRequest(
                    $"{module.ModuleName}_webgl_remote_{Guid.NewGuid():N}",
                    module.ModuleName,
                    fileInfo,
                    module.DownloadUrl,
                    null,
                    value => mDownloadProgress.AddOrUpdate(browserProgressKey, value, (_, oldValue) => Math.Max(oldValue, value)),
                    cancellationToken);
                try
                {
                    bool succeeded = await mDownloadService.DownloadFileAsync(browserRequest);
                    if (succeeded) module.MarkFileReady(fileInfo.abName);
                    return succeeded;
                }
                finally
                {
                    mDownloadProgress.TryRemove(browserProgressKey, out _);
                }
            }

            Directory.CreateDirectory(module.StagingPath);
            string stagingPath = Path.Combine(module.StagingPath, fileInfo.abName);
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);

            // 进度只反映"本次下载尝试"；任务结束（成功/失败/异常）都必须移除，避免查询方读到过期进度。
            string progressKey = BuildProgressKey(module.ModuleName, fileInfo.abName);
            string operationId = $"{module.ModuleName}_remote_{Guid.NewGuid():N}";
            AssetDownloadFileRequest downloadRequest = new AssetDownloadFileRequest(
                operationId,
                module.ModuleName,
                fileInfo,
                module.DownloadUrl,
                module.StagingPath,
                value =>
                    // 重试会从头下载同一文件；进度只增不减，避免业务进度条回退跳变。
                    mDownloadProgress.AddOrUpdate(progressKey, value, (_, oldValue) => Math.Max(oldValue, value)));
            bool downloaded;
            try
            {
                downloaded = await mDownloadService.DownloadFileAsync(downloadRequest);
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
                mCommitStrategy.PromoteVerifiedFile(
                    stagingPath,
                    destinationPath,
                    operationId,
                    module.ModuleName,
                    fileInfo.abName);
                module.MarkFileReady(fileInfo.abName);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"远端资源原子切换失败，平台：{AssetRuntimeBackendFactory.Current.PlatformKind}，" +
                    $"模块：{module.ModuleName}，文件：{fileInfo.abName}，操作：{operationId}，异常：{exception}");
                return false;
            }
        }
    }
}
