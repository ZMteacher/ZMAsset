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
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 资源下载线程
    /// </summary>
    public class DownLoadThread
    {
        private static readonly SemaphoreSlim _fileSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 下载完成回调
        /// </summary>
        private Action<DownLoadThread, HotFileInfo> OnDownLoadSuccess;

        /// <summary>
        /// 下载失败回调
        /// </summary>
        public Action<DownLoadThread, HotFileInfo> OnDownLoadFailed;
        /// <summary>
        /// 当前热更的资源模块
        /// </summary>
        private string _mCurBundleModuleName;

        /// <summary>
        /// 当前热更的资源模块
        /// </summary>
        private HotAssetsModule mCurHotAssetsModule;

        /// <summary>
        /// 当前热更的文件信息
        /// </summary>
        private HotFileInfo mHotFileInfo;

        /// <summary>
        /// 文件下载的地址
        /// </summary>
        private string mDownLoadUrl;

        /// <summary>
        /// 下载下的文件储存的地址
        /// </summary>
        private string mFileSavePath;

        /// <summary>
        /// 下载的大小
        /// </summary>
        private float mDownLoadSizeKB;

        /// <summary>
        /// 当前下载的次数
        /// </summary>
        private int curDownLoadCount;

        /// <summary>
        /// 最大尝试下载次数
        /// </summary>
        private const int MAX_TRY_DOWNLOAD_COUNT = 3;

        /// <summary>
        /// 资源下载线程
        /// </summary>
        /// <param name="assetsModule">资源所属模块</param>
        /// <param name="hotFileInfo">需要下载热更的资源</param>
        /// <param name="downLoadUrl">资源下载地址</param>
        /// <param name="fileSavePath">文件储存地址</param>
        public DownLoadThread(HotAssetsModule assetsModule, HotFileInfo hotFileInfo, string downLoadUrl,
            string fileSavePath)
        {
            this.mCurHotAssetsModule = assetsModule;
            this._mCurBundleModuleName = assetsModule.CurBundleModuleName;
            this.mHotFileInfo = hotFileInfo;
            this.mFileSavePath = fileSavePath + "/" + hotFileInfo.abName;
            this.mDownLoadUrl = downLoadUrl + "/" + hotFileInfo.abName;
        }

        public DownLoadThread(string bundleModule, HotFileInfo hotFileInfo, string downLoadUrl,
            string fileSavePath)
        {
            this.mHotFileInfo = hotFileInfo;
            this._mCurBundleModuleName = bundleModule;
            this.mFileSavePath = fileSavePath + "/" + hotFileInfo.abName;
            this.mDownLoadUrl = downLoadUrl + "/" + hotFileInfo.abName;
        }

        /// <summary>
        /// 开始通过子线程下载资源
        /// </summary>
        /// <param name="downLoadSuccess">下载成功回调</param>
        /// <param name="downLoadFailed">下载失败回调</param>
        public void StartDownLoad(Action<DownLoadThread, HotFileInfo> downLoadSuccess, Action<DownLoadThread, HotFileInfo> downLoadFailed)
        {
    
            curDownLoadCount++;
            OnDownLoadSuccess = downLoadSuccess;
            OnDownLoadFailed = downLoadFailed;
            Debug.Log("StartDownLoad ModuelEnum:" + mCurHotAssetsModule.CurBundleModuleName + " AssetBundle URL:" + mDownLoadUrl);
            Task.Run(() =>
            {
                //这里的代码在子线程中执行
                try
                {
                    HttpWebRequest request = WebRequest.Create(mDownLoadUrl) as HttpWebRequest;
                    request.Method = "GET";
                    //发起请求
                    HttpWebResponse response = request.GetResponse() as HttpWebResponse;
            
                    //创建本地文件流，使用 using 确保异常时也能释放文件句柄
                    using (FileStream fileStream = File.Create(mFileSavePath))
                    using (var stream = response.GetResponseStream())
                    {
                        byte[] buffer = new byte[512];
                        int size = stream.Read(buffer, 0, buffer.Length);
                        
                        while (size > 0)
                        {
                            fileStream.Write(buffer, 0, size);
                            size = stream.Read(buffer, 0, buffer.Length);
                            //1mb=1024kb 1kb=1024字节
                            mDownLoadSizeKB += size;
                            //计算以m为单位的大小
                            mCurHotAssetsModule.AssetsDownLoadSizeM += ((size / 1024.0f) / 1024.0f);
                        }
                        //文件下载异常 或 下载完成的文件因网络问题或其他问题发生损坏 || MD5.GetMd5FromFile(mFileSavePath) != mHotFileInfo.md5
                        if (mDownLoadSizeKB == 0 || MD5.GetMd5FromFile(mFileSavePath) != mHotFileInfo.md5)
                        {
                            Debug.LogError("File DownLoad exception plase check file fileName:" + mHotFileInfo.abName + " fileUrl:" + mDownLoadUrl);
                            if (curDownLoadCount > MAX_TRY_DOWNLOAD_COUNT)
                            {
                                OnDownLoadFailed?.Invoke(this, mHotFileInfo);
                            }
                            else
                            {
                                Debug.LogError("文件下载失败，正在进行重新下载，下载次数" + curDownLoadCount);
                                StartDownLoad(OnDownLoadSuccess, OnDownLoadFailed);
                            }
                        }
                        else
                        {
                            Debug.Log("OnDownLoadSuccess ModuleEnum:" + mCurHotAssetsModule.CurBundleModuleName + " AssetBundleUrl:" + mDownLoadUrl + " FileSavePath:" + mFileSavePath);
                            OnDownLoadSuccess?.Invoke(this, mHotFileInfo);
                        }
                    }
                    
                }
                catch (Exception e)
                {
                    Debug.LogError("DownLoad AssetBundle Error Url:" + mDownLoadUrl + " Exception:" + e);
                    if (curDownLoadCount > MAX_TRY_DOWNLOAD_COUNT)
                    {
                        OnDownLoadFailed?.Invoke(this, mHotFileInfo);
                    }
                    else
                    {
                        Debug.LogError("文件下载失败，正在进行重新下载，下载次数" + curDownLoadCount);
                        StartDownLoad(OnDownLoadSuccess, OnDownLoadFailed);
                    }
                }
            });
        }

         
        // Lazy 保证只有成功登记到字典中的任务才会真正发起网络请求。
        private static readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _downloadTasks = new();
        
        public async Task<bool> StartDownLoadAsync()
        {
            // URL、落盘路径和期望摘要完全一致时才共享任务，避免版本切换时复用旧校验结果。
            string downloadTaskKey =
                $"{mDownLoadUrl}|{Path.GetFullPath(mFileSavePath)}|{mHotFileInfo.md5}";
            return await GetOrCreateDownloadTask(downloadTaskKey);
        }

        private async Task<bool> GetOrCreateDownloadTask(string taskKey)
        {
            Lazy<Task<bool>> candidate = new Lazy<Task<bool>>(
                InternalStartDownLoadAsync,
                LazyThreadSafetyMode.ExecutionAndPublication);
            Lazy<Task<bool>> sharedTask = _downloadTasks.GetOrAdd(taskKey, candidate);
            bool ownsTaskEntry = ReferenceEquals(candidate, sharedTask);

            try
            {
                return await sharedTask.Value;
            }
            finally
            {
                // 只有登记字典成功的所有者负责移除，旧等待者不会误删同键的新下载任务。
                if (ownsTaskEntry)
                    _downloadTasks.TryRemove(taskKey, out _);
            }
        }

        /// <summary>
        /// 执行带重试的异步下载。
        /// </summary>
        private async Task<bool> InternalStartDownLoadAsync()
        {
            // 重试必须在当前任务内部循环，不能再次进入任务去重入口，否则会等待当前任务自身并永久挂起。
            while (curDownLoadCount < MAX_TRY_DOWNLOAD_COUNT)
            {
                curDownLoadCount++;
                try
                {
                    bool fileIsComplete = false;
                    byte[] buffer = null;
                    using (UnityWebRequest webRequest = UnityWebRequest.Get(mDownLoadUrl))
                    {
                        webRequest.timeout = 60;
                        await webRequest.SendWebRequest();

                        if (webRequest.result == UnityWebRequest.Result.Success)
                        {
                            buffer = webRequest.downloadHandler.data;
                        }
                        else
                        {
                            Debug.LogError("FixDownLoad File DownLoad exception webrequest.result:" + webRequest.result);
                        }
                    }

                    if (buffer != null && buffer.Length > 0)
                    {
                        await _fileSemaphore.WaitAsync();
                        try
                        {
                            // 文件写入完成后再做完整性校验，写入异常必须进入下一次重试。
                            await File.WriteAllBytesAsync(mFileSavePath, buffer);
                        }
                        finally
                        {
                            _fileSemaphore.Release();
                        }

                        // 验证下载文件是否完整，校验通过后本次下载才算成功。
                        fileIsComplete = MD5.GetMd5FromFile(mFileSavePath) == mHotFileInfo.md5;
                        mDownLoadSizeKB = buffer.Length;
                        if (!fileIsComplete)
                        {
                            Debug.LogError("FixDownLoad 文件下载完成，但文件已损坏");
                        }
                    }
                    else
                    {
                        Debug.LogError("FixDownLoad File DownLoad exception mDownLoadSizeKB ==0");
                        mDownLoadSizeKB = 0;
                    }

                    if (mDownLoadSizeKB > 0 && fileIsComplete)
                    {
                        Debug.Log("FixDownLoad OnDownLoadSuccess ModuleEnum:" + _mCurBundleModuleName + " AssetBundleUrl:" +
                                  mDownLoadUrl + " FileSavePath:" + mFileSavePath);
                        return true;
                    }

                    Debug.LogError("FixDownLoad File DownLoad exception plase check file fileName:" +
                                   mHotFileInfo.abName + " fileUrl:" + mDownLoadUrl);
                }
                catch (Exception e)
                {
                    Debug.LogError("FixDownLoad DownLoad AssetBundle Error Url:" + mDownLoadUrl + " Exception:" + e);
                }

                if (curDownLoadCount < MAX_TRY_DOWNLOAD_COUNT)
                {
                    Debug.LogError("FixDownLoad 文件下载失败，正在进行重新下载，下载次数" + curDownLoadCount);
                }
            }

            Debug.LogError("FixDownLoad 文件已达最大重试次数，下载失败，下载次数：" + curDownLoadCount);
            return false;
        }
    }
}
