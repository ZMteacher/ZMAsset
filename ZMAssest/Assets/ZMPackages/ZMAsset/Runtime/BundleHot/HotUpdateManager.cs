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
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
namespace ZM.ZMAsset
{
    public class HotUpdateManager : ZMAssetMonoSingleton<HotUpdateManager>
    {
        private System.Action OnHotFinishCallBackAction;
        /// <summary>
        /// 热更并且解压热更模块
        /// </summary>
        /// <param name="bundleModule"></param>
        public async void HotAndUnPackAssets(string bundleModule,System.Action hotFinishCallBack)
        { 
            this.OnHotFinishCallBackAction = hotFinishCallBack; 
            //开始解压游戏内嵌资源
            //网络正常
            if (BundleSettings.Instance.bundleHotType == BundleHotEnum.Hot && bundleModule == BundleModuleName.AdressAsset)
            {
                //检测资源版本
                CheckAssetsVersion(bundleModule);
            }
            else
            {
                //初始化资源模块
                await ZMAsset.InitAssetsModule(bundleModule);
                //如果不需要热更，说明用户已经热更过了，资源是最新的，直接进入游戏 
                OnHotFinishCallBack(bundleModule);
            }
        }

        public void NotNetButtonClick(string bundleModule)
        {
            //如果么有网络，弹出弹窗提示，提示用户没有网络请重试
            if (Application.internetReachability!= NetworkReachability.NotReachable)
            {
                CheckAssetsVersion(bundleModule);
            }
        }
        public void CheckAssetsVersion(string bundleModule)
        {
            ZMAsset.CheckAssetsVersion(bundleModule,(isHot,sizem)=> {
                if (isHot)
                {
                    //当用户使用是流量的时候呢，需要询问用户是否需要更新资源
                    if (Application.internetReachability== NetworkReachability.ReachableViaCarrierDataNetwork||Application.platform == RuntimePlatform.WindowsEditor||Application.platform==RuntimePlatform.OSXEditor)
                    {
                        //弹出选择弹窗，让用户决定是否更新
                        InstantiateResourcesObj<UpdateTipsWindow>("UpdateTipsWindow").
                        InitView("当前有"+sizem.ToString("F2")+"m,资源需要更新，是否更新",()=> {
                            //确认更新回调
                            StartHotAssets(bundleModule);
                        },
                        ()=> {
                            //退出游戏回调
                            Application.Quit();
                        });
                    }
                    else
                    {
                        //开始热更资源
                        StartHotAssets(bundleModule);
                    }
                }
                else
                {
                    //如果不需要热更，说明用户已经热更过了，资源是最新的，直接进入游戏 TODO
                    OnHotFinishCallBack(bundleModule);
                }
            });
        }
        /// <summary>
        /// 开始热更资源
        /// </summary>
        /// <param name="bundleModule"></param>
        public void StartHotAssets(string bundleModule)
        {
            ZMAsset.HotAssets(bundleModule, OnStartHotAssetsCallBack, OnHotFinishCallBack,null,false);
        }
        /// <summary>
        /// 热更完成回调
        /// </summary>
        public void OnHotFinishCallBack(string bundleModule)
        {
            Debug.Log("OnHotFinishCallBack.....");
            OnHotFinishCallBackAction?.Invoke();
        }

        public void OnStartHotAssetsCallBack(string bundleModule)
        {

        }
        
        public void LoadGameConfig()
        {

        }
        public T InstantiateResourcesObj<T>(string prefabName)
        {
           return  GameObject.Instantiate<GameObject>(Resources.Load<GameObject>(prefabName)).GetComponent<T>();
        }
    }
}