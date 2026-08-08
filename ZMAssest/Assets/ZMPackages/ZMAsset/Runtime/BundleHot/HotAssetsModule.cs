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
        public string UpdateNoticeContent { get { return mServerHotAssetsManifest.updateNotice; } }
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
        private AssetsDownLoader mAssetsDownLoader;
        /// <summary>
        /// 当前热更新事务的临时快照、回滚快照和 Manifest 临时文件。
        /// </summary>
        private string mTransactionStagingPath;
        private string mTransactionBackupPath;
        private string mTransactionManifestStagingPath;
        private string mTransactionManifestBackupPath;
        private string mTransactionJournalPath;
        private string mTransactionId;
        private bool mHadFinalSnapshot;
        private bool mHadLocalManifest;
        /// <summary>
        /// 中断恢复日志只保存事务标识和旧状态，所有真实路径均由可信根目录重新推导。
        /// </summary>
        [Serializable]
        private sealed class HotUpdateTransactionJournal
        {
            public string transactionId;
            public bool hadFinalSnapshot;
            public bool hadLocalManifest;
        }
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
                //检测资源版本是否需要热更
                // 先读取服务端 Manifest 并计算差异；只有确实有补丁才进入 staging 下载。
                CheckAssetsVersion((isHot,size)=> {
                    if (isHot)
                    {
                        // 版本有变化：创建临时快照并开始下载，正式目录此时仍保持旧版本。
                        StartDownLoadHotAssets(startDownLoadCallback);
                    }
                    else
                    {
                        // 没有补丁：不创建事务，但仍确保当前磁盘配置已初始化。
                        InitializeCurrentConfigurationAndNotify();
                    }
                });
            }
            else
            {
                // 调用方已明确跳过版本检查，直接使用此前准备好的 Manifest 差异开始下载。
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
                StartDownLoadHotAssets(startDownloadCallback);
                return;
            }

            CheckAssetsVersion((isHot, size) =>
            {
                // 版本请求本身暂不支持取消；旧请求延迟返回时必须忽略，不能污染下一次事务。
                if (!mIsCoordinatedTransaction || !mIsHotUpdateRunning || operationId != mCoordinatedOperationId)
                    return;

                if (isHot)
                {
                    StartDownLoadHotAssets(startDownloadCallback);
                    return;
                }

                // 无版本变化的模块仍参与统一初始化顺序，但不会创建或切换磁盘快照。
                mCoordinatedPreparedCallback?.Invoke(this, false);
            });
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

            try
            {
                // 先创建 staging、写模块日志并复制无需下载的旧文件，下载器只能写入 staging。
                PrepareHotUpdateTransaction();
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 创建热更新临时快照失败：{exception}");
                DownLoadAssetBundleFailed(
                    mNeedDownLoadAssetsList.Count > 0 ? mNeedDownLoadAssetsList[0] : null);
                return;
            }

            // 所有下载只写入临时快照，正式资源目录在完整校验前保持不变。
            mAssetsDownLoader = new AssetsDownLoader(
                this,
                downLoadQueue,
                mServerHotAssetsManifest.downLoadURL,
                mTransactionStagingPath,
                DownLoadAssetBundleSuccess,
                DownLoadAssetBundleFailed,
                HandleDownloadBatchFinished);

            startDonwLoadCallBack?.Invoke();
            //开始下载队列中的资源
            // 下载器启动后，后台只负责网络和文件，完成回调会由 OnMainThreadUpdate 切回 Unity 主线程。
            mAssetsDownLoader.StartThreadDownLoadQueue();

        }
        /// <summary>
        /// 检测资源版本；下载服务端清单、比对版本并计算需要热更的文件列表。
        /// </summary>
        /// <returns>是否需要热更，以及需要下载的热更大小（MB）。</returns>
        public async UniTask<(bool isHot, float sizeMb)> CheckAssetsVersionAsync()
        {
            //生成热更清单路径
            GeneratorHotAssetsManifest();

            try
            {
                // 版本比较前先恢复上次中断事务，避免用半切换的 Manifest 计算补丁差异。
                RecoverInterruptedTransactionIfNeeded();
            }
            catch (Exception exception)
            {
                // 正式资源目录始终是完整旧版或完整新版；恢复失败时保留事务材料并继续联网校验。
                Debug.LogError($"模块 {CurBundleModuleName} 恢复上次热更新事务失败：{exception}");
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
        /// 检测资源版本
        /// </summary>
        /// <param name="checkCallBack"></param>
        public void CheckAssetsVersion(Action<bool,float> checkCallBack)
        {
            CheckAssetsVersionSafeAsync(checkCallBack).Forget();
        }

        /// <summary>
        /// 异常安全的版本检查包装器：确保回调总会被调用，避免异常被 .Forget() 吞掉导致 Coordinator 永久挂起。
        /// </summary>
        private async UniTaskVoid CheckAssetsVersionSafeAsync(Action<bool, float> checkCallBack)
        {
            try
            {
                var result = await CheckAssetsVersionAsync();
                checkCallBack?.Invoke(result.isHot, result.sizeMb);
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 版本检查失败：{exception}");
                // 异常时安全降级：通知无需热更，避免 Coordinator 等待循环永久挂起
                checkCallBack?.Invoke(false, 0f);
            }
        }
        /// <summary>
        /// 计算需要热更的文件列表
        /// </summary>
        /// <param name="serverAssetsPath"></param>
        /// <returns></returns>
        public bool ComputeNeedHotAssetsList(HotAssetsPatch serverAssetsPath)
        {
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
            //如果本地资源清单文件不存在，说明我们需要热更
            if (!File.Exists(mLocalHotAssetManifestPath))
            {
                return true;
            }
            //判断本地资源清单补丁版本号是否与服务端资源清单补丁版本号一致，如果一致，不需要热更， 如果不一致，则需要热更
            HotAssetsManifest localHotAssetsManifest = JsonConvert.DeserializeObject<HotAssetsManifest>(File.ReadAllText(mLocalHotAssetManifestPath));
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
            using (UnityWebRequest webRequest = UnityWebRequest.Get(url))
            {
                webRequest.timeout = 30;
                Debug.Log($"*** Request AssetBundle HotAssetsMainfest Url Start Module:{CurBundleModuleName} url:{url}");

                await webRequest.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
                if (webRequest.result == UnityWebRequest.Result.ConnectionError)
#else
                if (webRequest.isNetworkError)
#endif
                {
                    Debug.LogError("DownLoad Error:" + webRequest.error);
                }
                else
                {
                    string downLoadContent = webRequest.downloadHandler.text;
                    try
                    {
                        Debug.Log($"*** Request AssetBundle HotAssetsMainfest Url Finish Module:{CurBundleModuleName} txt:{downLoadContent}");
                        //写入服务端资源热更清单到本地：先写临时文件再原子切换，避免进程中断留下半个 JSON 缓存；
                        //清单只有几 KB，同步写入可在当前 try-catch 内立即发现失败，替代原来不可观测的 async void 写法。
                        string manifestStagingPath = mServerHotAssetsManifestPath + ".writing";
                        
                        await File.WriteAllTextAsync(manifestStagingPath, downLoadContent);
                        
                        RemoteAssetModule.PromoteVerifiedFile(manifestStagingPath, mServerHotAssetsManifestPath);
                        
                        if (!string.IsNullOrEmpty(downLoadContent) && downLoadContent.Contains("md5"))
                            mServerHotAssetsManifest = JsonConvert.DeserializeObject<HotAssetsManifest>(downLoadContent);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("服务端资源清单配置下载异常，文件不存在或者配置有问题，更新出错，请检查：" + e.ToString());
                    }
                }
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

            RollbackHotUpdateTransaction();
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
                PromoteStagedSnapshot();

                // 只有正式快照切换成功后才能初始化配置；已初始化模块也必须安全重载，禁止磁盘与内存版本分叉。
                bool moduleAlreadyInitialized = AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName);
                
                bool initializeSucceeded = moduleAlreadyInitialized ? await AssetBundleManager.Instance.ReloadAssetModule(CurBundleModuleName) : await ZMAsset.Modules.InitializeAsync(CurBundleModuleName);
                
                if (!initializeSucceeded && (moduleAlreadyInitialized || !AssetBundleManager.Instance.IsAssetModuleInitialized(CurBundleModuleName)))
                    throw new InvalidOperationException($"模块 {CurBundleModuleName} 配置初始化或安全重载失败。");
                //正式快照和配置初始化成功后删除回滚数据。
                FinalizeHotUpdateTransaction();
                
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
        internal void PromoteCoordinatedTransaction()
        {
            if (!mIsCoordinatedTransaction)
                throw new InvalidOperationException($"模块 {CurBundleModuleName} 未处于多模块事务模式。");
            if (mCoordinatedHasChanges)
                PromoteStagedSnapshot();
        }

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
        internal void FinalizeCoordinatedTransaction()
        {
            if (mCoordinatedHasChanges)
            {
                FinalizeHotUpdateTransaction();
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

            if ((mCoordinatedHasChanges || !string.IsNullOrEmpty(mTransactionStagingPath)) &&
                !RollbackHotUpdateTransaction())
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
            ValidateModuleName(CurBundleModuleName);
            string finalSnapshotPath = NormalizeDirectoryPath(HotAssetsSavePath);
            string snapshotParentPath = Directory.GetParent(finalSnapshotPath)?.FullName;
            if (string.IsNullOrEmpty(snapshotParentPath))
                throw new InvalidOperationException($"无法解析热更新目录父路径：{finalSnapshotPath}");

            mTransactionJournalPath = Path.Combine(snapshotParentPath, $".{CurBundleModuleName}.hotupdate.transaction");
            string journalWritingPath = mTransactionJournalPath + ".writing";
            if (!File.Exists(mTransactionJournalPath) && File.Exists(journalWritingPath))
                File.Move(journalWritingPath, mTransactionJournalPath);
            if (!File.Exists(mTransactionJournalPath))
                return;

            HotUpdateTransactionJournal journal =
                JsonConvert.DeserializeObject<HotUpdateTransactionJournal>(File.ReadAllText(mTransactionJournalPath));
            if (journal == null || !IsValidTransactionId(journal.transactionId))
                throw new InvalidDataException($"模块 {CurBundleModuleName} 的组事务恢复日志非法。");

            mTransactionId = journal.transactionId;
            mTransactionStagingPath = Path.Combine(snapshotParentPath, $".{mTransactionId}.staging");
            mTransactionBackupPath = Path.Combine(snapshotParentPath, $".{mTransactionId}.backup");
            mTransactionManifestStagingPath = $"{mLocalHotAssetManifestPath}.{mTransactionId}.staging";
            mTransactionManifestBackupPath = $"{mLocalHotAssetManifestPath}.{mTransactionId}.backup";
            mHadFinalSnapshot = journal.hadFinalSnapshot;
            mHadLocalManifest = journal.hadLocalManifest;
            if (!RollbackHotUpdateTransaction())
                throw new IOException($"模块 {CurBundleModuleName} 的中断组事务回滚失败。");
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
        private async void InitializeCurrentConfigurationAndNotify()
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
        private void PrepareHotUpdateTransaction()
        {
            // 在创建 staging 和下载文件前先做一次快速门禁，避免明知 Shared 有消费者仍浪费带宽和磁盘。
            EnsureModuleConfigurationMutationAllowed();
            RecoverInterruptedTransactionIfNeeded();
            
            string finalSnapshotPath = NormalizeDirectoryPath(HotAssetsSavePath);
            
            string snapshotParentPath = Directory.GetParent(finalSnapshotPath)?.FullName;
            
            if (string.IsNullOrEmpty(snapshotParentPath))
                throw new InvalidOperationException($"无法解析热更新目录父路径：{finalSnapshotPath}");
            mTransactionJournalPath = Path.Combine(snapshotParentPath, $".{CurBundleModuleName}.hotupdate.transaction");

            mTransactionId = $"{CurBundleModuleName}_{Guid.NewGuid():N}";
            
            mTransactionStagingPath = Path.Combine(snapshotParentPath, $".{mTransactionId}.staging");
            
            mTransactionBackupPath = Path.Combine(snapshotParentPath, $".{mTransactionId}.backup");
            
            mTransactionManifestStagingPath = $"{mLocalHotAssetManifestPath}.{mTransactionId}.staging";
            
            mTransactionManifestBackupPath = $"{mLocalHotAssetManifestPath}.{mTransactionId}.backup";
            
            mHadFinalSnapshot = Directory.Exists(finalSnapshotPath);
            
            mHadLocalManifest = File.Exists(mLocalHotAssetManifestPath);

            Directory.CreateDirectory(mTransactionStagingPath);
            // 创建临时目录后立即登记事务，复制旧文件或下载期间退出也能在下次启动清理。
            WriteTransactionJournal();
            HashSet<string> downloadFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (HotFileInfo hotFile in mNeedDownLoadAssetsList)
            {
                // 服务端文件名必须在启动下载前完成路径边界校验，禁止先写出临时目录再事后发现越界。
                GetSafeSnapshotFilePath(mTransactionStagingPath, hotFile.abName);
                downloadFileNames.Add(hotFile.abName);
            }

            foreach (HotFileInfo hotFile in mAllHotAssetsList)
            {
                if (downloadFileNames.Contains(hotFile.abName))
                    continue;

                string targetPath = GetSafeSnapshotFilePath(mTransactionStagingPath, hotFile.abName);
                
                string finalSourcePath = GetSafeSnapshotFilePath(finalSnapshotPath, hotFile.abName);
                
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
        private void RecoverInterruptedTransactionIfNeeded()
        {
            ValidateModuleName(CurBundleModuleName);
            if (string.IsNullOrEmpty(mLocalHotAssetManifestPath))
                GeneratorHotAssetsManifest();

            string finalSnapshotPath = NormalizeDirectoryPath(HotAssetsSavePath);
            
            string snapshotParentPath = Directory.GetParent(finalSnapshotPath)?.FullName;
            
            if (string.IsNullOrEmpty(snapshotParentPath)) 
                throw new InvalidOperationException($"无法解析热更新目录父路径：{finalSnapshotPath}");
            
            Directory.CreateDirectory(snapshotParentPath);
            
            mTransactionJournalPath = Path.Combine(snapshotParentPath, $".{CurBundleModuleName}.hotupdate.transaction");
            
            RecoverInterruptedTransaction(snapshotParentPath, finalSnapshotPath);
            
            mTransactionJournalPath = Path.Combine(snapshotParentPath, $".{CurBundleModuleName}.hotupdate.transaction");
        }

        /// <summary>
        /// 校验临时快照中的完整版本，而不只是本次重新下载的文件。
        /// </summary>
        private void ValidateStagedSnapshot()
        {
            if (!Directory.Exists(mTransactionStagingPath))
                throw new DirectoryNotFoundException($"热更新临时目录不存在：{mTransactionStagingPath}");
            
            foreach (HotFileInfo hotFile in mAllHotAssetsList)
            {
                string stagedFilePath = GetSafeSnapshotFilePath(mTransactionStagingPath, hotFile.abName);
                
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
        private void PromoteStagedSnapshot()
        {
            string finalSnapshotPath = NormalizeDirectoryPath(HotAssetsSavePath);
            
            string manifestJson = JsonConvert.SerializeObject(mServerHotAssetsManifest, Formatting.Indented);
            
            File.WriteAllText(mTransactionManifestStagingPath, manifestJson);

            if (mHadFinalSnapshot)
                Directory.Move(finalSnapshotPath, mTransactionBackupPath);
            Directory.Move(mTransactionStagingPath, finalSnapshotPath);

            if (mHadLocalManifest)
                File.Move(mLocalHotAssetManifestPath, mTransactionManifestBackupPath);
            File.Move(mTransactionManifestStagingPath, mLocalHotAssetManifestPath);
        }

        /// <summary>
        /// 正式快照和配置初始化成功后删除回滚数据。
        /// </summary>
        private void FinalizeHotUpdateTransaction()
        {
            // 先删除事务日志声明提交完成；日志删除失败时保留全部备份，重启后仍可安全判定完整切换。
            if (TryDeleteFile(mTransactionJournalPath, "热更新事务日志"))
            {
                TryDeleteDirectory(mTransactionBackupPath, "热更新备份目录");
                TryDeleteFile(mTransactionManifestBackupPath, "热更新 Manifest 备份");
            }
            ResetTransactionState();
        }

        /// <summary>
        /// 恢复事务开始前的资源目录和本地 Manifest。
        /// </summary>
        private bool RollbackHotUpdateTransaction()
        {
            if (string.IsNullOrEmpty(mTransactionStagingPath))
                return true;

            string finalSnapshotPath = NormalizeDirectoryPath(HotAssetsSavePath);
            bool rollbackSucceeded = false;
            try
            {
                if (Directory.Exists(mTransactionBackupPath))
                {
                    DeleteDirectoryIfExists(finalSnapshotPath);
                    Directory.Move(mTransactionBackupPath, finalSnapshotPath);
                }
                else if (!mHadFinalSnapshot && Directory.Exists(finalSnapshotPath) && !Directory.Exists(mTransactionStagingPath))
                {
                    DeleteDirectoryIfExists(finalSnapshotPath);
                }

                if (File.Exists(mTransactionManifestBackupPath))
                {
                    DeleteFileIfExists(mLocalHotAssetManifestPath);
                    File.Move(mTransactionManifestBackupPath, mLocalHotAssetManifestPath);
                }
                else if (!mHadLocalManifest && File.Exists(mLocalHotAssetManifestPath) && !File.Exists(mTransactionManifestStagingPath))
                {
                    DeleteFileIfExists(mLocalHotAssetManifestPath);
                }
                rollbackSucceeded = true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"模块 {CurBundleModuleName} 热更新回滚失败：{exception}");
            }
            finally
            {
                // 回滚成功后再清理事务材料；失败时保留日志和备份，下一次启动可继续恢复。
                if (rollbackSucceeded)
                {
                    bool stagingRemoved = TryDeleteDirectory(mTransactionStagingPath, "热更新临时目录");
                    
                    bool manifestStagingRemoved = TryDeleteFile(mTransactionManifestStagingPath, "热更新 Manifest 临时文件");
                    
                    if (stagingRemoved && manifestStagingRemoved)
                        TryDeleteFile(mTransactionJournalPath, "热更新事务日志");
                }
                ResetTransactionState();
            }
            return rollbackSucceeded;
        }

        /// <summary>
        /// 写入跨进程事务日志；日志先落临时文件再重命名，避免进程中断留下半段 JSON。
        /// </summary>
        private void WriteTransactionJournal()
        {
            HotUpdateTransactionJournal journal = new HotUpdateTransactionJournal
            {
                transactionId = mTransactionId,
                hadFinalSnapshot = mHadFinalSnapshot,
                hadLocalManifest = mHadLocalManifest
            };
            string journalTempPath = mTransactionJournalPath + ".writing";
            
            File.WriteAllText(journalTempPath, JsonConvert.SerializeObject(journal, Formatting.Indented));
            
            if (File.Exists(mTransactionJournalPath)) throw new IOException($"模块 {CurBundleModuleName} 已存在未处理的热更新事务日志。");
            
            File.Move(journalTempPath, mTransactionJournalPath);
        }

        /// <summary>
        /// 恢复上次被进程退出打断的目录切换；完整切换保留新版本，半切换恢复旧版本。
        /// </summary>
        private void RecoverInterruptedTransaction(string snapshotParentPath, string finalSnapshotPath)
        {
            string journalWritingPath = mTransactionJournalPath + ".writing";
            if (!File.Exists(mTransactionJournalPath) && File.Exists(journalWritingPath))
            {
                // 写日志时退出可能只留下完整或半写入的临时日志；先提升为正式日志，再统一校验并恢复。
                File.Move(journalWritingPath, mTransactionJournalPath);
            }
            if (!File.Exists(mTransactionJournalPath))
                return;

            HotUpdateTransactionJournal journal;
            try
            {
                journal = JsonConvert.DeserializeObject<HotUpdateTransactionJournal>(File.ReadAllText(mTransactionJournalPath));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"模块 {CurBundleModuleName} 的热更新事务日志损坏，已停止覆盖现有资源。", exception);
            }

            if (journal == null || !IsValidTransactionId(journal.transactionId))
                throw new InvalidDataException($"模块 {CurBundleModuleName} 的热更新事务日志标识非法，已停止覆盖现有资源。");

            mTransactionId = journal.transactionId;
            mTransactionStagingPath = Path.Combine(snapshotParentPath, $".{mTransactionId}.staging");
            
            mTransactionBackupPath = Path.Combine(snapshotParentPath, $".{mTransactionId}.backup");
            
            mTransactionManifestStagingPath = $"{mLocalHotAssetManifestPath}.{mTransactionId}.staging";
            
            mTransactionManifestBackupPath = $"{mLocalHotAssetManifestPath}.{mTransactionId}.backup";
            
            mHadFinalSnapshot = journal.hadFinalSnapshot;
            mHadLocalManifest = journal.hadLocalManifest;

            bool directorySwitchCompleted = Directory.Exists(finalSnapshotPath) && !Directory.Exists(mTransactionStagingPath) && (!mHadFinalSnapshot || Directory.Exists(mTransactionBackupPath));
            if (directorySwitchCompleted && File.Exists(mTransactionManifestStagingPath))
            {
                // 资源目录已经完整提升时优先向前完成 Manifest，避免把已校验的新快照回滚为旧版本。
                if (mHadLocalManifest && !File.Exists(mTransactionManifestBackupPath) && File.Exists(mLocalHotAssetManifestPath))
                {
                    File.Move(mLocalHotAssetManifestPath, mTransactionManifestBackupPath);
                }
                else
                {
                    DeleteFileIfExists(mLocalHotAssetManifestPath);
                }
                File.Move(mTransactionManifestStagingPath, mLocalHotAssetManifestPath);
            }

            bool switchCompleted =
                Directory.Exists(finalSnapshotPath) &&
                File.Exists(mLocalHotAssetManifestPath) &&
                !Directory.Exists(mTransactionStagingPath) &&
                !File.Exists(mTransactionManifestStagingPath) &&
                (!mHadFinalSnapshot || Directory.Exists(mTransactionBackupPath)) &&
                (!mHadLocalManifest || File.Exists(mTransactionManifestBackupPath));
            if (switchCompleted)
            {
                // 临时快照和临时 Manifest 都已被原子重命名，说明磁盘切换完整；重启后内存会从新配置重新初始化。
                bool journalRemoved = TryDeleteFile(mTransactionJournalPath, "中断事务日志");
                if (journalRemoved)
                {
                    TryDeleteDirectory(mTransactionBackupPath, "中断事务备份目录");
                    TryDeleteFile(mTransactionManifestBackupPath, "中断事务 Manifest 备份");
                }
                ResetTransactionState();
                if (!journalRemoved) throw new IOException($"模块 {CurBundleModuleName} 的已提交事务日志无法清理，已停止创建新事务。");
                return;
            }

            string interruptedJournalPath = mTransactionJournalPath;
            RollbackHotUpdateTransaction();
            if (File.Exists(interruptedJournalPath)) throw new IOException($"模块 {CurBundleModuleName} 的中断事务未能自动回滚，请保留目录并检查磁盘状态。");
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

        private static void ValidateModuleName(string bundleModule)
        {
            if (string.IsNullOrWhiteSpace(bundleModule) ||
                bundleModule.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                bundleModule.Contains(Path.DirectorySeparatorChar.ToString()) ||
                bundleModule.Contains(Path.AltDirectorySeparatorChar.ToString()))
            {
                throw new InvalidDataException($"非法热更新模块名称：{bundleModule}");
            }
        }

        private bool IsValidTransactionId(string transactionId)
        {
            string prefix = CurBundleModuleName + "_";
            return !string.IsNullOrEmpty(transactionId) &&
                   transactionId.StartsWith(prefix, StringComparison.Ordinal) &&
                   Guid.TryParseExact(transactionId.Substring(prefix.Length), "N", out _);
        }

        private static void DeleteDirectoryIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private static void DeleteFileIfExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }

        private bool TryDeleteDirectory(string path, string operationName)
        {
            try
            {
                DeleteDirectoryIfExists(path);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"模块 {CurBundleModuleName} 清理{operationName}失败，可在下次启动继续回收。路径：{path}，异常：{exception}");
                return false;
            }
        }

        private bool TryDeleteFile(string path, string operationName)
        {
            try
            {
                DeleteFileIfExists(path);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"模块 {CurBundleModuleName} 清理{operationName}失败，可在下次启动继续回收。路径：{path}，异常：{exception}");
                return false;
            }
        }

        private void ResetTransactionState()
        {
            mTransactionStagingPath = null;
            mTransactionBackupPath = null;
            mTransactionManifestStagingPath = null;
            mTransactionManifestBackupPath = null;
            mTransactionJournalPath = null;
            mTransactionId = null;
            mHadFinalSnapshot = false;
            mHadLocalManifest = false;
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
            mAssetsDownLoader?.OnMainThreadUpdate();
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
                mAssetsDownLoader.MAX_THREAD_COUNT = threadCount;
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
