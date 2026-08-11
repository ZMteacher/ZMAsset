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
namespace ZM.Asset
{
    public class HotUpdateManager :  MonoSingleton<HotUpdateManager>
    {
        private System.Action OnHotFinishCallBackAction;
        /// <summary>
        /// 防止版本检查失败时重复创建多个重试窗口。
        /// </summary>
        private bool mVersionCheckFailureWindowShowing;
        /// <summary>
        /// 热更并且解压热更模块
        /// </summary>
        /// <param name="bundleModule"></param>
        public async void HotAndUnPackAssets(string bundleModule,System.Action hotFinishCallBack)
        { 
            this.OnHotFinishCallBackAction = hotFinishCallBack; 
            //开始解压游戏内嵌资源
            //网络正常
            if (BundleSettings.Instance.bundleHotType == BundleHotEnum.Hot && bundleModule == BundleModuleName.AddressAsset)
            {
                //检测资源版本
                CheckAssetsVersion(bundleModule);
            }
            else
            {
                //初始化资源模块
                bool initialized = await ZMAsset.Modules.InitializeAsync(bundleModule);
                if (!initialized)
                {
                    Debug.LogError($"模块 {bundleModule} 初始化失败，已阻止进入游戏。");
                    return;
                }
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
        public async void CheckAssetsVersion(string bundleModule)
        {
            HotUpdateVersionCheckResult versionResult;
            try
            {
                versionResult = await ZMAsset.HotUpdate.CheckVersionAsync(bundleModule);
            }
            catch (System.Exception exception)
            {
                // 初始化失败或其他内部异常同样必须失败关闭，不能从 async void 逸出后继续进入游戏。
                Debug.LogError($"模块 {bundleModule} 资源版本检查异常，已阻止进入游戏：{exception}");
                return;
            }

            if (versionResult.Status == HotUpdateVersionCheckStatus.UnableToConfirm)
            {
                // 网络、HTTP 或清单格式异常只允许重试或退出，不能把 Unknown 当作无更新。
                Debug.LogError(
                    $"模块 {bundleModule} 无法确认资源版本，请重试：{versionResult.ErrorMessage ?? "未知原因"}");
                ShowVersionCheckFailureWindow(bundleModule);
                return;
            }

            if (versionResult.Status == HotUpdateVersionCheckStatus.UpdateAvailable)
            {
                float sizem = versionResult.DownloadSizeMb;
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
            else if (versionResult.Status == HotUpdateVersionCheckStatus.ConfirmedNoUpdate)
            {
                // 只有 ConfirmedNoUpdate 才允许认为资源已确认是最新，并继续进入游戏。
                OnHotFinishCallBack(bundleModule);
            }
            else
            {
                // 防御未来新增枚举值或非法反序列化结果，未知状态默认阻止继续。
                Debug.LogError($"模块 {bundleModule} 返回了未识别的资源版本状态，已阻止进入游戏。");
            }
        }
        /// <summary>
        /// 开始热更资源
        /// </summary>
        /// <param name="bundleModule"></param>
        public async void StartHotAssets(string bundleModule)
        {
            HotUpdateTransactionResult result = await ZMAsset.HotUpdate.UpdateAsync(new[] { bundleModule }, false);
            if (result.Succeeded)
            {
                OnStartHotAssetsCallBack(bundleModule);
                OnHotFinishCallBack(bundleModule);
            }
            else
            {
                Debug.LogError($"[{bundleModule}] 热更新事务失败：{result.Message}");
            }
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

        /// <summary>
        /// 显示版本检查失败提示，并将重试和退出明确交给用户决定。
        /// </summary>
        private void ShowVersionCheckFailureWindow(string bundleModule)
        {
            if (mVersionCheckFailureWindowShowing)
                return;

            mVersionCheckFailureWindowShowing = true;
            UpdateTipsWindow window;
            try
            {
                window = InstantiateResourcesObj<UpdateTipsWindow>("UpdateTipsWindow");
            }
            catch (System.Exception exception)
            {
                mVersionCheckFailureWindowShowing = false;
                Debug.LogError($"无法创建资源版本检查失败提示窗：{exception}");
                return;
            }

            if (window == null)
            {
                mVersionCheckFailureWindowShowing = false;
                Debug.LogError("资源版本检查失败提示窗实例中缺少 UpdateTipsWindow 组件。");
                return;
            }

            window.InitView(
                $"模块 {bundleModule} 暂时无法检查资源版本，请检查网络后重试。",
                () =>
                {
                    // 回调触发前释放窗口占用标记，允许用户只保留一个新的重试流程。
                    mVersionCheckFailureWindowShowing = false;
                    CheckAssetsVersion(bundleModule);
                },
                () =>
                {
                    mVersionCheckFailureWindowShowing = false;
                    Application.Quit();
                });
        }

        public T InstantiateResourcesObj<T>(string prefabName)
        {
           return  GameObject.Instantiate<GameObject>(Resources.Load<GameObject>(prefabName)).GetComponent<T>();
        }
    }
}
