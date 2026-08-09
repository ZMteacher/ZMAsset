using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using System.Threading;

namespace ZM.ZMAsset
{
    /// <summary>
    /// RemoteAsset 门面与资源管理器之间的内部加载边界。
    /// </summary>
    internal interface IRemoteAssetLoader
    {
        /// <summary>
        /// 按需下载远端 Prefab 及其依赖，并从对象池实例化。
        /// </summary>
        /// <param name="onProgress">可选的下载进度回调（0~1），仅在发生远端下载时触发。</param>
        UniTask<AssetsRequest> InstantiateRemoteAsync(string path, Transform parent, string moduleName,object parameter1 = null, object parameter2 = null, object parameter3 = null, Action<float> onProgress = null);

        /// <summary>
        /// 按需下载并加载远端资源；本地文件有效时直接复用。
        /// </summary>
        /// <param name="onProgress">可选的下载进度回调（0~1），仅在发生远端下载时触发。</param>
        UniTask<T> LoadRemoteAsync<T>(string path, string moduleName, Action<float> onProgress = null) where T : UnityEngine.Object;

        /// <summary>
        /// 闲时预下载整个模块尚未就绪的远端文件；串行下载，不抢占前台加载带宽。
        /// </summary>
        UniTask<RemotePreDownloadResult> PreDownloadModuleAsync(string moduleName, Action<float> onProgress = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// 闲时预下载指定资源的主 Bundle 及其同模块依赖。
        /// </summary>
        UniTask<RemotePreDownloadResult> PreDownloadAssetAsync(string path, string moduleName, Action<float> onProgress = null, CancellationToken cancellationToken = default);
    }
}
