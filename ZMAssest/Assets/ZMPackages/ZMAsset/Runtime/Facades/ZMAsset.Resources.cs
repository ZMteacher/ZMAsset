using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZM.ZMAsset
{
    public partial class ZMAsset
    {
        /// <summary>
        /// 资源加载、实例化、预加载和释放入口。
        /// </summary>
        /// <remarks />
        public static class Resources
        {
            /// <summary>
            /// 同步预创建指定数量的池对象；可能产生主线程开销，常规业务优先使用 PreloadInstancesAsync。
            /// </summary>
            public static void PreloadInstances(string path, int count = 1)
            {
                ValidateAssetPath(path);
                ValidatePreloadCount(count);
                InitializedInstance.mResource.PreLoadObj(path, count);
            }

            /// <summary>
            /// 预先异步创建指定数量的池对象，降低首次显示时的实例化开销。
            /// </summary>
            public static UniTask PreloadInstancesAsync(string path, int count = 1)
            {
                ValidateAssetPath(path);
                ValidatePreloadCount(count);
                return InitializedInstance.mResource.PreLoadObjAsync(path, count);
            }

            /// <summary>
            /// 同步实例化一个 GameObject，并使用默认局部坐标、缩放和旋转。
            /// </summary>
            public static GameObject Instantiate(string path, Transform parent)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.Instantiate(path, parent, Vector3.zero, Vector3.one, Quaternion.identity);
            }

            /// <summary>
            /// 同步实例化一个 GameObject，并应用指定的局部变换。
            /// </summary>
            public static GameObject Instantiate(string path, Transform parent, Vector3 localPosition, Vector3 localScale, Quaternion localRotation)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.Instantiate(path, parent, localPosition, localScale, localRotation);
            }

            /// <summary>
            /// 异步实例化一个 GameObject；返回值携带实例和框架跟踪信息，释放时应传回 Release。
            /// </summary>
            public static UniTask<AssetsRequest> InstantiateAsync(string path, Transform parent, object parameter1 = null, object parameter2 = null, object parameter3 = null)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.InstantiateAsync(path, parent, parameter1, parameter2, parameter3);
            }

            /// <summary>
            /// 异步加载指定类型的资源；路径可以包含扩展名，也可以使用框架配置的默认扩展名。
            /// </summary>
            public static UniTask<T> LoadAsync<T>(string path) where T : UnityEngine.Object
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.LoadResourceAsync<T>(path);
            }

            /// <summary>
            /// 异步准备场景所属 Bundle；WebGL 首次加载场景前必须等待该方法成功。
            /// 准备完成后继续调用现有 LoadSceneAsync，即可立即取得 Unity AsyncOperation。
            /// </summary>
            public static UniTask<bool> PrepareSceneAsync(string path)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.PrepareSceneAsync(path);
            }

            /// <summary>
            /// 同步加载 TextAsset。
            /// </summary>
            public static TextAsset LoadTextAsset(string path)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.LoadTextAsset(path);
            }

            /// <summary>
            /// 同步加载 ScriptableObject 或其他 UnityEngine.Object 派生配置对象。
            /// </summary>
            public static T LoadScriptableObject<T>(string path) where T : UnityEngine.Object
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.LoadScriptableObject<T>(path);
            }

            /// <summary>
            /// 异步加载场景，并返回 Unity 原生 AsyncOperation 供调用方观察进度。
            /// WebGL 首次加载该场景前需要先等待 PrepareSceneAsync。
            /// </summary>
            public static AsyncOperation LoadSceneAsync(string path, LoadSceneMode mode = LoadSceneMode.Additive)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.LoadSceceAsync(path, mode);
            }

            /// <summary>
            /// 从 Unity SpriteAtlas 中同步读取指定 Sprite。
            /// </summary>
            public static Sprite LoadAtlasSprite(string atlasPath, string spriteName)
            {
                ValidateAtlasParameters(atlasPath, spriteName);
                return InitializedInstance.mResource.LoadAtlasSprite(atlasPath, spriteName);
            }

            /// <summary>
            /// 从 TexturePacker 图集中同步读取指定 Sprite。
            /// </summary>
            public static Sprite LoadTexturePackerSprite(string atlasPath, string spriteName)
            {
                ValidateAtlasParameters(atlasPath, spriteName);
                return InitializedInstance.mResource.LoadPNGAtlasSprite(atlasPath, spriteName);
            }

            /// <summary>
            /// 同步加载 Sprite。
            /// </summary>
            public static Sprite LoadSprite(string path)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.LoadSprite(path);
            }

            /// <summary>
            /// 同步加载 Texture。
            /// </summary>
            public static Texture LoadTexture(string path)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.LoadTexture(path);
            }

            /// <summary>
            /// 同步加载 AudioClip。
            /// </summary>
            public static AudioClip LoadAudio(string path)
            {
                ValidateAssetPath(path);
                return InitializedInstance.mResource.LoadAudio(path);
            }

            /// <summary>
            /// 归还或销毁由框架实例化的 GameObject；空引用表示无需释放，会被安全忽略。
            /// </summary>
            public static void Release(GameObject instance, bool destroyCache = false)
            {
                if (instance == null)
                    return;

                InitializedInstance.mResource.Release(instance, destroyCache);
            }

            /// <summary>
            /// 释放异步实例化请求跟踪的资源；空请求会被安全忽略。
            /// </summary>
            public static void Release(AssetsRequest request)
            {
                if (request == null)
                    return;

                InitializedInstance.mResource.Release(request);
            }

            /// <summary>
            /// 释放框架缓存中的 Texture；调用前必须保证业务层不再使用该纹理。
            /// </summary>
            public static void Release(Texture texture)
            {
                if (texture == null)
                    return;

                InitializedInstance.mResource.Release(texture);
            }

            /// <summary>
            /// 取消尚未触发的旧回调式对象加载请求。
            /// </summary>
            public static void CancelLegacyLoad(long loadId)
            {
                InitializedInstance.mResource.RemoveObjectLoadCallBack(loadId);
            }

            /// <summary>
            /// 清理全部旧回调式异步加载任务。
            /// </summary>
            public static void CancelAllLegacyLoads()
            {
                InitializedInstance.mResource.ClearAllAsyncLoadTask();
            }

            /// <summary>
            /// 清理所有资源缓存；深度清理会销毁框架跟踪的活动对象，调用前必须停止使用这些对象。
            /// </summary>
            public static void ClearAll(bool forceTrackedObjects)
            {
                InitializedInstance.mResource.ClearResourcesAssets(forceTrackedObjects);
            }

            /// <summary>
            /// 校验对象池预加载数量，防止无效任务进入资源管理器。
            /// </summary>
            private static void ValidatePreloadCount(int count)
            {
                if (count <= 0)
                    throw new ArgumentOutOfRangeException(nameof(count), count, "预加载数量必须大于 0。");
            }

            /// <summary>
            /// 统一校验图集路径和子 Sprite 名称，保证两种图集加载方式具有相同的失败行为。
            /// </summary>
            private static void ValidateAtlasParameters(string atlasPath, string spriteName)
            {
                ValidateAssetPath(atlasPath, nameof(atlasPath));
                if (string.IsNullOrWhiteSpace(spriteName))
                    throw new ArgumentException("图集中的 Sprite 名称不能为空。", nameof(spriteName));
            }
        }
    }
}
