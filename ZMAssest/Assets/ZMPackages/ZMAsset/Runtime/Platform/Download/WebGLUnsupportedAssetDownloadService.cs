using System;
using Cysharp.Threading.Tasks;

namespace ZM.Asset
{
    /// <summary>
    /// 第 2 期 WebGL 下载边界。
    /// 内嵌加载已经可用，但 RemoteAsset 与热更新下载必须等第 3 期浏览器实现，禁止回退 Native 线程下载器。
    /// </summary>
    internal sealed class WebGLUnsupportedAssetDownloadService : IAssetDownloadService
    {
        public IAssetDownloadBatch CreateBatch(AssetDownloadBatchRequest request)
        {
            throw CreateException("批量热更新下载");
        }

        public UniTask<bool> DownloadFileAsync(AssetDownloadFileRequest request)
        {
            throw CreateException("RemoteAsset 单文件下载");
        }

        private static PlatformNotSupportedException CreateException(string operation)
        {
            return new PlatformNotSupportedException(
                $"WebGL 第 2 期尚未启用{operation}，已阻止回退到 Native 下载实现。" +
                "当前只能使用内嵌模块和 ZMAsset.Resources 的异步加载接口。");
        }
    }
}
