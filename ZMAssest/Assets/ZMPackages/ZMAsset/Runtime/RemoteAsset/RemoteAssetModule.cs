using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 保存单个远端资源模块的 Manifest、缺失文件集合和初始化互斥状态。
    /// </summary>
    internal sealed class RemoteAssetModule
    {
        private readonly Dictionary<string, HotFileInfo> mAllRemoteFiles = new Dictionary<string, HotFileInfo>(StringComparer.Ordinal);

        private readonly Dictionary<string, HotFileInfo> mFilesNeedingDownload = new Dictionary<string, HotFileInfo>(StringComparer.Ordinal);

        private readonly Dictionary<string, SemaphoreSlim> mFileGates = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        private readonly object mFileGateLock = new object();

        private HotAssetsManifest mActiveManifest;

        internal RemoteAssetModule(string moduleName)
        {
            // 内部初始化入口也执行相同校验，防止绕过公开门面后生成越界或非法缓存路径。
            ZMAsset.ValidateModuleName(moduleName);
            ModuleName = moduleName;
            InitializationGate = new SemaphoreSlim(1, 1);
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
        /// 首次无缓存时允许较长网络等待；已有缓存时缩短超时，弱网或假死网络下快速回退本地清单。
        /// </summary>
        private const int FirstLoadManifestTimeoutSeconds = 30;
        private const int CachedManifestTimeoutSeconds = 5;

        /// <summary>
        /// 下载远端 Manifest；失败时回退到上一次成功提交的缓存，并重建缺失文件索引。
        /// </summary>
        internal async UniTask<bool> RefreshManifestAsync()
        {
            // 本地已有缓存清单时缩短网络等待，避免弱网设备首次加载最长阻塞一个完整超时周期。
            bool hasLocalCache = File.Exists(RemoteManifestCachePath);
            
            HotAssetsManifest manifest = await DownloadManifestAsync(hasLocalCache ? CachedManifestTimeoutSeconds : FirstLoadManifestTimeoutSeconds);
            
            if (TryValidateManifest(manifest, out string networkFailureReason))
            {
                mActiveManifest = manifest;
                // 远端清单与上次提交的缓存同版本时，本地文件在上次初始化已完成完整性校验，
                // 本次只做存在性检查即可发现缺失文件；版本变化才需要全量 MD5 扫描。
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

        /// <summary>
        /// 将已验证的服务端 Manifest 原子提交为下次启动可用的本地缓存。
        /// </summary>
        internal async UniTask CommitManifestAsync()
        {
            if (mActiveManifest == null)
                throw new InvalidOperationException($"模块 {ModuleName} 尚无可提交的远端 Manifest。");

            string json = JsonConvert.SerializeObject(mActiveManifest);
            string stagingPath = RemoteManifestCachePath + ".writing";
            await File.WriteAllTextAsync(stagingPath, json);
            PromoteVerifiedFile(stagingPath, RemoteManifestCachePath);
        }

        /// <summary>
        /// 下载并解析 Manifest；网络、协议和数据错误都返回失败，由调用方决定是否回退缓存。
        /// </summary>
        private async UniTask<HotAssetsManifest> DownloadManifestAsync(int timeoutSeconds)
        {
            string url =
                $"{BundleSettings.Instance.AssetBundleDownLoadUrl}/HotAssets/{ModuleName}/{BundleSettings.Instance.HotManifestName(ModuleName)}";

            Debug.Log($"远端资源 Manifest 下载，模块：{ModuleName}，地址：{url}，超时：{timeoutSeconds}s");
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = timeoutSeconds;
                await request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"远端资源 Manifest 下载失败，模块：{ModuleName}，错误：{request.error}");
                    return null;
                }

                string content = request.downloadHandler?.text;
                if (string.IsNullOrWhiteSpace(content))
                    return null;

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

        /// <summary>
        /// 读取上一次原子提交的 Manifest；损坏缓存不会继续参与加载。
        /// </summary>
        private async UniTask<HotAssetsManifest> LoadCachedManifestAsync()
        {
            if (!File.Exists(RemoteManifestCachePath))
                return null;

            try
            {
                string content = await File.ReadAllTextAsync(RemoteManifestCachePath);
                return JsonConvert.DeserializeObject<HotAssetsManifest>(content);
            }
            catch (Exception exception)
            {
                Debug.LogError($"远端资源 Manifest 缓存读取失败，模块：{ModuleName}，异常：{exception}");
                return null;
            }
        }

        /// <summary>
        /// 比对远端清单与本地缓存清单的最新补丁版本；一致时允许跳过本地文件的全量 MD5 扫描。
        /// </summary>
        private async UniTask<bool> CachedManifestHasSameVersionAsync(HotAssetsManifest remoteManifest)
        {
            HotAssetsManifest cachedManifest = await LoadCachedManifestAsync();
            if (cachedManifest?.hotAssetsPatchList == null || cachedManifest.hotAssetsPatchList.Count == 0)
                return false;

            HotAssetsPatch cachedLatestPatch = cachedManifest.hotAssetsPatchList[cachedManifest.hotAssetsPatchList.Count - 1];
            HotAssetsPatch remoteLatestPatch = remoteManifest.hotAssetsPatchList[remoteManifest.hotAssetsPatchList.Count - 1];
            if (cachedLatestPatch?.hotAssetsList == null || remoteLatestPatch?.hotAssetsList == null)
                return false;

            // 版本号与文件数量双重比对，防止同版本号下清单被重新发布导致漏扫。
            return cachedLatestPatch.patchVersion == remoteLatestPatch.patchVersion &&
                   cachedLatestPatch.hotAssetsList.Count == remoteLatestPatch.hotAssetsList.Count;
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

                if (fileInfo.abName.IndexOf('/') >= 0 || fileInfo.abName.IndexOf('\\') >= 0 || fileInfo.abName.Contains(".."))
                {
                    failureReason = $"文件名称包含非法路径片段：{fileInfo.abName}";
                    return false;
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

        /// <summary>
        /// 同卷文件优先使用 File.Replace 完成原子替换；首次写入使用原子 Move。
        /// </summary>
        internal static void PromoteVerifiedFile(string stagingPath, string destinationPath)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
                Directory.CreateDirectory(destinationDirectory);

            if (!File.Exists(stagingPath))
                throw new FileNotFoundException("待提交的远端资源临时文件不存在。", stagingPath);

            if (!File.Exists(destinationPath))
            {
                File.Move(stagingPath, destinationPath);
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
                Debug.LogWarning($"当前平台不支持直接替换文件，将使用可回滚切换：{replaceException.Message}");
                PromoteWithRollback(stagingPath, destinationPath, backupPath);
                return;
            }

            // 备份清理失败不影响已完成的原子提交，只保留诊断并由后续切换覆盖。
            try
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
            }
            catch (Exception cleanupException)
            {
                Debug.LogWarning($"远端资源旧文件备份清理失败：{backupPath}，异常：{cleanupException.Message}");
            }
        }

        /// <summary>
        /// File.Replace 不可用时采用备份、移动和失败恢复，保证旧文件不会因切换失败而丢失。
        /// </summary>
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
    }
}
