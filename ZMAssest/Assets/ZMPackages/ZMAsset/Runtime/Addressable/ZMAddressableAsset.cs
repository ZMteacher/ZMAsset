using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZM.ZMAsset 
{ 
    public  class ZMAddressableAsset 
    {

        public static IAddressableAssetInterface Interface;
        /// <summary>
        /// 异步实例化可寻址资源系统对象
        /// </summary>
        /// <param name="path">加载路径</param>
        /// <param name="bundleModule">资源模块</param>
        /// <param name="param1">透传参数1 (回调触发时返回)</param>
        /// <param name="param2">透传参数2 (回调触发时返回)</param>
        /// <param name="param3">透传参数3 (回调触发时返回)</param>
        /// <returns></returns>
        public static async UniTask<AssetsRequest> InstantiateAsyncFormPool(string path,Transform parent, string bundleModule, object param1 = null, object param2 = null, object param3 = null)
        {
             return await Interface.InstantiateAsyncFormPoolAas(path,parent, bundleModule, param1, param2, param3);
        }
        
        /// <summary>
        /// 异步加载可寻址资源(无论资源在本地还是在网络上，该接口都能准确无误加载到)
        /// </summary>
        /// <typeparam name="T">资源类型</typeparam>
        /// <param name="path">资源路径</param>
        /// <param name="moduleName">可寻址资源模块</param>
        /// <returns></returns>
        public static async UniTask<T> LoadResourceAsync<T>(string fullPath, string moduleName) where T : UnityEngine.Object
        {
            return await Interface.LoadResourceAsyncAas<T>(fullPath, moduleName);
        }
        #region 资源加载 API
        
        /// <summary>
        /// 预加载一些仅仅不需要实例化的资源
        /// </summary>
        /// <param name="fullPath"></param>
        /// <param name="moduleName"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public static async UniTask<T> PreLoadResourceAsync<T>(string fullPath,string moduleName) where T : UnityEngine.Object
        {
          return await Interface.LoadResourceAsyncAas<T>(fullPath,moduleName);
        }
        /// <summary>
        /// 释放对象占用内存
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="destroy"></param>
        public static void Release(GameObject obj, bool destroy = false)
        {
            ZMAsset.Release(obj,destroy);
        }
        /// <summary>
        /// 释放图片所占用的内存(存在危险性，确定该图片资源不用时进行调用)
        /// </summary>
        /// <param name="texture"></param>
        public static void Release(Texture texture)
        {
            ZMAsset.Release(texture);
        }

        /// <summary>
        /// 加载图片资源
        /// </summary>
        /// <param name="path">基于Assets开始的路径</param>
        /// <param name="moduleName">资源模块</param>
        /// <returns></returns>
        public static UniTask<Sprite> LoadSpriteAsync(string path,string moduleName)
        {
            return Interface.LoadResourceAsyncAas<Sprite>(path,moduleName);
        }

        /// <summary>
        /// 加载Texture图片
        /// </summary>
        /// <param name="path">基于Assets开始的路径</param>
        /// <param name="moduleName">资源模块</param>
        /// <returns></returns>
        public static UniTask<Texture> LoadTextureAsync(string path,string moduleName)
        {
            return Interface.LoadResourceAsyncAas<Texture>(path,moduleName);
        }

        /// <summary>
        /// 加载音频文件
        /// </summary>
        /// <param name="path">基于Assets开始的路径</param>
        /// <param name="moduleName">资源模块</param>
        /// <returns></returns>
        public static UniTask<AudioClip> LoadAudioAsync(string path,string moduleName)
        {
            return Interface.LoadResourceAsyncAas<AudioClip>(path,moduleName);
        }

        /// <summary>
        /// 加载Text资源
        /// </summary>
        /// <param name="path">绝对路径</param>
        /// <param name="moduleName"></param>
        /// <returns></returns>
        public static UniTask<TextAsset> LoadTextAssetAsync(string path,string moduleName)
        {
            return Interface.LoadResourceAsyncAas<TextAsset>(path,moduleName);
        }
        
        /// <summary>
        /// 加载可编写脚本对象
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="path">绝对路径</param>
        /// <param name="moduleName"></param>
        /// <returns></returns>
        public static UniTask<T> LoadScriptableObject<T>(string path,string moduleName) where T : UnityEngine.Object
        {
            return Interface.LoadResourceAsyncAas<T>(path,moduleName);
        }
        #endregion
      
    }
}
