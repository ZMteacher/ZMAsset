/*---------------------------------------------------------------------------------------------------------------------------------------------
*
* Title: ZMAsset
*
* Description: 可视化多模块打包器、多模块热更、多线程下载、多版本热更、多版本回退、加密、解密、内嵌、解压、内存引用计数、大型对象池、AssetBundle加载、Editor加载
*
* Author: ZM
*
* Date: 2023.4.13
*
* Modify: 
------------------------------------------------------------------------------------------------------------------------------------------------*/
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 热更新模块的只读状态快照，避免业务层直接操作 HotAssetsModule 内部生命周期。
    /// </summary>
    public sealed class HotAssetsModuleState
    {
        public string BundleModule { get; internal set; }
        public bool IsHotUpdateRunning { get; internal set; }
        public bool IsAssetModuleInitialized { get; internal set; }
        public int HotAssetCount { get; internal set; }
        public int NeedDownloadAssetCount { get; internal set; }
        public float DownloadedSizeM { get; internal set; }
    }

    /// <summary>
    /// 热更资源模块
    /// </summary>
    public class HotAssetsModule
    {
        /// <summary>
        /// 当前应用版本
        /// </summary>
        private string mAppVersion;
        /// <summary>
        /// 热更资源下载储存路径
        /// </summary>
        public string HotAssetsSavePath { get { return Application.persistentDataPath + "/HotAssets/" + CurBundleModuleName + "/"; } }
        /// <summary>
        /// 所有热更的资源列表
        /// </summary>
        public List<HotFileInfo> mAllHotAssetsList = new List<HotFileInfo>();
        /// <summary>
        /// 需要下载的资源列表
        /// </summary>
        public List<HotFileInfo> mNeedDownLoadAssetsList = new List<HotFileInfo>();
        /// <summary>
        /// 服务端资源清单
        /// </summary>
        private HotAssetsManifest mServerHotAssetsManifest;
        /// <summary>
        /// 本地资源清单
        /// </summary>
        private HotAssetsManifest mLocalHotAssetsManifest;
        /// <summary>
        /// 服务端资源热更清单储存路径
        /// </summary>
        private string mServerHotAssetsManifestPath;
        /// <summary>
        /// 本地资源热更清单文件储存路径
        /// </summary>
        private string mLocalHotAssetManifestPath;
        /// <summary>
        /// 热更公告
        /// </summary>
        public string UpdateNoticeContent { get { return mServerHotAssetsManifest?.updateNotice; } }
        /// <summary>
        /// 当前下载的资源模块类型
        /// </summary>
        public string CurBundleModuleName { get; set; }
        /// <summary>
        /// 最大下载资源大小
        /// </summary>
        public float AssetsMaxSizeM { get; set; }
        /// <summary>
        /// 资源已经下载的大小
        /// </summary>
        private long mAssetsDownloadedBytes;
        /// <summary>
        /// 下载进度由整数字节原子累计，再在读取时换算为 MB，避免多线程 float 加法丢失更新。
        /// </summary>
        public float AssetsDownLoadSizeM => Interlocked.Read(ref mAssetsDownloadedBytes) / 1024.0f / 1024.0f;
        /// <summary>
        /// 资源下载器
        /// </summary>
        private IAssetDownloadBatch mAssetsDownLoader;

        /// <summary>
        /// 同一模块实例固定使用同一个平台后端，避免一次事务中途混用不同的下载、元数据或提交实现。
        /// </summary>
        private readonly IAssetDownloadService mDownloadService;
        private readonly IAssetMetadataStore mMetadataStore;
        private readonly IHotUpdateCommitStrategy mCommitStrategy;
        private readonly AssetRuntimePlatformKind mPlatformKind;
        private bool UsesWebGLVersionPointer => mPlatformKind == AssetRuntimePlatformKind.WebGL;
        /// <summary>
        /// 当前热更新事务的临时快照、回滚快照和 Manifest 临时文件。
        /// </summary>
        private HotUpdateCommitContext mTransactionContext;
        /// <summary>
        /// AssetBundle配置文件下载完成监听
        /// </summary>
        public Action<string> OnDownLoadABConfigListener;
        /// <summary>
        /// 下载AssetBundle完成的回调
        /// </summary>
        public Action<string> OnDownLoadAssetBundleListener;
        /// <summary>
        /// 所有热更资源的一个长度
        /// </summary>
        public int HotAssetCount { get { return mAllHotAssetsList.Count; } }
        
        private MonoBehaviour mMono;
        /// <summary>
        /// 下载所有资源完成的回调
        /// </summary>
        public Action<string> OnDownLoadAllAssetsFinish;
        /// <summary>
        /// 整批资源下载失败回调，失败时不会更新本地热更清单。
        /// </summary>
        public Action<string, HotFileInfo> OnDownLoadAllAssetsFailed;
        /// <summary>
        /// 直接调用模块接口时等待本次操作完成的回调集合。
        /// </summary>
        private readonly List<Action<string>> mPendingHotFinishCallbacks = new List<Action<string>>();
        private bool mIsHotUpdateRunning;
        /// <summary>
        /// 当前模块是否正在执行热更新；只读暴露给状态快照使用。
        /// </summary>
        internal bool IsHotUpdateRunning => mIsHotUpdateRunning;
        /// <summary>
        /// 协调模式只准备并校验临时快照，不允许模块自行提交或初始化。
        /// </summary>
        private bool mIsCoordinatedTransaction;
        private bool mCoordinatedHasChanges;
        private bool mWasInitializedBeforeCoordinatedTransaction;
        private Action<HotAssetsModule, bool> mCoordinatedPreparedCallback;
        private Action<HotAssetsModule, HotFileInfo, Exception> mCoordinatedFailedCallback;
        private int mCoordinatedOperationId;
        public HotAssetsModule(string bundleModule,MonoBehaviour mono)
        {
            AssetRuntimeBackend backend = AssetRuntimeBackendFactory.Current;
            mDownloadService = backend.DownloadService;
            mMetadataStore = backend.MetadataStore;
            mCommitStrategy = backend.CommitStrategy;
            mPlatformKind = backend.PlatformKind;
            mMono = mono;
            CurBundleModuleName = bundleModule;
            mAppVersion = Application.version;
        }
        /// <summary>
        /// 开始热更资源
        /// </summary>
        /// <param name="startDownLoadCallback">开始下载的回调</param>
        /// <param name="hotFinish">热更完成回调</param>
        /// <param name="isCheckAssetsVersion">是否检测资源版本</param>
        /// <summary>
        /// 旧单模块入口：模块自行完成版本检查、下载、提交和初始化。
        /// 多模块场景由 PrepareForCoordinatedTransaction 拆开这些阶段。
        /// </summary>
        public void StartHotAssets(Action startDownLoadCallback,Action<string> hotFinish=null,bool isCheckAssetsVersion=true)
        {
            if (hotFinish != null)
                mPendingHotFinishCallbacks.Add(hotFinish);
            // 模块内部同样只允许一个真实任务，后续调用仅加入完成等待列表。
            if (mIsHotUpdateRunning)
                return;
            mIsHotUpdateRunning = true;
            if (isCheckAssetsVersion)
            {
                // 版本有变化才创建 staging；版本检查异常由异步方法统一进入失败收口。
                StartHotAssetsAfterVersionCheckAsync(startDownLoadCallback).Forget();
            }
            else
            {
                // 调用方显式跳过版本检查时，仍必须提供此前成功取得的清单，不能在未知状态下直接启动下载。
                if (mServerHotAssetsManifest == null)
                {
                    mIsHotUpdateRunning = false;
                    DownLoadAssetBundleFailed(null);
                    return;
                }

                StartDownLoadHotAssets(startDownLoadCallback);
            }
        }

        /// <summary>
        /// 为显式多模块事务准备当前模块；成功回调只表示“可提交”，不表示正式资源已经变化。
        /// </summary>
        internal void PrepareForCoordinatedTransaction(
            Action startDownloadCallback,
            Action<HotAssetsModule, bool> preparedCallback,
            Action<HotAssetsModule, HotFileInfo, Exception> failedCallback,
            bool isCheckAssetsVersion)
        {
            if (mIsHotUpdateRunning)
                throw new InvalidOperationException($"模块 {CurBundleModuleName} 已有热更新任务正在执行。");

            mIsHotUpdateRunning = true;
            mIsCoordinatedTransaction = true;
            mCoordinatedHasChanges = false;
            mWasInitializedBeforeCoordinatedTransaction =
                AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName);
            mCoordinatedPreparedCallback = preparedCallback;
            mCoordinatedFailedCallback = failedCallback;
            int operationId = ++mCoordinatedOperationId;

            if (!isCheckAssetsVersion)
            {
                // 多模块显式跳过版本检查同样要求已有可信清单，否则准备阶段必须失败并触发整组回滚。
                if (mServerHotAssetsManifest == null)
                {
                    mIsHotUpdateRunning = false;
                    mCoordinatedFailedCallback?.Invoke(
                        this,
                        null,
                        new HotUpdateVersionCheckException(
                            CurBundleModuleName,
                            "MissingManifest",
                            $"模块 {CurBundleModuleName} 跳过版本检查时没有可用的已验证清单。"));
                    return;
                }

                StartDownLoadHotAssets(startDownloadCallback);
                return;
            }

            // 多模块事务同样直接等待版本检查，避免回调异常导致协调器只能等待超时。
            PrepareCoordinatedTransactionAfterVersionCheckAsync(
                startDownloadCallback,
                operationId).Forget();
        }
        /// <summary>
        /// 开始下载热更资源
        /// </summary>
        /// <param name="startDonwLoadCallBack"></param>
        /// <summary>
        /// 创建模块 staging 并启动下载；传入回调只用于通知“开始下载”，不是提交完成。
        /// </summary>
        public void StartDownLoadHotAssets(Action startDonwLoadCallBack)
        {
            // 每次真实下载都从零开始统计，重复热更同一模块不会沿用上一次进度。
            Interlocked.Exchange(ref mAssetsDownloadedBytes, 0L);
            //优先下载AssetBUndle配置文件，下载完成后呢，调用回调，让开发者及时加载配置文件
            //热更资源下载完成之后同样给与回调，供开发者动态加载刚下载完成的资源
            List<HotFileInfo> downLoadList = new List<HotFileInfo>();
            for (int i = 0; i < mNeedDownLoadAssetsList.Count; i++)
            {
                HotFileInfo hotFile = mNeedDownLoadAssetsList[i];
                //如果包含Config 说明是配置文件，需要优先下载
                if (hotFile.abName.Contains("config"))
                {
                    downLoadList.Insert(0, hotFile);
                }
                else
                {
                    downLoadList.Add(hotFile);
                }
            }
            //获取资源下载队列
            Queue<HotFileInfo> downLoadQueue = new Queue<HotFileInfo>();
            foreach (var item in downLoadList)
            {
                downLoadQueue.Enqueue(item);
            }

            PrepareAndStartDownloadAsync(downLoadQueue, startDonwLoadCallBack).Forget();

        }

        private async UniTask PrepareAndStartDownloadAsync(
            Queue<HotFileInfo> downLoadQueue,
            Action startDownloadCallback)
        {
            try
            {
                // Native creates a staging directory; WebGL creates a metadata-only candidate context.
                await PrepareHotUpdateTransactionAsync();
                mAssetsDownLoader = mDownloadService.CreateBatch(new AssetDownloadBatchRequest
                {
                    OperationId = mTransactionContext.TransactionId,
                    ModuleName = CurBundleModuleName,
                    DownloadQueue = downLoadQueue,
                    DownloadUrl = mServerHotAssetsManifest.downLoadURL,
                    SavePath = mTransactionContext.StagingSnapshotPath,
                    BytesDownloaded = AddDownloadedBytes,
                    DownloadSucceeded = DownLoadAssetBundleSuccess,
                    DownloadFailed = DownLoadAssetBundleFailed,
                    BatchFinished = HandleDownloadBatchFinished
                });
                startDownloadCallback?.Invoke();
                mAssetsDownLoader.Start();
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 创建热更新候选快照失败：{exception}");
                DownLoadAssetBundleFailed(mNeedDownLoadAssetsList.Count > 0 ? mNeedDownLoadAssetsList[0] : null);
            }
        }
        /// <summary>
        /// 检测资源版本；下载服务端清单、比对版本并计算需要热更的文件列表。
        /// </summary>
        /// <returns>是否需要热更，以及需要下载的热更大小（MB）。</returns>
        public async UniTask<(bool isHot, float sizeMb)> CheckAssetsVersionAsync()
        {
            //生成热更清单路径
            GeneratorHotAssetsManifest();

            // 每次请求前清空上一次成功请求留下的对象，网络失败时绝不能继续复用旧清单得出错误结论。
            mServerHotAssetsManifest = null;

            try
            {
                // 版本比较前先恢复上次中断事务，避免用半切换的 Manifest 计算补丁差异。
                await RecoverInterruptedTransactionIfNeededAsync();
            }
            catch (Exception exception)
            {
                // 恢复失败意味着正式目录和事务材料的关系无法确认，必须失败关闭，不能继续联网后误判为无更新。
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "Recovery",
                    $"模块 {CurBundleModuleName} 恢复上次热更新事务失败。",
                    exception);
            }
            mNeedDownLoadAssetsList.Clear();
            // 每次版本检测都重新构造服务端完整文件集合，禁止历史版本文件残留。
            mAllHotAssetsList.Clear();

            await DownLoadHotAssetsManifestAsync();

            //资源清单下载完成
            //1.检测当前版本是否需要热更
            if (CheckModuleAssetsIsHot())
            {
                HotAssetsPatch serverHotPath = mServerHotAssetsManifest.hotAssetsPatchList[^1];
                bool isNeedHot = ComputeNeedHotAssetsList(serverHotPath);
                return isNeedHot ? (true, AssetsMaxSizeM) : (false, 0f);
            }
            return (false, 0f);
        }

        /// <summary>
        /// 单模块版本检查完成后的异步分支；成功结果直接决定下载或初始化路径。
        /// </summary>
        private async UniTask StartHotAssetsAfterVersionCheckAsync(Action startDownloadCallback)
        {
            try
            {
                (bool isHot, _) = await CheckAssetsVersionAsync();
                if (isHot)
                {
                    // 版本有变化：创建临时快照并开始下载，正式目录此时仍保持旧版本。
                    StartDownLoadHotAssets(startDownloadCallback);
                    return;
                }

                // 没有补丁：不创建事务，但仍确保当前磁盘配置已初始化。
                await InitializeCurrentConfigurationAndNotifyAsync();
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 版本检查失败：{exception}");
                // 版本检查失败不能伪装成“无需热更”，否则业务会继续使用未知版本资源。
                DownLoadAssetBundleFailed(null);
            }
        }

        /// <summary>
        /// 多模块事务版本检查完成后的异步分支；旧操作返回时通过 operationId 丢弃结果。
        /// </summary>
        private async UniTask PrepareCoordinatedTransactionAfterVersionCheckAsync(
            Action startDownloadCallback,
            int operationId)
        {
            try
            {
                (bool isHot, _) = await CheckAssetsVersionAsync();

                // 版本请求本身支持异步等待；旧请求延迟返回时必须忽略，不能污染下一次事务。
                if (!mIsCoordinatedTransaction ||
                    !mIsHotUpdateRunning ||
                    operationId != mCoordinatedOperationId)
                {
                    return;
                }

                if (isHot)
                {
                    StartDownLoadHotAssets(startDownloadCallback);
                    return;
                }

                // 无版本变化的模块仍参与统一初始化顺序，但不会创建或切换磁盘快照。
                mCoordinatedPreparedCallback?.Invoke(this, false);
            }
            catch (Exception exception)
            {
                if (!mIsCoordinatedTransaction ||
                    !mIsHotUpdateRunning ||
                    operationId != mCoordinatedOperationId)
                {
                    return;
                }

                mIsHotUpdateRunning = false;
                mCoordinatedFailedCallback?.Invoke(this, null, exception);
            }
        }
        /// <summary>
        /// 计算需要热更的文件列表
        /// </summary>
        /// <param name="serverAssetsPath"></param>
        /// <returns></returns>
        public bool ComputeNeedHotAssetsList(HotAssetsPatch serverAssetsPath)
        {
            if (UsesWebGLVersionPointer)
            {
                AssetsMaxSizeM = 0;
                foreach (HotFileInfo item in serverAssetsPath.hotAssetsList)
                {
                    mAllHotAssetsList.Add(item);
                    // Unity Cache has no reliable non-request existence query. Preparing the complete
                    // candidate snapshot lets cached objects short-circuit in the browser and repairs eviction.
                    mNeedDownLoadAssetsList.Add(item);
                    AssetsMaxSizeM += item.size / 1024f;
                }
                return mNeedDownLoadAssetsList.Count > 0;
            }

            if (!Directory.Exists(HotAssetsSavePath))
            {
                Directory.CreateDirectory(HotAssetsSavePath);
            }
            if(File.Exists(mLocalHotAssetManifestPath))
                mLocalHotAssetsManifest = JsonConvert.DeserializeObject<HotAssetsManifest>(File.ReadAllText(mLocalHotAssetManifestPath));
            AssetsMaxSizeM = 0;
            foreach (var item in serverAssetsPath.hotAssetsList)
            {
                //获取本地AssetBundle文件路径
                string localHotFilePath = HotAssetsSavePath + item.abName;
                //获取本地解压后的AssetBundle文件路径
                string localCompressFilePath = BundleSettings.Instance.GetAssetsDecompressPath(CurBundleModuleName)+ item.abName;
                mAllHotAssetsList.Add(item);
                //如果本地热更文件不存在，或者本地文件与服务端不一致 就需要热更
                if (!File.Exists(localHotFilePath) ||item.md5!= MD5.GetMd5FromFile(localHotFilePath))//验证资源是否损、是否需要热更坏或被篡改
                {
                    //检测本地内嵌解压后的资源是否存在，进行二次验证，如仍不一致，则需要确定热更
                    if (!File.Exists(localCompressFilePath) || item.md5 != MD5.GetMd5FromFile(localCompressFilePath))
                    {
                        mNeedDownLoadAssetsList.Add(item);
                        AssetsMaxSizeM += item.size / 1024f;
                    }
                }
            }
            
            return mNeedDownLoadAssetsList.Count > 0;
        }
        /// <summary>
        /// 检测模块资源是否需要热更
        /// </summary>
        /// <returns></returns>
        public bool CheckModuleAssetsIsHot()
        {
            //如果服务端资源清单不存，不需要热更
            if (mServerHotAssetsManifest==null)
            {
                return false;   
            }
            //资源应用版本不一致
            if (mServerHotAssetsManifest.appVersion!=mAppVersion && mServerHotAssetsManifest.appVersion!="0.0.0")
            {
                Debug.Log($"应用版本不一致，{CurBundleModuleName} 不需要热更");
                return false;
            }
            //全版本生效热更
            if ( mServerHotAssetsManifest.appVersion=="0.0.0")
            {
                Debug.Log($"{CurBundleModuleName} 属于全版本热更资源，计算热更需要下载的文件");
            }

            // 服务端合法清单可以没有补丁；此时必须直接确认无更新，避免后续访问空列表的最后一项。
            if (mServerHotAssetsManifest.hotAssetsPatchList.Count == 0)
            {
                return false;
            }

            if (UsesWebGLVersionPointer)
            {
                if (!WebGLActiveAssetRegistry.TryGetManifest(CurBundleModuleName, out HotAssetsManifest activeManifest) ||
                    activeManifest?.hotAssetsPatchList == null ||
                    activeManifest.hotAssetsPatchList.Count == 0)
                    return true;
                HotAssetsPatch activePatch = activeManifest.hotAssetsPatchList[activeManifest.hotAssetsPatchList.Count - 1];
                HotAssetsPatch candidatePatch = mServerHotAssetsManifest.hotAssetsPatchList[mServerHotAssetsManifest.hotAssetsPatchList.Count - 1];
                return activePatch == null || candidatePatch == null ||
                       activePatch.patchVersion != candidatePatch.patchVersion ||
                       !string.Equals(activeManifest.manifestId, mServerHotAssetsManifest.manifestId, StringComparison.Ordinal);
            }

            //如果本地资源清单文件不存在，说明我们需要热更
            if (!File.Exists(mLocalHotAssetManifestPath))
            {
                return true;
            }
            //判断本地资源清单补丁版本号是否与服务端资源清单补丁版本号一致，如果一致，不需要热更， 如果不一致，则需要热更
            HotAssetsManifest localHotAssetsManifest = JsonConvert.DeserializeObject<HotAssetsManifest>(File.ReadAllText(mLocalHotAssetManifestPath));
            if (localHotAssetsManifest == null || localHotAssetsManifest.hotAssetsPatchList == null)
            {
                // 本地清单损坏时按需要热更处理，让后续事务重新生成可用的本地状态。
                return true;
            }
            if (localHotAssetsManifest.hotAssetsPatchList.Count==0 && mServerHotAssetsManifest.hotAssetsPatchList.Count!=0)
            {
                return true;
            }
         
            //获取本地热更补丁的最后一个补丁
            HotAssetsPatch localHotPatch = localHotAssetsManifest.hotAssetsPatchList[^1];
            //获取服务端热更补丁的最后一个补丁
            HotAssetsPatch serverHotPatch = mServerHotAssetsManifest.hotAssetsPatchList[^1];

            if (localHotPatch!=null&& serverHotPatch!=null)
            {
                if (localHotPatch.patchVersion!=serverHotPatch.patchVersion)
                {
                    return true;
                }
                // 补丁版本一致时不需要热更，避免每次启动触发全量 MD5 比对；中断事务已由 CheckAssetsVersion 前置的 RecoverInterruptedTransactionIfNeeded 恢复，此时本地清单即真实状态。注意：版本一致期间不做热更文件完整性自愈（损坏/缺失需等待下一次版本提升）。
                return false;
            }

            if (serverHotPatch!=null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        /// <summary>
        /// 下载资源热更清单
        /// </summary>
        /// <returns></returns>
        private async UniTask DownLoadHotAssetsManifestAsync()
        {
            string url = $"{BundleSettings.Instance.AssetBundleDownLoadUrl}/HotAssets/{CurBundleModuleName}/{BundleSettings.Instance.HotManifestName(CurBundleModuleName)}";
            Debug.Log($"*** Request AssetBundle HotAssetsMainfest Url Start Module:{CurBundleModuleName} url:{url}");

            using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
            {
                webRequest.timeout = 30;
                try
                {
                    // UnityWebRequest 的异步等待只负责等待请求结束，HTTP 状态和数据内容仍需要显式校验。
                    await webRequest.SendWebRequest();
                }
                catch (Exception exception)
                {
                    throw new HotUpdateVersionCheckException(CurBundleModuleName, "Network", $"模块 {CurBundleModuleName} 请求热更清单失败。", exception);
                }

#if UNITY_2020_1_OR_NEWER
                if (webRequest.result != UnityWebRequest.Result.Success)
#else
                if (webRequest.isNetworkError || webRequest.isHttpError)
#endif
                {
                    string error = string.IsNullOrEmpty(webRequest.error) ? "远端服务器未返回成功状态。" : webRequest.error;

                    throw new HotUpdateVersionCheckException(CurBundleModuleName, "Http", $"模块 {CurBundleModuleName} 请求热更清单失败：{error}");
                }

                string downLoadContent = webRequest.downloadHandler?.text;
                if (string.IsNullOrWhiteSpace(downLoadContent))
                {
                    throw new HotUpdateVersionCheckException(CurBundleModuleName, "EmptyResponse", $"模块 {CurBundleModuleName} 收到空的热更清单响应。");
                }

                await ApplyDownloadedManifestContentAsync(downLoadContent);
            }
        }

        /// <summary>
        /// 对远端清单执行解析、结构校验和缓存原子写入，保证所有来源遵循同一条失败关闭路径。
        /// </summary>
        private async UniTask ApplyDownloadedManifestContentAsync(string downLoadContent)
        {
            if (string.IsNullOrWhiteSpace(downLoadContent))
            {
                throw new HotUpdateVersionCheckException(CurBundleModuleName, "EmptyResponse", $"模块 {CurBundleModuleName} 收到空的热更清单响应。");
            }

            HotAssetsManifest downloadedManifest;
            try
            {
                // 先检查关键字段是否真实存在，避免模型字段初始化器把缺失字段伪装成空列表。
                JObject manifestObject = JObject.Parse(downLoadContent);
                ValidateManifestJsonStructure(manifestObject);
                downloadedManifest = manifestObject.ToObject<HotAssetsManifest>();
            }
            catch (HotUpdateVersionCheckException)
            {
                // 结构校验已经生成了精确失败类别，原样向上抛出，避免覆盖诊断上下文。
                throw;
            }
            catch (Exception exception)
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单不是合法 JSON。",
                    exception);
            }

            ValidateDownloadedManifest(downloadedManifest);

            // 服务端清单缓存只用于诊断和下次排查，不作为本次版本判断的权威来源；写缓存失败不能改变已确认的远端结果。
            string manifestStagingPath = mServerHotAssetsManifestPath + ".writing";
            try
            {
                if (UsesWebGLVersionPointer)
                {
                    await mMetadataStore.WriteTextAtomicallyAsync(mServerHotAssetsManifestPath, downLoadContent);
                }
                else
                {
                    await mMetadataStore.WriteTextAsync(manifestStagingPath, downLoadContent);
                    string operationId = $"{CurBundleModuleName}_manifest_{Guid.NewGuid():N}";
                    mCommitStrategy.PromoteVerifiedFile(
                        manifestStagingPath,
                        mServerHotAssetsManifestPath,
                        operationId,
                        CurBundleModuleName,
                        "ServerManifest");
                }
            }
            catch (Exception exception)
            {
                if (!UsesWebGLVersionPointer)
                    TryDeleteManifestStagingFile(manifestStagingPath);
                Debug.LogWarning(
                    $"模块 {CurBundleModuleName} 远端热更清单已校验成功，但本地缓存写入失败：{exception.Message}");
            }

            // 只有合法清单才允许进入后续版本比较和下载事务。
            mServerHotAssetsManifest = downloadedManifest;
            Debug.Log($"*** Request AssetBundle HotAssetsMainfest Url Finish Module:{CurBundleModuleName}");
        }

        /// <summary>
        /// 校验 JSON 中的关键数组字段，区分“合法空列表”和“服务端漏字段”。
        /// </summary>
        private void ValidateManifestJsonStructure(JObject manifestObject)
        {
            if (manifestObject == null)
            {
                throw new HotUpdateVersionCheckException(CurBundleModuleName, "InvalidManifest", $"模块 {CurBundleModuleName} 的热更清单 JSON 对象为空。");
            }

            JToken patchListToken = manifestObject.GetValue("hotAssetsPatchList", StringComparison.OrdinalIgnoreCase);
            if (patchListToken == null || patchListToken.Type != JTokenType.Array)
            {
                throw new HotUpdateVersionCheckException(CurBundleModuleName, "InvalidManifest", $"模块 {CurBundleModuleName} 的热更清单缺少有效的补丁列表。");
            }

            foreach (JToken patchToken in patchListToken)
            {
                if (patchToken == null || patchToken.Type != JTokenType.Object)
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的热更清单包含非法补丁项。");
                }

                JObject patchObject = (JObject)patchToken;
                JToken patchVersionToken = patchObject.GetValue("patchVersion", StringComparison.OrdinalIgnoreCase);
                if (patchVersionToken == null ||
                    patchVersionToken.Type != JTokenType.Integer ||
                    patchVersionToken.Value<long>() < 0)
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的热更清单补丁缺少有效的 patchVersion。");
                }

                JToken assetListToken = patchObject.GetValue("hotAssetsList", StringComparison.OrdinalIgnoreCase);
                if (assetListToken == null || assetListToken.Type != JTokenType.Array)
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的热更清单补丁缺少有效资源列表。");
                }

                foreach (JToken assetToken in assetListToken)
                {
                    if (assetToken == null || assetToken.Type != JTokenType.Object)
                    {
                        throw new HotUpdateVersionCheckException(
                            CurBundleModuleName,
                            "InvalidManifest",
                            $"模块 {CurBundleModuleName} 的热更清单包含非法资源项。");
                    }

                    JObject assetObject = (JObject)assetToken;
                    JToken nameToken = assetObject.GetValue("abName", StringComparison.OrdinalIgnoreCase);
                    JToken md5Token = assetObject.GetValue("md5", StringComparison.OrdinalIgnoreCase);
                    JToken sizeToken = assetObject.GetValue("size", StringComparison.OrdinalIgnoreCase);
                    if (nameToken == null || nameToken.Type != JTokenType.String ||
                        md5Token == null || md5Token.Type != JTokenType.String ||
                        sizeToken == null ||
                        (sizeToken.Type != JTokenType.Integer && sizeToken.Type != JTokenType.Float))
                    {
                        throw new HotUpdateVersionCheckException(
                            CurBundleModuleName,
                            "InvalidManifest",
                            $"模块 {CurBundleModuleName} 的热更清单资源项缺少有效的 abName、md5 或 size。");
                    }
                }
            }
        }

        /// <summary>
        /// 校验清单的最小运行结构，避免后续按补丁索引时出现空引用或把损坏响应当作无更新。
        /// </summary>
        private void ValidateDownloadedManifest(HotAssetsManifest manifest)
        {
            if (manifest == null)
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单反序列化结果为空。");
            }

            if (string.IsNullOrWhiteSpace(manifest.appVersion))
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单缺少应用版本。");
            }

            if (UsesWebGLVersionPointer)
            {
                if (!string.Equals(manifest.targetPlatform, "WebGL", StringComparison.Ordinal))
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的 WebGL 热更清单目标平台无效：{manifest.targetPlatform ?? "<empty>"}。");
                if (string.IsNullOrWhiteSpace(manifest.manifestId))
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的 WebGL 热更清单缺少 manifestId。");
            }

            if (manifest.hotAssetsPatchList == null)
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单缺少补丁列表。");
            }

            bool hasAssets = false;
            int previousPatchVersion = -1;
            HashSet<string> assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < manifest.hotAssetsPatchList.Count; index++)
            {
                HotAssetsPatch patch = manifest.hotAssetsPatchList[index];
                if (patch == null || patch.hotAssetsList == null)
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的热更清单包含损坏的补丁项。");
                }

                if (patch.patchVersion < 0 || patch.patchVersion <= previousPatchVersion)
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的热更清单补丁版本必须为非负值且严格递增。");
                }

                previousPatchVersion = patch.patchVersion;

                hasAssets |= patch.hotAssetsList.Count > 0;
                for (int assetIndex = 0; assetIndex < patch.hotAssetsList.Count; assetIndex++)
                {
                    HotFileInfo hotFile = patch.hotAssetsList[assetIndex];
                    ValidateHotFileInfo(hotFile, assetNames);
                }
            }

            if (hasAssets && string.IsNullOrWhiteSpace(manifest.downLoadURL))
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单缺少资源下载地址。");
            }

            Uri downloadUri = null;
            if (hasAssets && !Uri.TryCreate(manifest.downLoadURL, UriKind.Absolute, out downloadUri))
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单下载地址不是合法绝对地址。");
            }

            if (hasAssets &&
                !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单下载地址协议不受支持：{downloadUri.Scheme}。");
            }
        }

        /// <summary>
        /// 校验单个热更文件，防止清单中的路径和校验字段影响本地文件系统边界。
        /// </summary>
        private void ValidateHotFileInfo(HotFileInfo hotFile, HashSet<string> assetNames)
        {
            if (hotFile == null)
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单包含空文件项。");
            }

            if (string.IsNullOrWhiteSpace(hotFile.abName) || hotFile.abName != hotFile.abName.Trim())
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更文件名为空或包含首尾空白。");
            }

            if (Path.IsPathRooted(hotFile.abName) ||
                hotFile.abName.IndexOf('/', StringComparison.Ordinal) >= 0 ||
                hotFile.abName.IndexOf('\\', StringComparison.Ordinal) >= 0 ||
                hotFile.abName.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更文件名包含非法路径片段：{hotFile.abName}。");
            }

            char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
            for (int index = 0; index < hotFile.abName.Length; index++)
            {
                char character = hotFile.abName[index];
                if (char.IsControl(character) || Array.IndexOf(invalidFileNameChars, character) >= 0)
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的热更文件名包含非法字符：{hotFile.abName}。");
                }
            }

            if (!assetNames.Add(hotFile.abName))
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更清单包含重复文件：{hotFile.abName}。");
            }

            if (string.IsNullOrWhiteSpace(hotFile.md5) || hotFile.md5.Length != 32)
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更文件缺少有效 MD5：{hotFile.abName}。");
            }

            for (int index = 0; index < hotFile.md5.Length; index++)
            {
                if (!Uri.IsHexDigit(hotFile.md5[index]))
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的热更文件 MD5 格式非法：{hotFile.abName}。");
                }
            }

            if (float.IsNaN(hotFile.size) || float.IsInfinity(hotFile.size) || hotFile.size < 0f)
            {
                throw new HotUpdateVersionCheckException(
                    CurBundleModuleName,
                    "InvalidManifest",
                    $"模块 {CurBundleModuleName} 的热更文件大小非法：{hotFile.abName}。");
            }


            if (UsesWebGLVersionPointer)
            {
                if (string.IsNullOrWhiteSpace(hotFile.bundleHash))
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的 WebGL Bundle 缺少 Hash：{hotFile.abName}。");
                try
                {
                    Hash128 hash = Hash128.Parse(hotFile.bundleHash);
                    if (!hash.isValid)
                        throw new FormatException("Hash128 is invalid.");
                }
                catch (Exception exception)
                {
                    throw new HotUpdateVersionCheckException(
                        CurBundleModuleName,
                        "InvalidManifest",
                        $"模块 {CurBundleModuleName} 的 WebGL Bundle Hash 无效：{hotFile.abName}。",
                        exception);
                }
            }
        }

        /// <summary>
        /// 清理未完成的清单临时文件，避免下次启动误把半写入文件当作缓存。
        /// </summary>
        private static void TryDeleteManifestStagingFile(string manifestStagingPath)
        {
            try
            {
                if (File.Exists(manifestStagingPath))
                    File.Delete(manifestStagingPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"清理热更清单临时文件失败：{exception.Message}");
            }
        }
        /// <summary>
        /// 生成热更清单路径
        /// </summary>
        public void GeneratorHotAssetsManifest()
        {
            mServerHotAssetsManifestPath = Application.persistentDataPath + "/Server" + CurBundleModuleName + "AssetsHotManifest.json";
            mLocalHotAssetManifestPath = Application.persistentDataPath + "/Local" + CurBundleModuleName + "AssetsHotManifest.json";
        }

#region 资源下载回调
        private void DownLoadAssetBundleSuccess(HotFileInfo hotFile)
        {
            // 单文件成功只代表临时快照中的文件通过校验，业务通知必须等待整批事务提交。
        }

        /// <summary>
        /// 失败收口入口：等待后台任务退出、恢复旧快照，并向协调器或旧调用方发送失败事件。
        /// </summary>
        public async void DownLoadAssetBundleFailed(HotFileInfo hotFile)
        {
            string failedFileName = hotFile == null ? "未知文件" : hotFile.abName;
            Debug.LogError($"模块 {CurBundleModuleName} 热更失败，文件：{failedFileName}。本地清单保持不变。");

            // 失败回调产生时下载队列通常已结束；仍统一等待可避免未来提前失败策略留下写文件线程。
            if (mAssetsDownLoader != null)
                await mAssetsDownLoader.CancelAndWaitAsync();

            if (mIsCoordinatedTransaction)
            {
                mIsHotUpdateRunning = false;
                mCoordinatedFailedCallback?.Invoke(this, hotFile, null);
                return;
            }

            await RollbackHotUpdateTransactionAsync();
            mAssetsDownLoader?.Dispose();
            mAssetsDownLoader = null;
            // 失败时绝不能触发成功回调，否则业务层会把不完整版本当作可用版本。
            mIsHotUpdateRunning = false;
            mPendingHotFinishCallbacks.Clear();
            OnDownLoadAllAssetsFailed?.Invoke(CurBundleModuleName, hotFile);
        }
        /// <summary>
        /// 所有资源下载完成
        /// </summary>
        /// <param name="hotFile"></param>
        /// <summary>
        /// 旧单模块成功入口：校验、切换、初始化全部成功后才通知业务层。
        /// </summary>
        public async void DownLoadAllAssetBundleFinish (HotFileInfo hotFile)
        {
            try
            {
                // 最终磁盘切换前再次检查依赖图；下载期间新初始化的业务模块也能阻止 Shared 在线升级。
                EnsureModuleConfigurationMutationAllowed();
                ValidateStagedSnapshot();
                await PromoteStagedSnapshotAsync(true);

                // 只有正式快照切换成功后才能初始化配置；已初始化模块也必须安全重载，禁止磁盘与内存版本分叉。
                bool moduleAlreadyInitialized = AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName);
                
                bool initializeSucceeded = moduleAlreadyInitialized ? await AssetBundleManager.Instance.ReloadAssetModule(CurBundleModuleName) : await ZMAsset.Modules.InitializeAsync(CurBundleModuleName);
                
                if (!initializeSucceeded && (moduleAlreadyInitialized || !AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName)))
                    throw new InvalidOperationException($"模块 {CurBundleModuleName} 配置初始化或安全重载失败。");
                //正式快照和配置初始化成功后删除回滚数据。
                await FinalizeHotUpdateTransactionAsync();
                
                DispatchCommittedFileCallbacks();
                
                NotifyDownloadSucceeded();
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 热更新事务提交失败：{exception}");
                DownLoadAssetBundleFailed(null);
            }
        }

        /// <summary>
        /// 下载器的统一完成出口；协调模式停在校验完成，旧单模块模式继续原有提交路径。
        /// </summary>
        private void HandleDownloadBatchFinished(HotFileInfo hotFile)
        {
            if (!mIsCoordinatedTransaction)
            {
                DownLoadAllAssetBundleFinish(hotFile);
                return;
            }

            try
            {
                ValidateStagedSnapshot();
                mCoordinatedHasChanges = true;
                mCoordinatedPreparedCallback?.Invoke(this, true);
            }
            catch (Exception exception)
            {
                mIsHotUpdateRunning = false;
                mCoordinatedFailedCallback?.Invoke(this, hotFile, exception);
            }
        }

        /// <summary>
        /// 由组协调器按调用方顺序切换已校验快照；没有变化的模块是空操作。
        /// </summary>
        internal async UniTask PrepareCoordinatedCommitAsync()
        {
            if (!mIsCoordinatedTransaction)
                throw new InvalidOperationException($"模块 {CurBundleModuleName} 未处于多模块事务模式。");
            if (mCoordinatedHasChanges)
                await PromoteStagedSnapshotAsync(false);
        }

        internal HotUpdateCommitContext TransactionContext => mTransactionContext;

        /// <summary>
        /// 在全部模块磁盘切换完成后，按显式顺序初始化或安全重载配置。
        /// </summary>
        internal async UniTask<bool> InitializeCoordinatedTransaction()
        {
            bool isInitialized = AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName);
            if (isInitialized && mCoordinatedHasChanges)
                return await AssetBundleManager.Instance.ReloadAssetModule(CurBundleModuleName);
            if (!isInitialized)
                return await ZMAsset.Modules.InitializeAsync(CurBundleModuleName);
            return true;
        }

        /// <summary>
        /// 组内全部初始化成功后才删除备份；随后统一发送文件级提交事件。
        /// </summary>
        internal async UniTask FinalizeCoordinatedTransactionAsync(bool groupPointerAlreadyFinalized)
        {
            if (mCoordinatedHasChanges)
            {
                if (!groupPointerAlreadyFinalized)
                    await FinalizeHotUpdateTransactionAsync();
                else
                    ResetTransactionState();
                DispatchCommittedFileCallbacks();
            }
            ResetCoordinatedState();
        }

        /// <summary>
        /// 先等待后台下载退出，再恢复磁盘；已在本事务中新初始化的模块同时撤销内存配置。
        /// </summary>
        internal async UniTask RollbackCoordinatedTransaction()
        {
            if (mAssetsDownLoader != null)
                await mAssetsDownLoader.CancelAndWaitAsync();

            if ((mCoordinatedHasChanges || mTransactionContext != null) &&
                !await RollbackHotUpdateTransactionAsync())
                throw new IOException($"模块 {CurBundleModuleName} 的热更新磁盘快照回滚失败。");

            bool isInitializedNow = AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName);
            if (!mWasInitializedBeforeCoordinatedTransaction && isInitializedNow)
            {
                ModuleUnloadResult unloadResult =
                    await AssetBundleManager.Instance.UnloadAssetModuleAsync(CurBundleModuleName);
                if (unloadResult.status != ModuleUnloadStatus.Success &&
                    unloadResult.status != ModuleUnloadStatus.NotInitialized)
                    throw new InvalidOperationException(
                        $"模块 {CurBundleModuleName} 回滚后撤销新配置失败：{unloadResult.message}",
                        unloadResult.exception);
            }
            else if (mWasInitializedBeforeCoordinatedTransaction && isInitializedNow && mCoordinatedHasChanges)
            {
                if (!await AssetBundleManager.Instance.ReloadAssetModule(CurBundleModuleName))
                    throw new InvalidOperationException($"模块 {CurBundleModuleName} 回滚后恢复旧配置失败。");
            }

            ResetCoordinatedState();
        }

        /// <summary>
        /// 进程重启发现组事务日志时强制回滚模块日志，不采用单模块“向前完成提交”策略。
        /// </summary>
        internal void RollbackInterruptedGroupTransaction()
        {
            GeneratorHotAssetsManifest();
            // 组事务恢复始终回滚整组，不采用单模块允许的“目录已完整切换则向前完成”策略。
            mCommitStrategy.RecoverInterruptedTransaction(
                CurBundleModuleName,
                HotAssetsSavePath,
                mLocalHotAssetManifestPath,
                true);
            ResetTransactionState();
        }

        private void ResetCoordinatedState()
        {
            // 递增代次使仍在网络中的旧版本检查回调永久失效。
            mCoordinatedOperationId++;
            mIsHotUpdateRunning = false;
            mIsCoordinatedTransaction = false;
            mCoordinatedHasChanges = false;
            mCoordinatedPreparedCallback = null;
            mCoordinatedFailedCallback = null;
            mAssetsDownLoader?.Dispose();
            mAssetsDownLoader = null;
        }

        /// <summary>
        /// 由后台下载线程原子累计已写入字节数；UI 只读取换算后的快照。
        /// </summary>
        internal void AddDownloadedBytes(int byteCount)
        {
            if (byteCount > 0)
                Interlocked.Add(ref mAssetsDownloadedBytes, byteCount);
        }

        /// <summary>
        /// 无需下载时仍保证当前磁盘配置已经初始化，再向业务层发送完成事件。
        /// </summary>
        private async UniTask InitializeCurrentConfigurationAndNotifyAsync()
        {
            try
            {
                if (!AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName))
                {
                    bool initializeSucceeded = await ZMAsset.Modules.InitializeAsync(CurBundleModuleName);
                    
                    if (!initializeSucceeded && !AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName)) throw new InvalidOperationException($"模块 {CurBundleModuleName} 当前配置初始化失败。");
                }
                NotifyDownloadSucceeded();
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 无需下载但配置初始化失败：{exception}");
                
                DownLoadAssetBundleFailed(null);
            }
        }

        /// <summary>
        /// 创建与正式目录同级的临时快照，并复制本版本中无需重新下载的有效文件。
        /// </summary>
        private async UniTask PrepareHotUpdateTransactionAsync()
        {
            // 在创建 staging 和下载文件前先做一次快速门禁，避免明知 Shared 有消费者仍浪费带宽和磁盘。
            EnsureModuleConfigurationMutationAllowed();
            await RecoverInterruptedTransactionIfNeededAsync();
            if (UsesWebGLVersionPointer)
            {
                WebGLVersionPointerCommitStrategy strategy = GetWebGLCommitStrategy();
                mTransactionContext = await strategy.CreateTransactionAsync(CurBundleModuleName);
                return;
            }
            mTransactionContext = mCommitStrategy.CreateTransaction(
                CurBundleModuleName,
                HotAssetsSavePath,
                mLocalHotAssetManifestPath);
            HashSet<string> downloadFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (HotFileInfo hotFile in mNeedDownLoadAssetsList)
            {
                // 服务端文件名必须在启动下载前完成路径边界校验，禁止先写出临时目录再事后发现越界。
                GetSafeSnapshotFilePath(mTransactionContext.StagingSnapshotPath, hotFile.abName);
                downloadFileNames.Add(hotFile.abName);
            }

            foreach (HotFileInfo hotFile in mAllHotAssetsList)
            {
                if (downloadFileNames.Contains(hotFile.abName))
                    continue;

                string targetPath = GetSafeSnapshotFilePath(mTransactionContext.StagingSnapshotPath, hotFile.abName);
                
                string finalSourcePath = GetSafeSnapshotFilePath(mTransactionContext.FinalSnapshotPath, hotFile.abName);
                
                string decompressSourcePath = GetSafeSnapshotFilePath(BundleSettings.Instance.GetAssetsDecompressPath(CurBundleModuleName), hotFile.abName);
                
                string validSourcePath = FindValidSnapshotSource(hotFile, finalSourcePath, decompressSourcePath);
                
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                
                File.Copy(validSourcePath, targetPath, true);
            }
        }

        /// <summary>
        /// Shared 或任何被已初始化模块依赖的配置不能在线热更；必须先卸载全部消费者。
        /// </summary>
        private void EnsureModuleConfigurationMutationAllowed()
        {
            if (AssetBundleManager.Instance.CanMutateModuleConfiguration(
                    CurBundleModuleName,
                    out string reason)) return;
            throw new InvalidOperationException(
                $"模块 {CurBundleModuleName} 当前禁止热更：{reason}。" +
                "请先卸载依赖它的业务模块，或随完整资源包发布 Shared 变更。");
        }

        /// <summary>
        /// 从可信模块路径定位事务日志，并在创建新事务前完成中断恢复。
        /// </summary>
        private async UniTask RecoverInterruptedTransactionIfNeededAsync()
        {
            if (UsesWebGLVersionPointer)
            {
                await GetWebGLCommitStrategy().InitializeAsync();
                ResetTransactionState();
                return;
            }
            if (string.IsNullOrEmpty(mLocalHotAssetManifestPath))
                GeneratorHotAssetsManifest();
            mCommitStrategy.RecoverInterruptedTransaction(
                CurBundleModuleName,
                HotAssetsSavePath,
                mLocalHotAssetManifestPath,
                false);
            ResetTransactionState();
        }

        private WebGLVersionPointerCommitStrategy GetWebGLCommitStrategy()
        {
            if (mCommitStrategy is WebGLVersionPointerCommitStrategy strategy)
                return strategy;
            throw new InvalidOperationException("WebGL 热更新模块未绑定版本指针提交策略。");
        }

        /// <summary>
        /// 校验临时快照中的完整版本，而不只是本次重新下载的文件。
        /// </summary>
        private void ValidateStagedSnapshot()
        {
            if (UsesWebGLVersionPointer)
                return;
            if (mTransactionContext == null || !Directory.Exists(mTransactionContext.StagingSnapshotPath))
                throw new DirectoryNotFoundException(
                    $"热更新临时目录不存在：{mTransactionContext?.StagingSnapshotPath ?? "未创建事务"}");
            
            foreach (HotFileInfo hotFile in mAllHotAssetsList)
            {
                string stagedFilePath = GetSafeSnapshotFilePath(mTransactionContext.StagingSnapshotPath, hotFile.abName);
                
                if (!File.Exists(stagedFilePath))
                    throw new FileNotFoundException("临时快照缺少文件", stagedFilePath);
                
                if (!string.Equals(MD5.GetMd5FromFile(stagedFilePath), hotFile.md5, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"临时快照文件校验失败：{hotFile.abName}");
                }
            }
        }

        /// <summary>
        /// 使用同磁盘目录重命名切换资源快照和 Manifest，任一步失败均可回滚。
        /// </summary>
        private async UniTask PromoteStagedSnapshotAsync(bool commitWebGLPointer)
        {
            string manifestJson = JsonConvert.SerializeObject(mServerHotAssetsManifest, Formatting.Indented);
            if (UsesWebGLVersionPointer)
            {
                WebGLVersionPointerCommitStrategy strategy = GetWebGLCommitStrategy();
                await strategy.PrepareCandidateAsync(mTransactionContext, manifestJson);
                if (commitWebGLPointer)
                {
                    await strategy.CommitGroupAsync(
                        new[] { mTransactionContext },
                        mTransactionContext.TransactionId,
                        new[] { CurBundleModuleName });
                }
                return;
            }
            mCommitStrategy.PromoteSnapshot(mTransactionContext, manifestJson);
        }

        /// <summary>
        /// 正式快照和配置初始化成功后删除回滚数据。
        /// </summary>
        private async UniTask FinalizeHotUpdateTransactionAsync()
        {
            if (UsesWebGLVersionPointer)
                await GetWebGLCommitStrategy().FinalizeGroupAsync(mTransactionContext.TransactionId);
            else
                mCommitStrategy.FinalizeTransaction(mTransactionContext);
            ResetTransactionState();
        }

        /// <summary>
        /// 恢复事务开始前的资源目录和本地 Manifest。
        /// </summary>
        private async UniTask<bool> RollbackHotUpdateTransactionAsync()
        {
            if (mTransactionContext == null)
                return true;
            if (UsesWebGLVersionPointer)
            {
                try
                {
                    await GetWebGLCommitStrategy().RollbackGroupAsync(
                        new[] { mTransactionContext },
                        mTransactionContext.TransactionId);
                    ResetTransactionState();
                    return true;
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"模块 {CurBundleModuleName} WebGL 热更新指针回滚失败，事务：{mTransactionContext.TransactionId}，异常：{exception}");
                    ResetTransactionState();
                    return false;
                }
            }
            bool rollbackSucceeded = mCommitStrategy.Rollback(mTransactionContext, out Exception failure);
            if (!rollbackSucceeded)
                Debug.LogError(
                    $"模块 {CurBundleModuleName} 热更新回滚失败，事务：{mTransactionContext.TransactionId}，异常：{failure}");
            ResetTransactionState();
            return rollbackSucceeded;
        }

        private void DispatchCommittedFileCallbacks()
        {
            // 配置文件通知优先，之后再通知普通 Bundle，所有通知都发生在配置初始化完成之后。
            foreach (HotFileInfo hotFile in mNeedDownLoadAssetsList)
            {
                if (hotFile.abName.Contains("bundleconfig"))
                    DispatchCommittedFileCallback(hotFile);
            }
            foreach (HotFileInfo hotFile in mNeedDownLoadAssetsList)
            {
                if (!hotFile.abName.Contains("bundleconfig"))
                    DispatchCommittedFileCallback(hotFile);
            }
        }

        private void DispatchCommittedFileCallback(HotFileInfo hotFile)
        {
            string abName = hotFile.abName;
            if (!string.IsNullOrEmpty(BundleSettings.Instance.ABSUFFIX) && hotFile.abName.Contains("."))
            {
                abName = hotFile.abName.Replace(BundleSettings.Instance.ABSUFFIX, "");
            }

            try
            {
                if (hotFile.abName.Contains("bundleconfig"))
                    OnDownLoadABConfigListener?.Invoke(abName);
                else
                    OnDownLoadAssetBundleListener?.Invoke(abName);
                HotAssetsManager.DownLoadBundleFinish?.Invoke(hotFile);
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 提交文件回调执行异常：{exception}");
            }
        }

        private static string FindValidSnapshotSource(HotFileInfo hotFile, string finalSourcePath, string decompressSourcePath)
        {
            if (File.Exists(finalSourcePath) && string.Equals(MD5.GetMd5FromFile(finalSourcePath), hotFile.md5, StringComparison.OrdinalIgnoreCase))
            {
                return finalSourcePath;
            }
            
            if (File.Exists(decompressSourcePath) && string.Equals(MD5.GetMd5FromFile(decompressSourcePath), hotFile.md5, StringComparison.OrdinalIgnoreCase))
            {
                return decompressSourcePath;
            }
            throw new FileNotFoundException($"找不到有效的本地快照文件：{hotFile.abName}");
        }

        private static string GetSafeSnapshotFilePath(string rootPath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException($"非法热更新文件名：{relativePath}");

            string normalizedRootPath = NormalizeDirectoryPath(rootPath);
            
            string fullPath = Path.GetFullPath(Path.Combine(normalizedRootPath, relativePath));
            
            string rootPrefix = normalizedRootPath + Path.DirectorySeparatorChar;
            
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"热更新文件越过目标目录：{relativePath}");
            
            return fullPath;
        }

        private static string NormalizeDirectoryPath(string path)
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private void ResetTransactionState()
        {
            mTransactionContext = null;
        }

        /// <summary>
        /// 统一发送热更成功事件，并确保调用方回调只消费一次。
        /// </summary>
        private void NotifyDownloadSucceeded()
        {
            mAssetsDownLoader?.Dispose();
            mAssetsDownLoader = null;
            mIsHotUpdateRunning = false;
            OnDownLoadAllAssetsFinish?.Invoke(CurBundleModuleName);
            Action<string>[] callbacks = mPendingHotFinishCallbacks.ToArray();
            mPendingHotFinishCallbacks.Clear();
            foreach (Action<string> callback in callbacks)
                callback?.Invoke(CurBundleModuleName);
        }
#endregion

        public void OnMainThreadUpdate()
        {
            mAssetsDownLoader?.UpdateOnMainThread();
        }
        /// <summary>
        /// 设置下载线程个数
        /// </summary>
        /// <param name="threadCount"></param>
        public void SetDownLoadThreadCount(int threadCount)
        {
            Debug.Log("多线程负载均衡:"+threadCount+" ModuleType:"+CurBundleModuleName);
            if (mAssetsDownLoader!=null)
            {
                mAssetsDownLoader.MaximumConcurrency = threadCount;
            }
        }
        /// <summary>
        /// 判断热更文件是否存在
        /// </summary>
        public bool HotAssetsIsExists(string bundleName)
        {
            foreach (var item in mAllHotAssetsList)
            {
                if (string.Equals(bundleName,item.abName))
                {
                    return true;
                }
            }
            return false;
        }

    }
}
