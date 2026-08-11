using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace ZM.Asset
{
    /// <summary>
    /// 描述一个热更新下载批次。队列顺序由上层确定，下载服务只负责并发、重试、取消和回调派发。
    /// </summary>
    internal sealed class AssetDownloadBatchRequest
    {
        internal string OperationId { get; set; }

        internal string ModuleName { get; set; }

        internal Queue<HotFileInfo> DownloadQueue { get; set; }

        internal string DownloadUrl { get; set; }

        internal string SavePath { get; set; }

        internal Action<int> BytesDownloaded { get; set; }

        internal DownLoadEvent DownloadSucceeded { get; set; }

        internal DownLoadEvent DownloadFailed { get; set; }

        internal DownLoadEvent BatchFinished { get; set; }
    }

    /// <summary>
    /// 描述一个按需下载文件。文件内容仍由下载实现按 Manifest 中的摘要完成校验。
    /// </summary>
    internal readonly struct AssetDownloadFileRequest
    {
        internal AssetDownloadFileRequest(
            string operationId,
            string moduleName,
            HotFileInfo fileInfo,
            string downloadUrl,
            string savePath,
            Action<float> progressChanged,
            CancellationToken cancellationToken = default)
        {
            OperationId = operationId;
            ModuleName = moduleName;
            FileInfo = fileInfo;
            DownloadUrl = downloadUrl;
            SavePath = savePath;
            ProgressChanged = progressChanged;
            CancellationToken = cancellationToken;
        }

        internal string OperationId { get; }

        internal string ModuleName { get; }

        internal HotFileInfo FileInfo { get; }

        internal string DownloadUrl { get; }

        internal string SavePath { get; }

        internal Action<float> ProgressChanged { get; }

        internal CancellationToken CancellationToken { get; }
    }

    /// <summary>
    /// 表示一个仍受上层热更新生命周期控制的下载批次。
    /// 取消完成前不能释放实例，否则后台任务可能继续持有网络流或文件句柄。
    /// </summary>
    internal interface IAssetDownloadBatch : IDisposable
    {
        int MaximumConcurrency { get; set; }

        void Start();

        void UpdateOnMainThread();

        Task CancelAndWaitAsync();
    }

    /// <summary>
    /// 平台下载服务，统一批量热更新与单文件 RemoteAsset 下载入口。
    /// </summary>
    internal interface IAssetDownloadService
    {
        IAssetDownloadBatch CreateBatch(AssetDownloadBatchRequest request);

        UniTask<bool> DownloadFileAsync(AssetDownloadFileRequest request);
    }
}
