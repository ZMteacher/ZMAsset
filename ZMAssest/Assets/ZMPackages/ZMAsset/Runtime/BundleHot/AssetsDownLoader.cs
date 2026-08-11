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
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ZM.Asset
{
    public class DownLoadEventHandler
    {
        public DownLoadEvent downLoadEvent;//回调
        public HotFileInfo hotfileInfo;
    }

    /// <summary>
    /// 下载事件
    /// </summary>
    /// <param name="hotFile"></param>
    public delegate void DownLoadEvent(HotFileInfo hotFile);
    /// <summary>
    /// 多线程资源下载器
    /// </summary>
    public class AssetsDownLoader : IDisposable
    {
        /// <summary>
        /// 最大下载线程个数
        /// </summary>
        public int MAX_THREAD_COUNT = 3;
        /// <summary>
        /// 资源文件下载地址
        /// </summary>
        private string mAssetsDownLoadUrl;
        /// <summary>
        /// 热更文件储存路径
        /// </summary>
        private string mHotAssetsSavePath;
        /// <summary>
        /// 当前热更的资源模块
        /// </summary>
        private string mModuleName;

        /// <summary>
        /// 下载线程写入字节后的统计回调；由热更新模块持有统计状态，下载器不再反向依赖模块对象。
        /// </summary>
        private Action<int> mBytesDownloaded;
        /// <summary>
        /// 文件下载队列
        /// </summary>
        private Queue<HotFileInfo> mDownLoadQueue;
        /// <summary>
        /// 文件下载成功回调
        /// </summary>
        private DownLoadEvent OnDownLoadSuccess;
        /// <summary>
        /// 文件下载失败回调
        /// </summary>
        private DownLoadEvent OnDownLoadFailed;
        /// <summary>
        /// 所有文件下载完成的回调
        /// </summary>
        private DownLoadEvent OnDownLoadFinish;
        /// <summary>
        /// 下载回调的列表
        /// </summary>
        private Queue<DownLoadEventHandler> mDownLoadEventQueue = new Queue<DownLoadEventHandler>();
        /// <summary>
        /// 当前所有正在下载的线程列表
        /// </summary>
        private List<DownLoadThread> mAllDownLoadThreadList = new List<DownLoadThread>();
        /// <summary>
        /// 下载队列和活动任务共享同一把锁，保证多个下载线程回调时终态只计算一次。
        /// </summary>
        private readonly object mDownloadStateLock = new object();
        private bool mHasDownloadFailed;
        private bool mTerminalEventQueued;
        private HotFileInfo mFirstFailedHotFile;
        /// <summary>
        /// 整批下载共享一个取消令牌；回滚前必须取消并等待全部文件任务退出。
        /// </summary>
        private readonly CancellationTokenSource mCancellationSource = new CancellationTokenSource();
        private bool mIsCancellationRequested;
        private bool mIsDisposed;

        /// <summary>
        /// 资源下载器
        /// </summary>
        /// <param name="assetModule">资源下载模块</param>
        /// <param name="downLoadQueue">资源下载队列</param>
        /// <param name="downloadUrl">资源下载地址</param>
        /// <param name="hotAssetsSavePath">热更文件储存路径</param>
        /// <param name="downLoadSuccess">文件下载成功回调</param>
        /// <param name="downLoadFailed">文件下载失败或出错的回调</param>
        /// <param name="downLoadFinish">所有文件下载完成的回调</param>
        public AssetsDownLoader(HotAssetsModule assetModule, Queue<HotFileInfo> downLoadQueue, string downloadUrl, string hotAssetsSavePath,
            DownLoadEvent downLoadSuccess, DownLoadEvent downLoadFailed, DownLoadEvent downLoadFinish)
            : this(
                assetModule.CurBundleModuleName,
                downLoadQueue,
                downloadUrl,
                hotAssetsSavePath,
                assetModule.AddDownloadedBytes,
                downLoadSuccess,
                downLoadFailed,
                downLoadFinish)
        {
        }

        /// <summary>
        /// Native 下载服务使用的构造入口；仅注入模块标识和统计回调，不改变现有下载队列算法。
        /// </summary>
        internal AssetsDownLoader(
            string moduleName,
            Queue<HotFileInfo> downLoadQueue,
            string downloadUrl,
            string hotAssetsSavePath,
            Action<int> bytesDownloaded,
            DownLoadEvent downLoadSuccess,
            DownLoadEvent downLoadFailed,
            DownLoadEvent downLoadFinish)
        {
            this.mModuleName = moduleName;
            this.mDownLoadQueue = downLoadQueue;
            this.mAssetsDownLoadUrl = downloadUrl;
            this.mHotAssetsSavePath = hotAssetsSavePath;
            this.mBytesDownloaded = bytesDownloaded;
            this.OnDownLoadSuccess = downLoadSuccess;
            this.OnDownLoadFailed = downLoadFailed;
            this.OnDownLoadFinish = downLoadFinish;
        }

        /// <summary>
        /// 启动当前队列；真实线程数量由 MAX_THREAD_COUNT 限制，最终完成事件只入队一次。
        /// </summary>
        public void StartThreadDownLoadQueue()
        {
            // 非法线程数至少降级为单线程，避免队列永远无法启动。
            if (MAX_THREAD_COUNT <= 0)
                MAX_THREAD_COUNT = 1;
            ScheduleAvailableDownloads();
        }
        /// <summary>
        /// 开始下载下一个AssetBundle
        /// </summary>
        /// <summary>
        /// 在一个下载任务结束后补充下一个任务；可由旧调用方继续使用。
        /// </summary>
        public void StartDownLoadNextBundle()
        {
            ScheduleAvailableDownloads();
        }
        /// <summary>
        /// 开始下载下一个AssetBundle
        /// </summary>
        /// <summary>
        /// StartDownLoadNextBundle 的兼容别名，保持旧 API 行为不变。
        /// </summary>
        public void DownLoadNextBundle()
        {
            ScheduleAvailableDownloads();
        }

        /// <summary>
        /// 在锁内补齐可用下载通道，并在全部任务结束后生成唯一终态事件。
        /// </summary>
        private void ScheduleAvailableDownloads()
        {
            List<DownLoadThread> downloadItems = new List<DownLoadThread>();
            DownLoadEvent terminalEvent = null;
            HotFileInfo terminalFile = null;

            lock (mDownloadStateLock)
            {
                if (mIsCancellationRequested)
                    return;

                int threadLimit = Math.Max(1, MAX_THREAD_COUNT);
                while (mDownLoadQueue.Count > 0 && mAllDownLoadThreadList.Count < threadLimit)
                {
                    HotFileInfo hotFileInfo = mDownLoadQueue.Dequeue();
                    DownLoadThread downloadItem = new DownLoadThread(
                        mModuleName,
                        hotFileInfo,
                        mAssetsDownLoadUrl,
                        mHotAssetsSavePath,
                        mBytesDownloaded);
                    // 先登记活动任务再启动，避免极快失败时回调先于列表登记。
                    mAllDownLoadThreadList.Add(downloadItem);
                    downloadItems.Add(downloadItem);
                }

                if (mDownLoadQueue.Count == 0 &&
                    mAllDownLoadThreadList.Count == 0 &&
                    !mTerminalEventQueued)
                {
                    mTerminalEventQueued = true;
                    terminalEvent = mHasDownloadFailed ? OnDownLoadFailed : OnDownLoadFinish;
                    terminalFile = mFirstFailedHotFile;
                }
            }

            foreach (DownLoadThread downloadItem in downloadItems)
            {
                Debug.Log("Start DownLoad AssetBundle MAX_THREAD_COUNT:" + MAX_THREAD_COUNT);
                downloadItem.StartDownLoad(DownLoadSuccess, DownLoadFailed, mCancellationSource.Token);
            }

            if (terminalEvent != null)
            {
                TriggerCallBackInMainThread(new DownLoadEventHandler
                {
                    downLoadEvent = terminalEvent,
                    hotfileInfo = terminalFile
                });
            }
        }
        /// <summary>
        /// AssetBundle文件下载成功
        /// </summary>
        /// <param name="downLoadThread"></param>
        /// <param name="hotFileInfo"></param>
        /// <summary>
        /// 接收后台成功结果；先从活动集合移除，再安排后继下载和主线程通知。
        /// </summary>
        public void DownLoadSuccess(DownLoadThread downLoadThread,HotFileInfo hotFileInfo)
        {
            lock (mDownloadStateLock)
            {
                if (mIsCancellationRequested)
                    return;
                mAllDownLoadThreadList.Remove(downLoadThread);
                // 单文件事件必须在移除活动任务的同一临界区内入队，确保整批终态永远排在其后。
                TriggerCallBackInMainThread(new DownLoadEventHandler
                {
                    downLoadEvent = OnDownLoadSuccess,
                    hotfileInfo = hotFileInfo
                });
            }
            DownLoadNextBundle();
        }
        /// <summary>
        /// AssetBundle文件下载失败
        /// </summary>
        /// <param name="downLoadThread"></param>
        /// <param name="hotFileInfo"></param>
        /// <summary>
        /// 接收后台失败结果；记录首个失败文件，但仍等待其余活动任务收口。
        /// </summary>
        public void DownLoadFailed(DownLoadThread downLoadThread, HotFileInfo hotFileInfo)
        {
            lock (mDownloadStateLock)
            {
                if (mIsCancellationRequested)
                    return;
                mAllDownLoadThreadList.Remove(downLoadThread);
                mHasDownloadFailed = true;
                // 保留第一个失败文件，最终失败回调可以给出稳定、可诊断的目标。
                if (mFirstFailedHotFile == null)
                    mFirstFailedHotFile = hotFileInfo;
            }
            DownLoadNextBundle();
        }

        /// <summary>
        /// 在主线程中触发回调
        /// </summary>
        /// <param name="downLoadEventHandler"></param>
        /// <summary>
        /// 后台线程只能把事件放入队列，真正回调统一由 Unity 主线程 OnMainThreadUpdate 执行。
        /// </summary>
        public void TriggerCallBackInMainThread(DownLoadEventHandler downLoadEventHandler)
        {
            lock (mDownLoadEventQueue)
            {
                mDownLoadEventQueue.Enqueue(downLoadEventHandler);
            }
        }
        /// <summary>
        /// 主线程更新接口
        /// </summary>
        /// <summary>
        /// 每帧消费一个下载事件，保证 Unity 对象和业务回调不在后台线程执行。
        /// </summary>
        public void OnMainThreadUpdate()
        {
            DownLoadEventHandler downLoadEventHandler = null;
            lock (mDownLoadEventQueue)
            {
                if (mDownLoadEventQueue.Count > 0)
                    downLoadEventHandler = mDownLoadEventQueue.Dequeue();
            }
            downLoadEventHandler?.downLoadEvent?.Invoke(downLoadEventHandler.hotfileInfo);
        }

        /// <summary>
        /// 取消未启动和正在执行的下载，并等待后台任务释放网络流与文件句柄。
        /// </summary>
        public async Task CancelAndWaitAsync()
        {
            List<Task> activeTasks = new List<Task>();
            lock (mDownloadStateLock)
            {
                if (!mIsCancellationRequested)
                {
                    mIsCancellationRequested = true;
                    mTerminalEventQueued = true;
                    mDownLoadQueue.Clear();
                    mCancellationSource.Cancel();
                }

                foreach (DownLoadThread downloadThread in mAllDownLoadThreadList)
                    activeTasks.Add(downloadThread.CompletionTask);
            }

            try
            {
                await Task.WhenAll(activeTasks);
            }
            catch (OperationCanceledException)
            {
                // 取消是调用方主动终止事务的预期结果，不作为下载失败再次上报。
            }
            finally
            {
                lock (mDownloadStateLock)
                    mAllDownLoadThreadList.Clear();
                lock (mDownLoadEventQueue)
                    mDownLoadEventQueue.Clear();
            }
        }

        /// <summary>
        /// 终态后释放取消令牌持有的系统资源；调用方必须先等待下载任务退出。
        /// </summary>
        public void Dispose()
        {
            lock (mDownloadStateLock)
            {
                if (mIsDisposed)
                    return;
                mIsDisposed = true;
                mCancellationSource.Dispose();
            }
        }
        public void RemoveDownLoadThread(DownLoadThread downLoadThread)
        {
            lock (mDownloadStateLock)
            {
                if (mAllDownLoadThreadList.Contains(downLoadThread))
                {
                    mAllDownLoadThreadList.Remove(downLoadThread);
                } 
            }
        }
    }
}
