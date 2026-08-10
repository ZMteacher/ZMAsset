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
        /// UnityWebRequest 下载进度回调（0~1），主线程触发，按固定间隔与最小变化量节流。
        /// 仅 StartDownLoadAsync 路径汇报；HttpWebRequest 热更路径不汇报。
        /// </summary>
        public Action<DownLoadThread, HotFileInfo, float> OnDownloadProgress;

        /// <summary>
        /// 当前热更资源所属模块名称。
        /// </summary>
        private string _mCurBundleModuleName;

        /// <summary>
        /// 下载字节统计回调；为空时表示调用方不需要累计批次下载量。
        /// </summary>
        private Action<int> mBytesDownloaded;

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
        /// 保存回调式下载对应的后台任务，使上层取消事务时可以等待文件句柄真正释放。
        /// </summary>
        public Task CompletionTask { get; private set; } = Task.CompletedTask;

        /// <summary>
        /// 资源下载线程
        /// </summary>
        /// <param name="assetsModule">资源所属模块</param>
        /// <param name="hotFileInfo">需要下载热更的资源</param>
        /// <param name="downLoadUrl">资源下载地址</param>
        /// <param name="fileSavePath">文件储存地址</param>
        public DownLoadThread(HotAssetsModule assetsModule, HotFileInfo hotFileInfo, string downLoadUrl,
            string fileSavePath)
            : this(
                assetsModule.CurBundleModuleName,
                hotFileInfo,
                downLoadUrl,
                fileSavePath,
                assetsModule.AddDownloadedBytes)
        {
        }

        public DownLoadThread(string bundleModule, HotFileInfo hotFileInfo, string downLoadUrl,
            string fileSavePath)
            : this(bundleModule, hotFileInfo, downLoadUrl, fileSavePath, null)
        {
        }

        /// <summary>
        /// Native 下载后端使用的构造入口；字节回调替代对 HotAssetsModule 的直接依赖，下载算法保持不变。
        /// </summary>
        internal DownLoadThread(
            string bundleModule,
            HotFileInfo hotFileInfo,
            string downLoadUrl,
            string fileSavePath,
            Action<int> bytesDownloaded)
        {
            this.mHotFileInfo = hotFileInfo;
            this._mCurBundleModuleName = bundleModule;
            this.mFileSavePath = fileSavePath + "/" + hotFileInfo.abName;
            this.mDownLoadUrl = downLoadUrl + "/" + hotFileInfo.abName;
            this.mBytesDownloaded = bytesDownloaded;
        }

        /// <summary>
        /// 开始通过子线程下载资源
        /// </summary>
        /// <param name="downLoadSuccess">下载成功回调</param>
        /// <param name="downLoadFailed">下载失败回调</param>
        public void StartDownLoad(
            Action<DownLoadThread, HotFileInfo> downLoadSuccess,
            Action<DownLoadThread, HotFileInfo> downLoadFailed,
            CancellationToken cancellationToken = default)
        {
            OnDownLoadSuccess = downLoadSuccess;
            OnDownLoadFailed = downLoadFailed;
            Debug.Log(
                $"开始下载资源模块：{_mCurBundleModuleName}，文件地址：{AssetLogUtility.SanitizeUrl(mDownLoadUrl)}");

            // 一个文件的全部重试都归属于同一个 Task；禁止递归创建无法追踪的新后台任务。
            CompletionTask = Task.Run(() => DownloadWithRetry(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// 在单一后台任务内完成下载和重试；取消属于事务控制流，不触发普通失败回调。
        /// </summary>
        /// <summary>
        /// 在同一个后台 Task 内循环重试；成功或最终失败只调用一次对应回调，取消则不伪装成失败。
        /// </summary>
        private void DownloadWithRetry(CancellationToken cancellationToken)
        {
            string safeDownloadUrl = AssetLogUtility.SanitizeUrl(mDownLoadUrl);
            while (curDownLoadCount < MAX_TRY_DOWNLOAD_COUNT)
            {
                cancellationToken.ThrowIfCancellationRequested();
                curDownLoadCount++;
                mDownLoadSizeKB = 0;

                try
                {
                    HttpWebRequest request = WebRequest.Create(mDownLoadUrl) as HttpWebRequest;
                    if (request == null)
                        throw new InvalidOperationException($"无法创建资源下载请求：{safeDownloadUrl}");

                    request.Method = "GET";
                    request.Timeout = 60000;
                    request.ReadWriteTimeout = 60000;

                    // Abort 可打断阻塞中的网络读取，保证取消事务不会无限等待后台线程。
                    using (cancellationToken.Register(request.Abort))
                    using (HttpWebResponse response = request.GetResponse() as HttpWebResponse)
                    using (Stream stream = response?.GetResponseStream())
                    using (FileStream fileStream = File.Create(mFileSavePath))
                    {
                        if (stream == null)
                            throw new IOException($"服务器没有返回文件流：{safeDownloadUrl}");

                        byte[] buffer = new byte[81920];
                        int size;
                        while ((size = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            fileStream.Write(buffer, 0, size);
                            mDownLoadSizeKB += size;
                            mBytesDownloaded?.Invoke(size);
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    bool fileIsValid = mDownLoadSizeKB > 0 &&
                                       string.Equals(
                                           MD5.GetMd5FromFile(mFileSavePath),
                                           mHotFileInfo.md5,
                                           StringComparison.OrdinalIgnoreCase);
                    if (fileIsValid)
                    {
                        Debug.Log($"资源下载并校验成功，模块：{_mCurBundleModuleName}，文件：{mHotFileInfo.abName}");
                        OnDownLoadSuccess?.Invoke(this, mHotFileInfo);
                        return;
                    }

                    Debug.LogError($"下载文件为空或 MD5 校验失败，模块：{_mCurBundleModuleName}，文件：{mHotFileInfo.abName}");
                }
                catch (OperationCanceledException)
                {
                    DeletePartialFile();
                    throw;
                }
                catch (WebException) when (cancellationToken.IsCancellationRequested)
                {
                    // HttpWebRequest.Abort 会表现为 WebException；在令牌已取消时统一转换为取消语义。
                    DeletePartialFile();
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"下载资源失败，模块：{_mCurBundleModuleName}，文件：{mHotFileInfo.abName}，" +
                        $"地址：{safeDownloadUrl}，第 {curDownLoadCount} 次，异常类型：{exception.GetType().Name}");
                }

                DeletePartialFile();
                if (curDownLoadCount < MAX_TRY_DOWNLOAD_COUNT)
                    Debug.LogWarning($"准备重试下载，模块：{_mCurBundleModuleName}，文件：{mHotFileInfo.abName}，下一次：{curDownLoadCount + 1}");
            }

            OnDownLoadFailed?.Invoke(this, mHotFileInfo);
        }

        /// <summary>
        /// 失败或取消后移除不完整文件，避免后续完整快照校验误读半文件。
        /// </summary>
        private void DeletePartialFile()
        {
            try
            {
                if (File.Exists(mFileSavePath))
                    File.Delete(mFileSavePath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"清理未完成下载文件失败，路径：{mFileSavePath}，异常：{exception}");
            }
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
            string safeDownloadUrl = AssetLogUtility.SanitizeUrl(mDownLoadUrl);
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
                        // 手动轮询代替单次 await：在下载期间按 50ms 间隔汇报实时进度；
                        // 进度变化不足 1% 时不重复回调，避免大文件下载刷爆业务进度条。
                        UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                        float lastReportedProgress = -1f;
                        while (!operation.isDone)
                        {
                            float progress = webRequest.downloadProgress;
                            if (progress - lastReportedProgress >= 0.01f)
                            {
                                lastReportedProgress = progress;
                                OnDownloadProgress?.Invoke(this, mHotFileInfo, progress);
                            }

                            await UniTask.Delay(50);
                        }

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
                        // MD5 十六进制大小写不影响摘要值，服务端使用大写时也应通过同一完整性校验。
                        fileIsComplete = string.Equals(
                            MD5.GetMd5FromFile(mFileSavePath),
                            mHotFileInfo.md5,
                            StringComparison.OrdinalIgnoreCase);
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
                                  safeDownloadUrl + " FileName:" + Path.GetFileName(mFileSavePath));
                        return true;
                    }

                    Debug.LogError("FixDownLoad File DownLoad exception plase check file fileName:" +
                                   mHotFileInfo.abName + " fileUrl:" + safeDownloadUrl);
                }
                catch (Exception e)
                {
                    Debug.LogError(
                        "FixDownLoad DownLoad AssetBundle Error Url:" + safeDownloadUrl +
                        " ExceptionType:" + e.GetType().Name);
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
