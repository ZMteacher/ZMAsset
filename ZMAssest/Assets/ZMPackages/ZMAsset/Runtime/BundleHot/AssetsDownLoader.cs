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
using UnityEngine;

namespace ZM.ZMAsset
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
    public class AssetsDownLoader
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
        private HotAssetsModule mCurHotAssetsModule;
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
        {
            this.mCurHotAssetsModule = assetModule;
            this.mDownLoadQueue = downLoadQueue;
            this.mAssetsDownLoadUrl = downloadUrl;
            this.mHotAssetsSavePath = hotAssetsSavePath;
            this.OnDownLoadSuccess = downLoadSuccess;
            this.OnDownLoadFailed = downLoadFailed;
            this.OnDownLoadFinish = downLoadFinish;
        }

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
        public void StartDownLoadNextBundle()
        {
            ScheduleAvailableDownloads();
        }
        /// <summary>
        /// 开始下载下一个AssetBundle
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
                int threadLimit = Math.Max(1, MAX_THREAD_COUNT);
                while (mDownLoadQueue.Count > 0 && mAllDownLoadThreadList.Count < threadLimit)
                {
                    HotFileInfo hotFileInfo = mDownLoadQueue.Dequeue();
                    DownLoadThread downloadItem = new DownLoadThread(
                        mCurHotAssetsModule,
                        hotFileInfo,
                        mAssetsDownLoadUrl,
                        mHotAssetsSavePath);
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
                downloadItem.StartDownLoad(DownLoadSuccess, DownLoadFailed);
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
        public void DownLoadSuccess(DownLoadThread downLoadThread,HotFileInfo hotFileInfo)
        {
            lock (mDownloadStateLock)
            {
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
        public void DownLoadFailed(DownLoadThread downLoadThread, HotFileInfo hotFileInfo)
        {
            lock (mDownloadStateLock)
            {
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
