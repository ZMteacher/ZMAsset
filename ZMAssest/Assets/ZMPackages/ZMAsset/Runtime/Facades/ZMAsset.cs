/*---------------------------------------------------------------------------------------------------------------------------------------------
*
* Title: ZMAsset
*
* Description: 可视化多模块打包器、多模块热更、多线程下载、多版本热更、多版本回退、加密、解密、内嵌、解压、内存引用计数、大型对象池、AssetBundle加载、Editor加载
*
* Author: 铸梦xy
*
* Date: 2023.4.13
*
* Modify:
------------------------------------------------------------------------------------------------------------------------------------------------*/

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ZM.Asset
{
    public partial class ZMAsset : MonoSingleton<ZMAsset>
    {
        public static Transform RecyclObjPool { get; private set; }

        private IHotAssets mHotAssets = null;

        private IResourceInterface mResource = null;

        /// <summary>
        /// 远端资源加载边界由 ResourceManager 实现，但不向业务层暴露可替换的全局字段。
        /// </summary>
        private IRemoteAssetLoader mRemoteAssetLoader = null;

        /// <summary>
        /// 框架初始化状态；保证自动创建、场景挂载和显式初始化不会重复创建管理器。
        /// </summary>
        private bool mIsInitialized;

        /// <summary>
        /// 获取已经完成初始化的框架实例。
        /// 所有公开门面统一经过该入口，使开发者无需依赖 MonoBehaviour Awake 的执行顺序。
        /// </summary>
        private static ZMAsset InitializedInstance
        {
            get
            {
                ZMAsset instance = Instance;
                instance.Initialize();
                return instance;
            }
        }

        /// <summary>
        /// 初始化框架
        /// </summary>
        private void Initialize()
        {
            if (mIsInitialized)
                return;

            mIsInitialized = true;
            try
            {
                //创建对象池回收节点
                GameObject recyclObjectRoot = new GameObject("RecyclObjPool");
                RecyclObjPool = recyclObjectRoot.transform;
                recyclObjectRoot.SetActive(false);
                DontDestroyOnLoad(recyclObjectRoot);

                //热更资源管理器
                mHotAssets = new HotAssetsManager();

                //资源加载管理器
                var resource = new ResourceManager();
                mResource = resource;
                mRemoteAssetLoader = resource;
                //初始化资源管理器
                mResource.Initlizate();
                Debug.Log("ZMAsset Initialize Success!");
                LogRuntimeModes();
            }
            catch
            {
                // 初始化中途失败时允许下一次调用重试，同时不保留半初始化状态。
                mIsInitialized = false;
                throw;
            }
        }

        /// <summary>
        /// 在框架初始化完成后输出本次运行采用的资源策略，便于快速识别 Editor/AssetBundle 与热更配置。
        /// </summary>
        private static void LogRuntimeModes()
        {
            BundleSettings settings = BundleSettings.Instance;
            string loadMode = settings == null ? "<color=#FF5252><b>Missing</b></color>" :
                $"<color=#FFD740><b>{settings.loadAssetType}</b></color>";
            string hotUpdateMode = settings == null ? "<color=#FF5252><b>Missing</b></color>" :
                $"<color=#FFD740><b>{settings.bundleHotType}</b></color>";

            Debug.Log(
                "<color=#00E5FF><b>━━━━━━━━━━ ZMAsset Ready ━━━━━━━━━━</b></color>\n" +

                $"<color=#F5F5F5><b>资源加载模式：</b></color>{loadMode}    " +
                $"<color=#F5F5F5><b>热更模式：</b></color>{hotUpdateMode}    ");

        }
 

        public void Update()
        {
            mHotAssets?.OnMainThreadUpdate();
        }

        private void OnApplicationQuit()
        {
            mResource?.ClearResourcesAssets(true);
        }
    }
}
