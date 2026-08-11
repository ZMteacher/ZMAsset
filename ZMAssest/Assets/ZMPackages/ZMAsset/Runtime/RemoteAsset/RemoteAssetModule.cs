using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace ZM.Asset
{
    /// <summary>
    /// 保存单个远端资源模块的 Manifest、缺失文件集合和初始化互斥状态。
    /// </summary>
    internal sealed class RemoteAssetModule
    {
        private readonly Dictionary<string, HotFileInfo> mAllRemoteFiles = new Dictionary<string, HotFileInfo>(StringComparer.Ordinal);

        private readonly Dictionary<string, HotFileInfo> mFilesNeedingDownload = new Dictionary<string, HotFileInfo>(StringComparer.Ordinal);

        private readonly Dictionary<string, SemaphoreSlim> mFileGates = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        private readonly Dictionary<string, WebGLAsyncGate> mBrowserFileGates =
            new Dictionary<string, WebGLAsyncGate>(StringComparer.Ordinal);

        private readonly object mFileGateLock = new object();

        private readonly IAssetMetadataStore mMetadataStore;

        private readonly IHotUpdateCommitStrategy mCommitStrategy;

        private HotAssetsManifest mActiveManifest;

        internal RemoteAssetModule(string moduleName)
            : this(
                moduleName,
                AssetRuntimeBackendFactory.Current.MetadataStore,
                AssetRuntimeBackendFactory.Current.CommitStrategy)
        {
        }

        /// <summary>
        /// 测试和平台后端使用的依赖注入入口；生产代码默认从统一工厂取得同一组实现。
        /// </summary>
        internal RemoteAssetModule(
            string moduleName,
            IAssetMetadataStore metadataStore,
            IHotUpdateCommitStrategy commitStrategy)
        {
            // 内部初始化入口也执行相同校验，防止绕过公开门面后生成越界或非法缓存路径。
            ZMAsset.ValidateModuleName(moduleName);
            mMetadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            mCommitStrategy = commitStrategy ?? throw new ArgumentNullException(nameof(commitStrategy));
            ModuleName = moduleName;
            InitializationGate = new SemaphoreSlim(1, 1);
            BrowserInitializationGate = new WebGLAsyncGate(1);
            RemoteManifestCachePath = Path.Combine(Application.persistentDataPath, $"Remote{moduleName}AssetsManifest.json");
        }

        /// <summary>
        /// 当前模块名称。
        /// </summary>
        internal string ModuleName { get; }

        /// <summary>
        /// 同一模块只允许一次配置刷新和初始化，异常路径必须在 finally 中释放。
        /// </summary>
        internal SemaphoreSlim InitializationGate { get; }

        /// <summary>
        /// WebGL 初始化门不依赖线程池续体；Native 继续使用既有 SemaphoreSlim。
        /// </summary>
        internal WebGLAsyncGate BrowserInitializationGate { get; }

        /// <summary>
        /// 当前进程中是否已成功初始化模块配置。
        /// </summary>
        internal bool IsInitialized { get; set; }

        /// <summary>
        /// 远端资源最终落盘目录。
        /// </summary>
        internal string AssetSavePath => BundleSettings.Instance.GetHotAssetsPath(ModuleName);

        /// <summary>
        /// 临时下载目录与正式目录位于同一文件系统，便于校验后执行原子替换。
        /// </summary>
        internal string StagingPath => Path.Combine(AssetSavePath, ".remote_staging");

        /// <summary>
        /// 远端 Manifest 的本地缓存路径，断网时用于加载已经验证过的资源集合。
        /// </summary>
        internal string RemoteManifestCachePath { get; }

        /// <summary>
        /// Manifest 指定的文件下载根地址。
        /// </summary>
        internal string DownloadUrl => mActiveManifest?.downLoadURL;

        /// <summary>
        /// 当前进程是否已经取得可用 Manifest；本地状态查询据此区分“未声明文件”和“尚无法判断”。
        /// </summary>
        internal bool HasActiveManifest => mActiveManifest != null;

        internal bool UsesBrowserCache =>
            AssetRuntimeBackendFactory.Current.PlatformKind == AssetRuntimePlatformKind.WebGL;

        /// <summary>
        /// 首次无缓存时允许较长网络等待；已有缓存时缩短超时，弱网或假死网络下快速回退本地清单。
        /// </summary>
        private const int FirstLoadManifestTimeoutSeconds = 30;
        private const int CachedManifestTimeoutSeconds = 5;

        /// <summary>
        /// 下载远端 Manifest；失败时回退到上一次成功提交的缓存，并重建缺失文件索引。
        /// </summary>
        internal async UniTask<bool> RefreshManifestAsync()
        {
            if (UsesBrowserCache)
            {
                HotAssetsManifest webManifest = await DownloadManifestAsync(FirstLoadManifestTimeoutSeconds);
                if (webManifest != null)
                {
                    try
                    {
                        webManifest.downLoadURL = ResolveBrowserDownloadUrl(
                            BundleSettings.Instance.AssetBundleDownLoadUrl,
                            webManifest.downLoadURL);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogError(
                            $"WebGL RemoteAsset 下载地址无效，模块：{ModuleName}，原因：{exception.Message}");
                        return false;
                    }
                }
                if (!TryValidateManifest(webManifest, out string webFailureReason))
                {
                    Debug.LogError($"WebGL RemoteAsset Manifest 无效，模块：{ModuleName}，原因：{webFailureReason}");
                    return false;
                }

                mActiveManifest = webManifest;
                RebuildBrowserFileIndexes(webManifest);
                return true;
            }

            // Manifest 读取发生在网络请求之前；离线回退同样必须先恢复上次被进程中断的单文件切换。
            mCommitStrategy.RecoverVerifiedFile(
                RemoteManifestCachePath,
                $"{ModuleName}_remote_manifest_recovery",
                ModuleName,
                "RemoteManifest");
            // 本地已有缓存清单时缩短网络等待，避免弱网设备首次加载最长阻塞一个完整超时周期。
            bool hasLocalCache = mMetadataStore.Exists(RemoteManifestCachePath);
            
            HotAssetsManifest manifest = await DownloadManifestAsync(hasLocalCache ? CachedManifestTimeoutSeconds : FirstLoadManifestTimeoutSeconds);
            
            if (TryValidateManifest(manifest, out string networkFailureReason))
            {
                mActiveManifest = manifest;
                // 只有版本号和完整文件身份集合都一致，才能复用上次已经完成的 MD5 结论。
                // 同版本重新发布但文件名或摘要变化时必须重新校验，避免新 Manifest 指向旧字节。
                bool sameVersion = await CachedManifestHasSameVersionAsync(manifest);
                RebuildFileIndexes(manifest, verifyIntegrity: !sameVersion);
                return true;
            }

            // 网络失败、反序列化失败以及可解析但协议非法都属于远端失败，统一回退最后一次原子提交的缓存。
            Debug.LogWarning(
                $"远端资源 Manifest 当前不可用，将尝试本地缓存，模块：{ModuleName}，原因：{networkFailureReason}");
            HotAssetsManifest cachedManifest = await LoadCachedManifestAsync();
            if (!TryValidateManifest(cachedManifest, out string cachedFailureReason))
            {
                Debug.LogError($"远端资源 Manifest 无效，模块：{ModuleName}，远端原因：{networkFailureReason}，缓存原因：{cachedFailureReason}");
                return false;
            }

            mActiveManifest = cachedManifest;
            // 缓存清单即上次完整性校验使用的清单；存在性检查足以恢复缺失下载列表，文件级篡改留待版本升级时的全量校验自愈。
            RebuildFileIndexes(cachedManifest, verifyIntegrity: false);
            return true;
        }

        /// <summary>
        /// 获取当前确实缺失或校验失败的远端文件。
        /// </summary>
        internal bool TryGetFileNeedingDownload(string fileName, out HotFileInfo fileInfo)
        {
            return mFilesNeedingDownload.TryGetValue(fileName, out fileInfo);
        }

        /// <summary>
        /// 获取 Manifest 中声明的文件；用于判断缺失文件是否属于本模块的远端资源集合。
        /// </summary>
        internal bool TryGetRemoteFile(string fileName, out HotFileInfo fileInfo)
        {
            return mAllRemoteFiles.TryGetValue(fileName, out fileInfo);
        }

        /// <summary>
        /// 文件校验并提交成功后，从待下载集合移除。
        /// </summary>
        internal void MarkFileReady(string fileName)
        {
            mFilesNeedingDownload.Remove(fileName);
        }

        /// <summary>
        /// 获取当前缺失文件快照，供闲时预下载遍历。
        /// 快照是发起时刻的副本：遍历期间文件可能被按需加载并发就绪，逐项由文件级门拿锁重检兜底。
        /// 全链路运行在主线程，字典读取无需加锁。
        /// </summary>
        internal List<HotFileInfo> GetFilesNeedingDownloadSnapshot()
        {
            return new List<HotFileInfo>(mFilesNeedingDownload.Values);
        }

        /// <summary>
        /// 同一 Bundle 即使被不同资源 CRC 同时请求，也只能执行一次下载后的文件切换。
        /// </summary>
        internal SemaphoreSlim GetFileGate(string fileName)
        {
            lock (mFileGateLock)
            {
                if (!mFileGates.TryGetValue(fileName, out SemaphoreSlim gate))
                {
                    gate = new SemaphoreSlim(1, 1);
                    mFileGates.Add(fileName, gate);
                }

                return gate;
            }
        }

        internal WebGLAsyncGate GetBrowserFileGate(string fileName)
        {
            lock (mFileGateLock)
            {
                if (!mBrowserFileGates.TryGetValue(fileName, out WebGLAsyncGate gate))
                {
                    gate = new WebGLAsyncGate(1);
                    mBrowserFileGates.Add(fileName, gate);
                }

                return gate;
            }
        }

        /// <summary>
        /// 将已验证的服务端 Manifest 原子提交为下次启动可用的本地缓存。
        /// </summary>
        internal async UniTask CommitManifestAsync()
        {
            if (mActiveManifest == null)
                throw new InvalidOperationException($"模块 {ModuleName} 尚无可提交的远端 Manifest。");

            // RemoteAsset 的浏览器 Bundle 仍由 Unity Cache 管理；事务热更新元数据由独立活动快照持久化。
            if (UsesBrowserCache)
                return;

            string json = JsonConvert.SerializeObject(mActiveManifest);
            string stagingPath = RemoteManifestCachePath + ".writing";
            await mMetadataStore.WriteTextAsync(stagingPath, json);
            mCommitStrategy.PromoteVerifiedFile(
                stagingPath,
                RemoteManifestCachePath,
                $"{ModuleName}_remote_manifest_{Guid.NewGuid():N}",
                ModuleName,
                "RemoteManifest");
        }

        /// <summary>
        /// 下载并解析 Manifest；网络、协议和数据错误都返回失败，由调用方决定是否回退缓存。
        /// </summary>
        private async UniTask<HotAssetsManifest> DownloadManifestAsync(int timeoutSeconds)
        {
            string url =
                $"{BundleSettings.Instance.AssetBundleDownLoadUrl}/HotAssets/{ModuleName}/{BundleSettings.Instance.HotManifestName(ModuleName)}";

            Debug.Log(
                $"远端资源 Manifest 下载，模块：{ModuleName}，" +
                $"地址：{AssetLogUtility.SanitizeUrl(url)}，超时：{timeoutSeconds}s");
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = timeoutSeconds;
                // UniTask 的 UnityWebRequest awaiter 会在 HTTP 非成功状态直接抛出，并把不受信任的响应正文
                // 拼进异常文本。这里显式等待 operation，再由下方统一记录有限的状态与 request.error。
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                    await UniTask.Yield();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"远端资源 Manifest 下载失败，模块：{ModuleName}，错误：{request.error}");
                    return null;
                }

                string content = request.downloadHandler?.text;
                if (string.IsNullOrWhiteSpace(content))
                    return null;

                // UTF-8 BOM 是常见且合法的 JSON 文本编码标记；UnityWebRequest.text 会保留该字符，
                // Newtonsoft.Json 不会自动忽略它，因此在不改变正文空白语义的前提下只移除开头 BOM。
                content = RemoveLeadingByteOrderMark(content);

                try
                {
                    return JsonConvert.DeserializeObject<HotAssetsManifest>(content);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"远端资源 Manifest 解析失败，模块：{ModuleName}，异常：{exception}");
                    return null;
                }
            }
        }

        internal static string RemoveLeadingByteOrderMark(string content)
        {
            return !string.IsNullOrEmpty(content) && content[0] == '\uFEFF'
                ? content.Substring(1)
                : content;
        }

        /// <summary>
        /// 将 WebGL Manifest 中受约束的相对资源根解析为当前 Player/CDN 根地址。
        /// 正式发布的绝对 HTTP(S) 地址保持不变；相对地址只用于同源、自包含部署。
        /// </summary>
        internal static string ResolveBrowserDownloadUrl(string configuredRoot, string declaredDownloadUrl)
        {
            if (string.IsNullOrWhiteSpace(declaredDownloadUrl))
                return declaredDownloadUrl;

            string declared = declaredDownloadUrl.Trim();
            if (Uri.TryCreate(declared, UriKind.Absolute, out Uri absoluteUri))
            {
                if (absoluteUri.Scheme != Uri.UriSchemeHttp && absoluteUri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException($"WebGL 下载地址必须使用 HTTP(S)：{declared}");
                return declared.TrimEnd('/');
            }

            if (declared.StartsWith("//", StringComparison.Ordinal) ||
                declared.IndexOf('\\') >= 0 ||
                declared.IndexOf('?') >= 0 ||
                declared.IndexOf('#') >= 0)
            {
                throw new InvalidDataException($"WebGL 相对下载地址包含不支持的格式：{declared}");
            }

            string[] segments = declared.Split('/');
            foreach (string segment in segments)
            {
                if (segment == "." || segment == "..")
                    throw new InvalidDataException($"WebGL 相对下载地址不能跨越目录边界：{declared}");
            }

            string root = configuredRoot?.Trim();
            if (!Uri.TryCreate(root?.TrimEnd('/') + "/", UriKind.Absolute, out Uri rootUri) ||
                (rootUri.Scheme != Uri.UriSchemeHttp && rootUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidDataException($"WebGL Player/CDN 根地址无效：{configuredRoot ?? "<empty>"}");
            }

            if (!Uri.TryCreate(rootUri, declared.TrimStart('/'), out Uri resolvedUri))
                throw new InvalidDataException($"无法解析 WebGL 相对下载地址：{declared}");
            return resolvedUri.AbsoluteUri.TrimEnd('/');
        }

        /// <summary>
        /// 读取上一次原子提交的 Manifest；损坏缓存不会继续参与加载。
        /// </summary>
        private async UniTask<HotAssetsManifest> LoadCachedManifestAsync()
        {
            if (!mMetadataStore.Exists(RemoteManifestCachePath))
                return null;

            try
            {
                string content = await mMetadataStore.ReadTextAsync(RemoteManifestCachePath);
                return JsonConvert.DeserializeObject<HotAssetsManifest>(content);
            }
            catch (Exception exception)
            {
                Debug.LogError($"远端资源 Manifest 缓存读取失败，模块：{ModuleName}，异常：{exception}");
                return null;
            }
        }

        /// <summary>
        /// 只读取上一次原子提交的 Native Manifest 并重建本地文件状态，不访问服务器，也不把模块标记为远端初始化完成。
        /// 正常 Remote 加载随后仍会刷新服务器 Manifest，避免一次状态查询冻结远端版本。
        /// </summary>
        internal async UniTask<bool> TryLoadCommittedManifestForLocalQueryAsync()
        {
            if (mActiveManifest != null)
                return true;
            if (UsesBrowserCache)
                return false;

            mCommitStrategy.RecoverVerifiedFile(
                RemoteManifestCachePath,
                $"{ModuleName}_remote_manifest_local_query_recovery",
                ModuleName,
                "RemoteManifest");

            HotAssetsManifest cachedManifest = await LoadCachedManifestAsync();
            if (!TryValidateManifest(cachedManifest, out string failureReason))
            {
                if (cachedManifest != null)
                {
                    Debug.LogWarning(
                        $"远端资源本地状态查询忽略无效 Manifest，模块：{ModuleName}，原因：{failureReason}");
                }
                return false;
            }

            mActiveManifest = cachedManifest;
            RebuildFileIndexes(cachedManifest, verifyIntegrity: false);
            return true;
        }

        /// <summary>
        /// 比对远端清单与本地缓存清单的最新补丁版本和完整文件身份；完全一致时才允许跳过全量 MD5 扫描。
        /// </summary>
        private async UniTask<bool> CachedManifestHasSameVersionAsync(HotAssetsManifest remoteManifest)
        {
            HotAssetsManifest cachedManifest = await LoadCachedManifestAsync();
            if (cachedManifest?.hotAssetsPatchList == null || cachedManifest.hotAssetsPatchList.Count == 0)
                return false;

            return HaveSameLatestPatchIdentity(cachedManifest, remoteManifest);
        }

        /// <summary>
        /// 比较两份 Manifest 最新补丁的稳定文件身份。
        /// 列表顺序不影响结果，但重复 Bundle 名、文件名变化或 MD5 变化都会强制重新校验磁盘内容。
        /// </summary>
        internal static bool HaveSameLatestPatchIdentity(
            HotAssetsManifest cachedManifest,
            HotAssetsManifest remoteManifest)
        {
            if (cachedManifest?.hotAssetsPatchList == null ||
                cachedManifest.hotAssetsPatchList.Count == 0 ||
                remoteManifest?.hotAssetsPatchList == null ||
                remoteManifest.hotAssetsPatchList.Count == 0)
            {
                return false;
            }

            HotAssetsPatch cachedLatestPatch =
                cachedManifest.hotAssetsPatchList[cachedManifest.hotAssetsPatchList.Count - 1];
            HotAssetsPatch remoteLatestPatch =
                remoteManifest.hotAssetsPatchList[remoteManifest.hotAssetsPatchList.Count - 1];
            if (cachedLatestPatch?.hotAssetsList == null ||
                remoteLatestPatch?.hotAssetsList == null ||
                cachedLatestPatch.patchVersion != remoteLatestPatch.patchVersion ||
                cachedLatestPatch.hotAssetsList.Count != remoteLatestPatch.hotAssetsList.Count)
            {
                return false;
            }

            Dictionary<string, string> cachedFiles =
                new Dictionary<string, string>(cachedLatestPatch.hotAssetsList.Count, StringComparer.Ordinal);
            foreach (HotFileInfo cachedFile in cachedLatestPatch.hotAssetsList)
            {
                if (cachedFile == null ||
                    string.IsNullOrWhiteSpace(cachedFile.abName) ||
                    string.IsNullOrWhiteSpace(cachedFile.md5) ||
                    !cachedFiles.TryAdd(cachedFile.abName, cachedFile.md5))
                {
                    return false;
                }
            }

            HashSet<string> remoteFileNames =
                new HashSet<string>(StringComparer.Ordinal);
            foreach (HotFileInfo remoteFile in remoteLatestPatch.hotAssetsList)
            {
                if (remoteFile == null ||
                    string.IsNullOrWhiteSpace(remoteFile.abName) ||
                    string.IsNullOrWhiteSpace(remoteFile.md5) ||
                    !remoteFileNames.Add(remoteFile.abName) ||
                    !cachedFiles.TryGetValue(remoteFile.abName, out string cachedMd5) ||
                    !string.Equals(cachedMd5, remoteFile.md5, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 验证应用版本、下载地址、补丁列表和文件摘要，避免异常数据进入下载层。
        /// </summary>
        private bool TryValidateManifest(HotAssetsManifest manifest, out string failureReason)
        {
            if (manifest == null)
            {
                failureReason = "服务端和本地均没有可用 Manifest";
                return false;
            }

            if (manifest.appVersion != "0.0.0" &&
                !string.Equals(manifest.appVersion, Application.version, StringComparison.Ordinal))
            {
                failureReason = $"Manifest 应用版本 {manifest.appVersion} 与当前版本 {Application.version} 不一致";
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.downLoadURL))
            {
                failureReason = "下载地址为空";
                return false;
            }

            if (manifest.hotAssetsPatchList == null || manifest.hotAssetsPatchList.Count == 0)
            {
                failureReason = "补丁列表为空";
                return false;
            }

            HotAssetsPatch latestPatch = manifest.hotAssetsPatchList[manifest.hotAssetsPatchList.Count - 1];
            if (latestPatch?.hotAssetsList == null)
            {
                failureReason = "最新补丁文件列表为空";
                return false;
            }

            foreach (HotFileInfo fileInfo in latestPatch.hotAssetsList)
            {
                if (fileInfo == null || string.IsNullOrWhiteSpace(fileInfo.abName) || string.IsNullOrWhiteSpace(fileInfo.md5))
                {
                    failureReason = "文件名称或 MD5 为空";
                    return false;
                }

                if (!AssetBundleNameValidator.TryValidateFileName(fileInfo.abName, out string fileNameFailure))
                {
                    failureReason = $"文件名称无效：{fileInfo.abName}，原因：{fileNameFailure}";
                    return false;
                }

                if (UsesBrowserCache)
                {
                    if (!string.Equals(manifest.targetPlatform, "WebGL", StringComparison.Ordinal))
                    {
                        failureReason = $"目标平台必须为 WebGL，实际为 {manifest.targetPlatform ?? "<empty>"}";
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(manifest.manifestId))
                    {
                        failureReason = "manifestId 为空";
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(fileInfo.bundleHash))
                    {
                        failureReason = $"Bundle 缺少 Hash：{fileInfo.abName}";
                        return false;
                    }
                    try
                    {
                        Hash128 hash = Hash128.Parse(fileInfo.bundleHash);
                        if (!hash.isValid)
                        {
                            failureReason = $"Bundle Hash 无效：{fileInfo.abName}";
                            return false;
                        }
                    }
                    catch (Exception)
                    {
                        failureReason = $"Bundle Hash 格式无效：{fileInfo.abName}";
                        return false;
                    }
                }
            }

            failureReason = null;
            return true;
        }

        /// <summary>
        /// 每次刷新都重建索引，避免重复检查导致列表无限增长或保留旧版本文件。
        /// verifyIntegrity 为 false 时只检查文件是否存在（清单同版本时本地文件已在上一轮通过完整性校验），
        /// 为 true 时逐文件计算 MD5；缺失或校验失败的文件进入待下载集合。
        /// </summary>
        private void RebuildFileIndexes(HotAssetsManifest manifest, bool verifyIntegrity)
        {
            mAllRemoteFiles.Clear();
            mFilesNeedingDownload.Clear();

            HotAssetsPatch latestPatch = manifest.hotAssetsPatchList[manifest.hotAssetsPatchList.Count - 1];
            Directory.CreateDirectory(AssetSavePath);

            foreach (HotFileInfo fileInfo in latestPatch.hotAssetsList)
            {
                mAllRemoteFiles[fileInfo.abName] = fileInfo;
                string localPath = Path.Combine(AssetSavePath, fileInfo.abName);
                // 读取文件前恢复兼容替换遗留的 backup，保证离线启动也能继续使用最后一个完整版本。
                mCommitStrategy.RecoverVerifiedFile(
                    localPath,
                    $"{ModuleName}_remote_bundle_recovery",
                    ModuleName,
                    fileInfo.abName);
                if (!File.Exists(localPath))
                {
                    mFilesNeedingDownload[fileInfo.abName] = fileInfo;
                    continue;
                }

                if (verifyIntegrity &&
                    !string.Equals(MD5.GetMd5FromFile(localPath), fileInfo.md5, StringComparison.OrdinalIgnoreCase))
                    mFilesNeedingDownload[fileInfo.abName] = fileInfo;
            }
        }

        private void RebuildBrowserFileIndexes(HotAssetsManifest manifest)
        {
            mAllRemoteFiles.Clear();
            mFilesNeedingDownload.Clear();
            HotAssetsPatch latestPatch = manifest.hotAssetsPatchList[manifest.hotAssetsPatchList.Count - 1];
            foreach (HotFileInfo fileInfo in latestPatch.hotAssetsList)
            {
                mAllRemoteFiles.Add(fileInfo.abName, fileInfo);
                // Unity 没有提供无请求的可靠缓存存在性查询；预下载请求会自行命中持久化缓存。
                mFilesNeedingDownload.Add(fileInfo.abName, fileInfo);
            }
        }

        internal bool TryCreateRemoteLocation(string bundleName, out AssetBundleLocation location)
        {
            if (mAllRemoteFiles.TryGetValue(bundleName, out HotFileInfo fileInfo))
            {
                location = new AssetBundleLocation(
                    ModuleName,
                    bundleName,
                    AssetBundleSourceKind.Remote,
                    WebGLAssetDownloadService.CombineUrl(DownloadUrl, bundleName),
                    fileInfo.bundleHash,
                    fileInfo.crc,
                    false);
                return true;
            }

            location = default;
            return false;
        }

        /// <summary>
        /// 同卷文件优先使用 File.Replace 完成原子替换；首次写入使用原子 Move。
        /// </summary>
        internal static void PromoteVerifiedFile(string stagingPath, string destinationPath)
        {
            AssetRuntimeBackendFactory.Current.CommitStrategy.PromoteVerifiedFile(
                stagingPath,
                destinationPath,
                $"compatibility_promote_{Guid.NewGuid():N}",
                "Compatibility",
                Path.GetFileName(destinationPath));
        }
    }
}
