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
using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
namespace ZM.Asset
{
    /// <summary>
    /// 等待下载的模块
    /// </summary>
    public class WaitDownLoadModule
    {
        public string bundleModule;
        public bool checkAssetsVersion;
    }

    /// <summary>
    /// 单次热更调用的回调集合；同模块并发请求会合并到同一个下载任务。
    /// </summary>
    internal sealed class HotUpdateRequestCallbacks
    {
        public Action<string> startHot;
        public Action<string> hotFinish;
        public Action<string, HotFileInfo> hotFailed;
    }

    public class HotAssetsManager : IHotAssets
    {
        public event Action<HotAssetsModuleState> StateChanged;
        /// <summary>
        /// 最大并发下载线程个数
        /// </summary>
        private int MAX_THREAD_COUNT = 3;
        /// <summary>
        /// 单模块 API 与显式多模块事务共用同一个下载线程预算，避免两套并发各自达到上限。
        /// </summary>
        private readonly HotDownloadScheduler mDownloadScheduler = new HotDownloadScheduler();
        private readonly MultiModuleHotUpdateCoordinator mTransactionCoordinator;
        private bool mIsTransactionRunning;
        private Exception mTransactionRecoveryFailure;
        private bool mRecoveryCompleted;
        private readonly WebGLAsyncGate mRecoveryGate = new WebGLAsyncGate(1);
        /// <summary>
        /// 所有热更资源模块
        /// </summary>
        private Dictionary<string, HotAssetsModule> mAllAssetsModuleDic = new Dictionary<string, HotAssetsModule>();

        /// <summary>
        /// 正在下载热更资源模块的字典
        /// </summary>
        private Dictionary<string, HotAssetsModule> mDownLoadingAssetsModuleDic = new Dictionary<string, HotAssetsModule>();
        /// <summary>
        /// 正在下载热更资源的列表
        /// </summary>
        private List<HotAssetsModule> mDownLoadAssetsModuleList = new List<HotAssetsModule>();
        /// <summary>
        /// 等待下载的队列
        /// </summary>
        private Queue<WaitDownLoadModule> mWaitDownLoadQueue = new Queue<WaitDownLoadModule>();
        /// <summary>
        /// 每个模块的所有调用方回调，模块终态产生后统一消费并移除。
        /// </summary>
        private readonly Dictionary<string, List<HotUpdateRequestCallbacks>> mModuleRequestCallbacks =
            new Dictionary<string, List<HotUpdateRequestCallbacks>>();
        /// <summary>
        /// 已经进入等待队列的模块，防止同模块被重复排队和重复下载。
        /// </summary>
        private readonly HashSet<string> mQueuedModuleSet = new HashSet<string>();
        /// <summary>
        /// 下载AssetBundle完成
        /// </summary>
        public static Action<HotFileInfo> DownLoadBundleFinish;

        /// <summary>
        /// 创建热更新管理器；首次请求前按平台异步恢复遗留事务。
        /// </summary>
        public HotAssetsManager() : this(AssetRuntimeBackendFactory.Current)
        {
        }

        /// <summary>
        /// 使用一组已经完成平台选择的后端服务创建热更新管理器。
        /// 该入口同时用于平台契约测试，避免测试通过修改全局单例来模拟 WebGL。
        /// </summary>
        internal HotAssetsManager(AssetRuntimeBackend runtimeBackend)
        {
            if (runtimeBackend == null)
                throw new ArgumentNullException(nameof(runtimeBackend));

            mTransactionCoordinator = new MultiModuleHotUpdateCoordinator(
                this,
                mDownloadScheduler,
                runtimeBackend.MetadataStore,
                runtimeBackend.CommitStrategy);
        }

        public void HotAssets(string bundleModule, Action<string> startHotCallBack, Action<string> hotFinish, Action<string> waiteDownLoad, 
            bool isCheckAssetsVersion = true, Action<string, HotFileInfo> hotFailed = null)
        {
            if (!mRecoveryCompleted)
            {
                ContinueHotAssetsAfterRecoveryAsync(
                    bundleModule,
                    startHotCallBack,
                    hotFinish,
                    waiteDownLoad,
                    isCheckAssetsVersion,
                    hotFailed).Forget();
                return;
            }

            if (mTransactionRecoveryFailure != null)
            {
                Debug.LogError($"存在尚未恢复的多模块事务，已拒绝模块 {bundleModule} 热更新：{mTransactionRecoveryFailure}");
                hotFailed?.Invoke(bundleModule, null);
                return;
            }
            if (mIsTransactionRunning)
            {
                Debug.LogError($"多模块热更新事务执行期间不能启动独立模块热更新：{bundleModule}");
                hotFailed?.Invoke(bundleModule, null);
                return;
            }
            if (BundleSettings.Instance.bundleHotType==  BundleHotEnum.NoHot)
            {
                hotFinish?.Invoke(bundleModule);
                return;
            }

            //读取配置中的最大下载线程个数
            MAX_THREAD_COUNT = Mathf.Max(1, BundleSettings.Instance.MAX_THREAD_COUNT);
            mDownloadScheduler.SetTotalThreadCount(MAX_THREAD_COUNT);
            RegisterModuleCallbacks(bundleModule, startHotCallBack, hotFinish, hotFailed);

            // 同模块已经下载中时只合并调用方，不创建第二个下载器。
            if (mDownLoadingAssetsModuleDic.ContainsKey(bundleModule))
            {
                waiteDownLoad?.Invoke(bundleModule);
                return;
            }

            // 同模块已经排队时只合并回调，队列中仍然保留一个模块任务。
            if (mQueuedModuleSet.Contains(bundleModule))
            {
                waiteDownLoad?.Invoke(bundleModule);
                return;
            }

            if (mDownLoadingAssetsModuleDic.Count < MAX_THREAD_COUNT)
            {
                StartHotAssetsModule(bundleModule, isCheckAssetsVersion);
            }
            else
            {
                waiteDownLoad?.Invoke(bundleModule);
                mQueuedModuleSet.Add(bundleModule);
                mWaitDownLoadQueue.Enqueue(new WaitDownLoadModule
                {
                    bundleModule = bundleModule,
                    checkAssetsVersion = isCheckAssetsVersion
                });
            }
        }

        /// <summary>
        /// 执行调用方显式指定的多模块事务；不会自动追加 Shared 或其消费者。
        /// </summary>
        /// <summary>
        /// 启动显式多模块事务；与旧单模块队列互斥，返回结果而不把业务异常吞掉。
        /// </summary>
        public async UniTask<HotUpdateTransactionResult> HotAssetsTransactionAsync(HotUpdateTransactionRequest request)
        {
            await EnsureRecoveryAsync();

            if (mTransactionRecoveryFailure != null)
            {
                return new HotUpdateTransactionResult
                {
                    Succeeded = false,
                    Message = "上次多模块热更新事务恢复失败，已阻止覆盖现有资源。请根据日志检查磁盘状态后重启应用。",
                    Exception = mTransactionRecoveryFailure,
                    OrderedModules = request?.OrderedModules
                };
            }
            if (mIsTransactionRunning || mDownLoadingAssetsModuleDic.Count > 0 || mWaitDownLoadQueue.Count > 0)
            {
                return new HotUpdateTransactionResult
                {
                    Succeeded = false,
                    Message = "已有热更新任务正在执行，请等待其结束后再启动多模块事务。",
                    OrderedModules = request?.OrderedModules
                };
            }

            MAX_THREAD_COUNT = Mathf.Max(1, BundleSettings.Instance.MAX_THREAD_COUNT);
            // 每次事务开始都读取最新配置，防止运行时修改线程预算后仍使用旧值。
            mDownloadScheduler.SetTotalThreadCount(MAX_THREAD_COUNT);
            mIsTransactionRunning = true;
            try
            {
                return await mTransactionCoordinator.ExecuteAsync(request);
            }
            catch (Exception exception)
            {
                return new HotUpdateTransactionResult
                {
                    Succeeded = false,
                    Message = $"无法启动多模块热更新事务：{exception.Message}",
                    Exception = exception,
                    OrderedModules = request?.OrderedModules
                };
            }
            finally
            {
                mIsTransactionRunning = false;
            }
        }

        private async UniTask ContinueHotAssetsAfterRecoveryAsync(
            string bundleModule,
            Action<string> startHotCallBack,
            Action<string> hotFinish,
            Action<string> waitDownload,
            bool checkAssetsVersion,
            Action<string, HotFileInfo> hotFailed)
        {
            await EnsureRecoveryAsync();
            HotAssets(
                bundleModule,
                startHotCallBack,
                hotFinish,
                waitDownload,
                checkAssetsVersion,
                hotFailed);
        }

        private async UniTask EnsureRecoveryAsync()
        {
            if (mRecoveryCompleted)
                return;
            await mRecoveryGate.WaitAsync(default);
            try
            {
                if (mRecoveryCompleted)
                    return;
                try
                {
                    await mTransactionCoordinator.RecoverInterruptedTransactionsAsync();
                }
                catch (Exception exception)
                {
                    mTransactionRecoveryFailure = exception;
                    Debug.LogError($"恢复中断的多模块热更新事务失败，后续事务将保持可诊断失败：{exception}");
                }
                mRecoveryCompleted = true;
            }
            finally
            {
                mRecoveryGate.Release();
            }
        }

        /// <summary>
        /// 将事务模块加入主线程更新与全局线程调度，但不加入旧单模块回调字典。
        /// </summary>
        /// <summary>
        /// 将组事务模块接入主线程更新和全局下载调度，但不接入旧单模块完成回调。
        /// </summary>
        internal void ActivateCoordinatedModule(HotAssetsModule module)
        {
            if (!mDownLoadAssetsModuleList.Contains(module))
                mDownLoadAssetsModuleList.Add(module);
            mDownloadScheduler.Register(module);
        }

        /// <summary>
        /// 组事务模块准备完成或失败后移出活动集合，释放其下载额度。
        /// </summary>
        internal void DeactivateCoordinatedModule(HotAssetsModule module)
        {
            mDownLoadAssetsModuleList.Remove(module);
            mDownloadScheduler.Unregister(module);
        }

        /// <summary>
        /// 下载器创建后刷新额度；这样首个模块不会在其他模块尚未启动时独占预算。
        /// </summary>
        internal void RefreshCoordinatedDownloadAllocation()
        {
            // 下载器已经创建后再次应用额度，确保本批所有模块合计不超过全局线程上限。
            mDownloadScheduler.SetTotalThreadCount(MAX_THREAD_COUNT);
        }

        internal void MarkTransactionRecoveryFailure(Exception exception)
        {
            // 保留首次恢复故障作为门禁根因，禁止后续异常覆盖最接近现场的诊断。
            if (mTransactionRecoveryFailure == null)
                mTransactionRecoveryFailure = exception;
        }

        /// <summary>
        /// 登记调用方回调，同模块所有请求共享一次真实热更操作。
        /// </summary>
        private void RegisterModuleCallbacks(string bundleModule, Action<string> startHot, Action<string> hotFinish,
            Action<string, HotFileInfo> hotFailed)
        {
            if (!mModuleRequestCallbacks.TryGetValue(bundleModule, out List<HotUpdateRequestCallbacks> callbacks))
            {
                callbacks = new List<HotUpdateRequestCallbacks>();
                mModuleRequestCallbacks.Add(bundleModule, callbacks);
            }

            callbacks.Add(new HotUpdateRequestCallbacks
            {
                startHot = startHot,
                hotFinish = hotFinish,
                hotFailed = hotFailed
            });
        }

        /// <summary>
        /// 启动一个模块的唯一热更任务。
        /// </summary>
        private void StartHotAssetsModule(string bundleModule, bool isCheckAssetsVersion)
        {
            HotAssetsModule assetsModule = GetOrNewAssetModule(bundleModule);
            mDownLoadingAssetsModuleDic.Add(bundleModule, assetsModule);
            if (!mDownLoadAssetsModuleList.Contains(assetsModule))
                mDownLoadAssetsModuleList.Add(assetsModule);
            mDownloadScheduler.Register(assetsModule);

            // 管理器事件只允许订阅一次，避免重复热更时同一个模块被重复结算。
            assetsModule.OnDownLoadAllAssetsFinish -= HotModuleAssetsFinish;
            assetsModule.OnDownLoadAllAssetsFinish += HotModuleAssetsFinish;
            assetsModule.OnDownLoadAllAssetsFailed -= HotModuleAssetsFailed;
            assetsModule.OnDownLoadAllAssetsFailed += HotModuleAssetsFailed;
            // 旧单模块入口仍由管理器负责生命周期登记；模块完成时会回到 HotModuleAssetsFinish 释放额度。
            assetsModule.StartHotAssets(
                () =>
                {
                    MultipleThreadBalancing();
                    NotifyModuleStarted(bundleModule);
                },
                null,
                isCheckAssetsVersion);
        }

        private void NotifyModuleStarted(string bundleModule)
        {
            if (!mModuleRequestCallbacks.TryGetValue(bundleModule, out List<HotUpdateRequestCallbacks> callbacks))
                return;
            foreach (HotUpdateRequestCallbacks callback in callbacks.ToArray())
            {
                try
                {
                    callback.startHot?.Invoke(bundleModule);
                }
                catch (Exception exception)
                {
                    // 单个业务回调异常不能阻断同模块其他调用方收到状态通知。
                    Debug.LogError($"模块 {bundleModule} 开始回调执行异常：{exception}");
                }
            }
        }
        public HotAssetsModule GetOrNewAssetModule(string bundleModule)
        {
            HotAssetsModule assetsModule = null;
            if (mAllAssetsModuleDic.ContainsKey(bundleModule))
            {
                assetsModule = mAllAssetsModuleDic[bundleModule];
            }
            else
            {
                assetsModule = new HotAssetsModule(bundleModule,ZMAsset.Instance);
                assetsModule.StateChanged += ForwardModuleStateChanged;
                mAllAssetsModuleDic.Add(bundleModule, assetsModule);
            }
            return assetsModule;
        }
  
        /// <summary>
        /// 检测资源版本是否需要热更，并返回需要下载的热更大小。
        /// </summary>
        /// <param name="bundleModule">热更模块</param>
        public async UniTask<HotUpdateVersionCheckResult> CheckAssetsVersionAsync(string bundleModule)
        {
            await EnsureRecoveryAsync();
            if (mTransactionRecoveryFailure != null)
                return HotUpdateVersionCheckResult.CreateUnableToConfirm(
                    "上次热更新事务恢复失败，已阻止新的版本检查。",
                    mTransactionRecoveryFailure);

            if (BundleSettings.Instance.bundleHotType == BundleHotEnum.NoHot)
            {
                Debug.Log($"模块 {bundleModule} 热更类型为 NoHot，跳过热更检测");
                return new HotUpdateVersionCheckResult(false, 0);
            }

            HotAssetsModule assetsModule = GetOrNewAssetModule(bundleModule);

            (bool isHot, float sizeMb) versionResult;
            try
            {
                // 只有成功取得并校验远端清单，版本检查才允许产生确定结论。
                versionResult = await assetsModule.CheckAssetsVersionAsync();
            }
            catch (Exception exception)
            {
                // 网络、HTTP 或清单格式失败都必须返回“无法确认”，不能伪装成无更新并初始化模块。
                string message = $"模块 {bundleModule} 无法确认资源版本，请检查网络或清单服务后重试。";
                Debug.LogError($"{message} 原因：{exception}");
                return HotUpdateVersionCheckResult.CreateUnableToConfirm(message, exception);
            }

            if (!versionResult.isHot)
            {
                // 只有确认无更新时才初始化当前模块，Unknown 状态不会进入这里。
                bool initialized = await ZMAsset.Modules.InitializeAsync(bundleModule);
                if (!initialized)
                {
                    throw new InvalidOperationException(
                        $"模块 {bundleModule} 已确认无需热更，但资源配置初始化失败，已阻止继续进入业务。");
                }
            }
            return new HotUpdateVersionCheckResult(versionResult.isHot, versionResult.sizeMb);
        }
        /// <summary>
        /// 获取热更模块
        /// </summary>
        /// <param name="bundleModule"></param>
        /// <returns></returns>
        public HotAssetsModule GetHotAssetsModule(string bundleModule)
        {
            if (mAllAssetsModuleDic.ContainsKey(bundleModule))
            {
                return mAllAssetsModuleDic[bundleModule];
            }
            return null;
        }

        /// <summary>
        /// 返回模块只读状态；业务层应使用该方法观察状态，不应直接驱动 HotAssetsModule 生命周期。
        /// </summary>
        public HotAssetsModuleState GetHotAssetsModuleState(string bundleModule)
        {
            HotAssetsModule module = GetHotAssetsModule(bundleModule);
            if (module == null)
                return null;

            return module.GetStateSnapshot();
        }

        private void ForwardModuleStateChanged(HotAssetsModuleState state)
        {
            Action<HotAssetsModuleState> handlers = StateChanged;
            if (handlers == null)
                return;

            foreach (Action<HotAssetsModuleState> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(state);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"模块 {state.BundleModule} 状态监听器执行异常：{exception}");
                }
            }
        }
        /// <summary>
        /// 热更模块资源完成
        /// </summary>
        /// <param name="bundleModule"></param>
        private void HotModuleAssetsFinish(string bundleModule)
        {
            ReleaseDownloadModule(bundleModule);
            StartWaitingModuleOrBalance();
            // 模块事件只会在快照切换和配置初始化全部成功后触发。
            NotifyModuleSucceeded(bundleModule);
        }

        /// <summary>
        /// 热更失败后释放模块下载槽，但不初始化不完整的资源配置。
        /// </summary>
        private void HotModuleAssetsFailed(string bundleModule, HotFileInfo failedFile)
        {
            string failedFileName = failedFile == null ? "未知文件" : failedFile.abName;
            Debug.LogError($"模块 {bundleModule} 热更终止，失败文件：{failedFileName}");
            ReleaseDownloadModule(bundleModule);
            StartWaitingModuleOrBalance();
            NotifyModuleFailed(bundleModule, failedFile);
        }

        private void NotifyModuleSucceeded(string bundleModule)
        {
            if (!mModuleRequestCallbacks.TryGetValue(bundleModule, out List<HotUpdateRequestCallbacks> callbacks))
                return;
            mModuleRequestCallbacks.Remove(bundleModule);
            foreach (HotUpdateRequestCallbacks callback in callbacks)
            {
                try
                {
                    callback.hotFinish?.Invoke(bundleModule);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"模块 {bundleModule} 成功回调执行异常：{exception}");
                }
            }
        }

        private void NotifyModuleFailed(string bundleModule, HotFileInfo failedFile)
        {
            if (!mModuleRequestCallbacks.TryGetValue(bundleModule, out List<HotUpdateRequestCallbacks> callbacks))
                return;
            mModuleRequestCallbacks.Remove(bundleModule);
            foreach (HotUpdateRequestCallbacks callback in callbacks)
            {
                try
                {
                    callback.hotFailed?.Invoke(bundleModule, failedFile);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"模块 {bundleModule} 失败回调执行异常：{exception}");
                }
            }
        }

        /// <summary>
        /// 从活动下载集合中移除已结束模块，成功和失败共用同一生命周期出口。
        /// </summary>
        private void ReleaseDownloadModule(string bundleModule)
        {
            if (!mDownLoadingAssetsModuleDic.TryGetValue(bundleModule, out HotAssetsModule assetsModule))
                return;

            mDownLoadAssetsModuleList.Remove(assetsModule);
            mDownLoadingAssetsModuleDic.Remove(bundleModule);
            mDownloadScheduler.Unregister(assetsModule);
        }

        /// <summary>
        /// 优先启动等待模块，没有等待任务时再重新分配剩余下载线程。
        /// </summary>
        private void StartWaitingModuleOrBalance()
        {
            if (mWaitDownLoadQueue.Count > 0)
            {
                WaitDownLoadModule downLoadModule = mWaitDownLoadQueue.Dequeue();
                mQueuedModuleSet.Remove(downLoadModule.bundleModule);
                // 只有一个模块结束并释放额度后，才从等待队列启动下一个模块。
                StartHotAssetsModule(downLoadModule.bundleModule, downLoadModule.checkAssetsVersion);
                return;
            }

            MultipleThreadBalancing();
        }
        /// <summary>
        /// 多线程均衡
        /// </summary>
        public void MultipleThreadBalancing()
        {
            // 保留旧公开入口的兼容性，实际分配统一委托给稳定顺序调度器。
            mDownloadScheduler.SetTotalThreadCount(MAX_THREAD_COUNT);
        }
        /// <summary>
        /// 主线程更新
        /// </summary>
        public void OnMainThreadUpdate()
        {
            for (int i = 0; i < mDownLoadAssetsModuleList.Count; i++)
            {
                mDownLoadAssetsModuleList[i].OnMainThreadUpdate();
            }
        }
    }
}
