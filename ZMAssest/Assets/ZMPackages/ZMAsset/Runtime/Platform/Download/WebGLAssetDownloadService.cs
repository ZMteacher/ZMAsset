using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ZM.ZMAsset
{
    /// <summary>
    /// WebGL RemoteAsset 与事务热更新下载服务。Bundle 直接进入 Unity/浏览器缓存，不复制到托管字节数组或桌面文件目录。
    /// </summary>
    internal sealed class WebGLAssetDownloadService : IAssetDownloadService
    {
        private const int MaximumAttempts = 3;
        private static readonly WebGLAsyncGate sConcurrencyGate = new WebGLAsyncGate(3);

        public IAssetDownloadBatch CreateBatch(AssetDownloadBatchRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            return new WebGLAssetDownloadBatch(this, request);
        }

        public async UniTask<bool> DownloadFileAsync(AssetDownloadFileRequest request)
        {
            Validate(request);
            await sConcurrencyGate.WaitAsync(request.CancellationToken);
            try
            {
                string url = CombineUrl(request.DownloadUrl, request.FileInfo.abName);
                Exception lastException = null;
                for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        Hash128 hash = Hash128.Parse(request.FileInfo.bundleHash);
                        using (UnityWebRequest webRequest = UnityWebRequestAssetBundle.GetAssetBundle(url, hash, request.FileInfo.crc))
                        {
                            webRequest.timeout = 30;
                            UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
                            while (!operation.isDone)
                            {
                                if (request.CancellationToken.IsCancellationRequested)
                                {
                                    webRequest.Abort();
                                    request.CancellationToken.ThrowIfCancellationRequested();
                                }
                                request.ProgressChanged?.Invoke(webRequest.downloadProgress);
                                await UniTask.Yield();
                            }
                            if (webRequest.result != UnityWebRequest.Result.Success)
                                throw new InvalidOperationException(
                                    $"WebGL RemoteAsset 请求失败，模块：{request.ModuleName}，Bundle：{request.FileInfo.abName}，" +
                                    $"地址：{AssetLogUtility.SanitizeUrl(url)}，结果：{webRequest.result}，错误：{webRequest.error}");

                            AssetBundle bundle = DownloadHandlerAssetBundle.GetContent(webRequest);
                            if (bundle == null)
                                throw new InvalidOperationException($"WebGL RemoteAsset 请求完成但未取得 Bundle：{request.FileInfo.abName}");
                            bundle.Unload(false);
                            request.ProgressChanged?.Invoke(1f);
                            return true;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        lastException = exception;
                        if (attempt < MaximumAttempts)
                            await UniTask.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken: request.CancellationToken);
                    }
                }

                Debug.LogError(
                    $"WebGL RemoteAsset 下载在 {MaximumAttempts} 次尝试后失败。操作：{request.OperationId}，" +
                    $"异常类型：{lastException?.GetType().Name ?? "Unknown"}");
                return false;
            }
            finally
            {
                sConcurrencyGate.Release();
            }
        }

        private static void Validate(AssetDownloadFileRequest request)
        {
            if (request.FileInfo == null) throw new ArgumentException("WebGL 下载文件信息不能为空。", nameof(request));
            if (string.IsNullOrWhiteSpace(request.DownloadUrl)) throw new ArgumentException("WebGL 下载根地址不能为空。", nameof(request));
            if (string.IsNullOrWhiteSpace(request.FileInfo.bundleHash))
                throw new InvalidOperationException($"WebGL RemoteAsset Manifest 缺少 bundleHash：{request.FileInfo.abName}");
            Hash128 hash = Hash128.Parse(request.FileInfo.bundleHash);
            if (!hash.isValid) throw new InvalidOperationException($"WebGL RemoteAsset Hash 无效：{request.FileInfo.abName}");
        }

        internal static string CombineUrl(string root, string fileName) => root.TrimEnd('/') + "/" + fileName;

        private sealed class WebGLAssetDownloadBatch : IAssetDownloadBatch
        {
            private readonly WebGLAssetDownloadService mService;
            private readonly AssetDownloadBatchRequest mRequest;
            private readonly Queue<HotFileInfo> mQueue;
            private readonly CancellationTokenSource mCancellation = new CancellationTokenSource();
            private Task mTask = Task.CompletedTask;
            private bool mStarted;
            private bool mDisposed;
            private HotFileInfo mLastCompleted;

            internal WebGLAssetDownloadBatch(WebGLAssetDownloadService service, AssetDownloadBatchRequest request)
            {
                mService = service;
                mRequest = request;
                mQueue = request.DownloadQueue == null
                    ? new Queue<HotFileInfo>()
                    : new Queue<HotFileInfo>(request.DownloadQueue);
                MaximumConcurrency = 3;
            }

            public int MaximumConcurrency { get; set; }

            public void Start()
            {
                if (mDisposed)
                    throw new ObjectDisposedException(nameof(WebGLAssetDownloadBatch));
                if (mStarted)
                    throw new InvalidOperationException("WebGL 热更新下载批次不能重复启动。");
                mStarted = true;
                mTask = RunAsync().AsTask();
            }

            public void UpdateOnMainThread()
            {
                // WebGL workers and callbacks already run on Unity's browser main thread.
            }

            public async Task CancelAndWaitAsync()
            {
                if (!mCancellation.IsCancellationRequested)
                    mCancellation.Cancel();
                try
                {
                    await mTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            public void Dispose()
            {
                if (mDisposed)
                    return;
                mDisposed = true;
                mCancellation.Dispose();
            }

            private async UniTask RunAsync()
            {
                try
                {
                    int workerCount = Math.Max(1, Math.Min(MaximumConcurrency, mQueue.Count));
                    List<UniTask> workers = new List<UniTask>(workerCount);
                    for (int index = 0; index < workerCount; index++)
                        workers.Add(RunWorkerAsync());
                    await UniTask.WhenAll(workers);
                    if (!mCancellation.IsCancellationRequested)
                        mRequest.BatchFinished?.Invoke(mLastCompleted);
                }
                catch (OperationCanceledException)
                {
                    if (!mDisposed)
                        mRequest.DownloadFailed?.Invoke(mLastCompleted);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"WebGL 热更新下载批次失败，操作：{mRequest.OperationId}，异常：{exception}");
                    if (!mCancellation.IsCancellationRequested)
                        mCancellation.Cancel();
                    mRequest.DownloadFailed?.Invoke(mLastCompleted);
                }
            }

            private async UniTask RunWorkerAsync()
            {
                while (!mCancellation.IsCancellationRequested && mQueue.Count > 0)
                {
                    HotFileInfo file = mQueue.Dequeue();
                    bool succeeded = await mService.DownloadFileAsync(new AssetDownloadFileRequest(
                        mRequest.OperationId,
                        mRequest.ModuleName,
                        file,
                        mRequest.DownloadUrl,
                        null,
                        null,
                        mCancellation.Token));
                    if (!succeeded)
                    {
                        mLastCompleted = file;
                        mCancellation.Cancel();
                        throw new InvalidOperationException($"WebGL 热更新 Bundle 下载失败：{file.abName}");
                    }

                    mLastCompleted = file;
                    mRequest.BytesDownloaded?.Invoke((int)Math.Min(int.MaxValue, Math.Max(0f, file.size * 1024f)));
                    mRequest.DownloadSucceeded?.Invoke(file);
                }
                mCancellation.Token.ThrowIfCancellationRequested();
            }
        }
    }
}
