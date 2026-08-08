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
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
namespace ZM.ZMAsset
{
    public interface IHotAssets
    {
        /// <summary>
        /// 开始热更
        /// </summary>
        /// <param name="bundleModule">热更模块</param>
        /// <param name="startHotCallBack">开始热更回调</param>
        /// <param name="hotFinish">热更完成回调</param>
        /// <param name="waiteDownLoad">等待下载的回调</param>
        /// <param name="isCheckAssetsVersion">是否需要检测资源版本</param>
        /// <param name="hotFailed">热更失败回调，包含失败模块和首个失败文件</param>
        void HotAssets(
            string bundleModule,
            Action<string> startHotCallBack,
            Action<string> hotFinish,
            Action<string> waiteDownLoad,
            bool isCheckAssetsVersion = true,
            Action<string, HotFileInfo> hotFailed = null);
        /// <summary>
        /// 按调用方给定顺序执行显式多模块热更新事务。
        /// </summary>
        UniTask<HotUpdateTransactionResult> HotAssetsTransactionAsync(HotUpdateTransactionRequest request);
        /// <summary>
        /// 检测资源版本，获取需要热更资源的大小；网络或清单失败时返回 UnableToConfirm，不得按无更新处理。
        /// </summary>
        /// <param name="bundleModule">热更模块类型</param>
        UniTask<HotUpdateVersionCheckResult> CheckAssetsVersionAsync(string bundleModule);
 
        /// <summary>
        /// 获取热更模块
        /// </summary>
        /// <param name="bundleModule">热更模块类型</param>
        /// <returns></returns>
        HotAssetsModule GetHotAssetsModule(string bundleModule);
        /// <summary>
        /// 获取指定模块的只读热更新状态。
        /// </summary>
        HotAssetsModuleState GetHotAssetsModuleState(string bundleModule);
        /// <summary>
        /// 主线程更新
        /// </summary>
        void OnMainThreadUpdate();
        
    }
}
