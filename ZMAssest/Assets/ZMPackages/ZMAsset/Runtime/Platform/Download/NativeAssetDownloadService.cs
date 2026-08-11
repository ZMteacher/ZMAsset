using System;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace ZM.Asset
{
    /// <summary>
    /// Native 下载服务。批量下载继续委托 AssetsDownLoader，单文件下载继续委托 DownLoadThread。
    /// 现有并发上限、三次重试、MD5 校验和取消等待语义均保持不变。
    /// </summary>
    internal sealed class NativeAssetDownloadService : IAssetDownloadService
    {
        public IAssetDownloadBatch CreateBatch(AssetDownloadBatchRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            AssetsDownLoader downloader = new AssetsDownLoader(
                request.ModuleName,
                request.DownloadQueue,
                request.DownloadUrl,
                request.SavePath,
                request.BytesDownloaded,
                request.DownloadSucceeded,
                request.DownloadFailed,
                request.BatchFinished);
            return new NativeAssetDownloadBatch(downloader);
        }

        public async UniTask<bool> DownloadFileAsync(AssetDownloadFileRequest request)
        {
            DownLoadThread download = new DownLoadThread(
                request.ModuleName,
                request.FileInfo,
                request.DownloadUrl,
                request.SavePath);
            if (request.ProgressChanged != null)
                download.OnDownloadProgress = (_, _, value) => request.ProgressChanged(value);

            return await download.StartDownLoadAsync();
        }

        private sealed class NativeAssetDownloadBatch : IAssetDownloadBatch
        {
            private readonly AssetsDownLoader mDownloader;

            internal NativeAssetDownloadBatch(AssetsDownLoader downloader)
            {
                mDownloader = downloader;
            }

            public int MaximumConcurrency
            {
                get => mDownloader.MAX_THREAD_COUNT;
                set => mDownloader.MAX_THREAD_COUNT = value;
            }

            public void Start()
            {
                mDownloader.StartThreadDownLoadQueue();
            }

            public void UpdateOnMainThread()
            {
                mDownloader.OnMainThreadUpdate();
            }

            public Task CancelAndWaitAsync()
            {
                return mDownloader.CancelAndWaitAsync();
            }

            public void Dispose()
            {
                mDownloader.Dispose();
            }
        }
    }
}
