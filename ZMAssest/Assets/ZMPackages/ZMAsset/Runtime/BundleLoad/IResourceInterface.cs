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
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZM.Asset
{
    public interface IResourceInterface
    {
        void Initlizate();

        UniTask<bool> InitAssetModule(string bundleModule, bool isRemoteAsset = false);

        void PreLoadObj(string path, int count = 1);

        UniTask PreLoadObjAsync(string path, int count = 1);

        void PreLoadResource<T>(string path) where T : UnityEngine.Object;

        GameObject Instantiate(string path, Transform parent, Vector3 localPoition, Vector3 localScale,
            Quaternion quaternion);

        void InstantiateAsync(string path, Transform parent, Action<GameObject, object> loadAsync,
            object param1);

        UniTask<AssetsRequest> InstantiateAsync(string path, Transform parent, object param1, object param21,
            object param2);

        long InstantiateAndLoad(string path, Transform parent, Action<GameObject, object, object> loadAsync,
            Action loading, object param1, object param2);

        UniTask<AssetHandle<T>> LoadResourceAsync<T>(string path, CancellationToken cancellationToken = default)
            where T : UnityEngine.Object;

        UniTask<AssetHandle<T>> LoadResourceAsync<T>(
            string path,
            bool isEncrypt,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object;

        void RemoveObjectLoadCallBack(long loadid);

        void Release(GameObject obj, bool destroyCache = false);

        void Release(AssetsRequest request);

        AssetHandle<Sprite> LoadSprite(string path);

        AssetHandle<Texture> LoadTexture(string path);

        AssetHandle<AudioClip> LoadAudio(string path);

        AssetHandle<TextAsset> LoadTextAsset(string path);
        /// <summary>
        /// 异步准备场景所属 Bundle，使随后保持原签名的 LoadSceceAsync 可以立即返回 Unity AsyncOperation。
        /// WebGL 无法在返回 AsyncOperation 的同步入口中等待网络，因此场景首次加载前必须调用该方法。
        /// </summary>
        UniTask<bool> PrepareSceneAsync(string path);
        AsyncOperation LoadSceceAsync(string path, LoadSceneMode loadSceneMode = LoadSceneMode.Additive);
        AssetHandle<T> LoadScriptableObject<T>(string path) where T : UnityEngine.Object;

        AssetHandle<UnityEngine.Sprite> LoadAtlasSprite(string atlasPath, string spriteName);

        UniTask<AssetHandle<UnityEngine.Sprite>> LoadAtlasSpriteAsync(
            string atlasPath,
            string spriteName,
            CancellationToken cancellationToken = default);

        AssetHandle<UnityEngine.Sprite> LoadPNGAtlasSprite(string atlasPath, string spriteName);

        UniTask<AssetHandle<UnityEngine.Sprite>> LoadPNGAtlasSpriteAsync(
            string atlasPath,
            string spriteName,
            CancellationToken cancellationToken = default);

        void ClearAllAsyncLoadTask();

        void ClearResourcesAssets(bool absoluteCleaning); //是否深度清理

        /// <summary>
        /// 清理指定资源模块中由框架跟踪的缓存和对象。
        /// </summary>
        /// <remarks>
        /// PooledOnly 会拒绝清理仍有活动 AssetHandle 的资源；ForceTrackedObjects 会使这些句柄失效，
        /// 调用方必须保证之后不再访问对应资源对象。
        /// </remarks>
        UniTask<ModuleClearResult> ClearModuleAssetsAsync(string bundleModule, ModuleClearMode mode = ModuleClearMode.PooledOnly);

        /// <summary>
        /// 清理模块资源并显式移除其配置、依赖图和租约；Shared 有依赖模块时失败。
        /// </summary>
        UniTask<ModuleUnloadResult> UnloadModuleAssetsAsync(string bundleModule, ModuleClearMode mode = ModuleClearMode.PooledOnly);
    }
}
