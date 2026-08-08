using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ZM.ZMAsset
{
    public partial class ZMAsset
    {
        /// <summary>
        /// 远端资源按需下载入口；本地缓存有效时不会重复下载。
        /// </summary>
        public static class Remote
        {
            /// <summary>
            /// 按需下载远端 Prefab 及其依赖，并从框架对象池实例化。
            /// </summary>
            /// <param name="onProgress">可选的下载进度回调（0~1），仅在发生远端下载时触发。</param>
            public static UniTask<AssetsRequest> InstantiateAsync(string path, Transform parent, string moduleName, object parameter1 = null, object parameter2 = null, object parameter3 = null, Action<float> onProgress = null)
            {
                ValidateAssetPath(path);
                ValidateModuleName(moduleName);
                return GetLoader().InstantiateRemoteAsync(path, parent, moduleName, parameter1, parameter2, parameter3, onProgress);
            }

            /// <summary>
            /// 按需下载并加载指定类型的远端资源。
            /// </summary>
            /// <param name="onProgress">可选的下载进度回调（0~1），仅在发生远端下载时触发。</param>
            public static UniTask<T> LoadAsync<T>(string path, string moduleName, Action<float> onProgress = null) where T : UnityEngine.Object
            {
                ValidateAssetPath(path);
                ValidateModuleName(moduleName);
                return GetLoader().LoadRemoteAsync<T>(path, moduleName, onProgress);
            }

            /// <summary>
            /// 闲时预下载整个模块尚未就绪的远端文件，业务随后加载时秒开。
            /// 串行逐个下载：闲时预热不抢占前台加载带宽；与按需加载并发时先到先下、后者复用。
            /// </summary>
            /// <param name="moduleName">资源物理归属模块。</param>
            /// <param name="onProgress">可选的整体进度回调（0~1）=(已完成文件数+当前文件进度)/待下载总数。</param>
            public static UniTask<RemotePreDownloadResult> PreDownloadAsync(string moduleName, Action<float> onProgress = null)
            {
                ValidateModuleName(moduleName);
                return GetLoader().PreDownloadModuleAsync(moduleName, onProgress);
            }

            /// <summary>
            /// 闲时预下载指定资源的主 Bundle 及其同模块依赖。
            /// </summary>
            /// <param name="path">项目相对资源路径。</param>
            /// <param name="moduleName">资源物理归属模块。</param>
            /// <param name="onProgress">可选的整体进度回调（0~1）。</param>
            public static UniTask<RemotePreDownloadResult> PreDownloadAsync(string path, string moduleName, Action<float> onProgress = null)
            {
                ValidateAssetPath(path);
                ValidateModuleName(moduleName);
                return GetLoader().PreDownloadAssetAsync(path, moduleName, onProgress);
            }

            /// <summary>
            /// 获取框架初始化时注入的 RemoteAsset 加载器，并提供明确的初始化失败信息。
            /// </summary>
            private static IRemoteAssetLoader GetLoader()
            {
                IRemoteAssetLoader loader = InitializedInstance.mRemoteAssetLoader;
                if (loader == null)
                    throw new System.InvalidOperationException("ZMAsset 尚未完成初始化，无法加载远端资源。");
                return loader;
            }
        }
    }
}
