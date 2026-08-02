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
namespace ZM.ZMAsset
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
        /// <summary>
        /// 最大并发下载线程个数
        /// </summary>
        private int MAX_THREAD_COUNT = 3;
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

        public void HotAssets(string bundleModule, Action<string> startHotCallBack, Action<string> hotFinish, Action<string> waiteDownLoad, 
            bool isCheckAssetsVersion = true, Action<string, HotFileInfo> hotFailed = null)
        {
            if (BundleSettings.Instance.bundleHotType==  BundleHotEnum.NoHot)
            {
                hotFinish?.Invoke(bundleModule);
                return;
            }

            //读取配置中的最大下载线程个数
            MAX_THREAD_COUNT = Mathf.Max(1, BundleSettings.Instance.MAX_THREAD_COUNT);
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

            // 管理器事件只允许订阅一次，避免重复热更时同一个模块被重复结算。
            assetsModule.OnDownLoadAllAssetsFinish -= HotModuleAssetsFinish;
            assetsModule.OnDownLoadAllAssetsFinish += HotModuleAssetsFinish;
            assetsModule.OnDownLoadAllAssetsFailed -= HotModuleAssetsFailed;
            assetsModule.OnDownLoadAllAssetsFailed += HotModuleAssetsFailed;
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
                mAllAssetsModuleDic.Add(bundleModule, assetsModule);
            }
            return assetsModule;
        }
  
        /// <summary>
        /// 检测资源版本是否需要热更
        /// </summary>
        /// <param name="bundleModule">热更模块</param>
        /// <param name="callBack">热更回调</param>
        public void  CheckAssetsVersion(string bundleModule, Action<bool, float> callBack)
        {
            if (BundleSettings.Instance.bundleHotType == BundleHotEnum.NoHot)
            {
                Debug.Log("NoHot加载模式，不需要热更");
                callBack?.Invoke(false, 0);
                return;
            }
            HotAssetsModule assetsModule = GetOrNewAssetModule(bundleModule);
            assetsModule.CheckAssetsVersion(async (isHot,sizem)=>
            {
               if (!isHot)
               { 
                   await ZMAsset.InitAssetsModule(bundleModule);
               }
               callBack?.Invoke(isHot, sizem);
           } );
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
            //获取当前正在下载热更资源模块的一个长度个数
            int count = mDownLoadingAssetsModuleDic.Count;
            // 没有活动模块时无需做线程均衡，同时避免出现除零和无效线程数。
            if (count <= 0)
                return;
            //计算多线程均衡后的线程分配个数
            //以最大下载线程个数为3 举例子
            //1.  3/1=3 最大并发下载线程个数为3  （偶数）
            //2.  3/2=1.5 向上取整 2 1 （奇数）
            //3.  3/3= 1  每一个模块 都拥有一个下载线程 
            float threadCount= MAX_THREAD_COUNT * 1.0f / count;
            //主下载线程个数
            int mainThreadCount = 0;
            //通过(int) 进行强转  (int)强转：表示向下强转
            int threadBalancingCount = (int)threadCount;

            if ((int)threadCount< threadCount)
            {
                //向上取整
                mainThreadCount = Mathf.CeilToInt(threadCount);
                //向下取整
                threadBalancingCount = Mathf.FloorToInt(threadCount);
            }
            //多线程均衡
            int i = 0;
            foreach (var item in mDownLoadingAssetsModuleDic.Values)
            {
                if (mainThreadCount!=0&&i==0)
                {
                    item.SetDownLoadThreadCount(mainThreadCount);//设置主下载线程个数
                }
                else
                {
                    item.SetDownLoadThreadCount(threadBalancingCount);
                }
                i++;
            }
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
