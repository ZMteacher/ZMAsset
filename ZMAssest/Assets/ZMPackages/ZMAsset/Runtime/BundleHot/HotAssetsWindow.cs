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
using UnityEngine;
using UnityEngine.UI;
using ZM.Asset;
public class HotAssetsWindow : MonoBehaviour
{
    public Slider progressSlider;
    public Text progressText;
    // public Text rateText;
 
    private string mBundleModule;
    private string mOperationId;

    private bool LoadGameEnv = false;

    public GameObject updateNoticeObj;//更新公告总结点

    public Text updateNoticeText;//更新公告文本
   
    /// <summary>
    /// 显示热更资源进度
    /// </summary>
    /// <param name="assetsModule"></param>
    public void ShowHotAssetsProgress(HotAssetsModule assetsModule)
    {
        if (assetsModule == null)
            throw new System.ArgumentNullException(nameof(assetsModule));
        progressText.text = "";
        progressSlider.value = 0;
        mBundleModule = assetsModule.CurBundleModuleName;
        mOperationId = null;
        updateNoticeObj.SetActive(true);
        ApplyState(ZMAsset.HotUpdate.GetModuleState(mBundleModule));
    }

    private void OnEnable()
    {
        ZMAsset.HotUpdate.StateChanged += OnHotUpdateStateChanged;
        if (!string.IsNullOrEmpty(mBundleModule))
            ApplyState(ZMAsset.HotUpdate.GetModuleState(mBundleModule));
    }

    private void OnDisable()
    {
        ZMAsset.HotUpdate.StateChanged -= OnHotUpdateStateChanged;
    }
    public void SetLoadGameEvn()
    {
        LoadGameEnv = true;
    }
    private void OnHotUpdateStateChanged(HotAssetsModuleState state)
    {
        if (LoadGameEnv || state == null || state.BundleModule != mBundleModule)
            return;

        ApplyState(state);
    }

    private void ApplyState(HotAssetsModuleState state)
    {
        if (state == null)
            return;

        // 同一窗口绑定当前操作；模块层会屏蔽已经失效的旧异步回调。
        if (string.IsNullOrEmpty(mOperationId) || mOperationId == state.OperationId || state.Stage == HotUpdateStage.CheckingVersion)
            mOperationId = state.OperationId;
        else
            return;

        progressSlider.value = state.DownloadProgress;
        if (updateNoticeText != null)
            updateNoticeText.text = (state.UpdateNoticeContent ?? string.Empty).Replace("\\n", "\n");

        switch (state.Stage)
        {
            case HotUpdateStage.CheckingVersion:
                progressText.text = "正在检查资源版本...";
                break;
            case HotUpdateStage.Downloading:
                progressText.text = $"资源下载中...{ToMb(state.DownloadedBytes):F1}MB/{ToMb(state.TotalBytes):F1}MB";
                break;
            case HotUpdateStage.Verifying:
                progressText.text = "正在校验资源...";
                break;
            case HotUpdateStage.Committing:
                progressText.text = "正在应用更新...";
                break;
            case HotUpdateStage.Initializing:
                progressText.text = "正在初始化资源...";
                break;
            case HotUpdateStage.Succeeded:
                progressSlider.value = 1f;
                progressText.text = "资源更新完成";
                Destroy(gameObject, 0.3f);
                break;
            case HotUpdateStage.Failed:
                progressText.text = $"资源更新失败：{state.ErrorMessage}";
                break;
            case HotUpdateStage.Canceled:
                progressText.text = "资源更新已取消";
                break;
            default:
                progressText.text = string.Empty;
                break;
        }
    }

    private static double ToMb(long bytes) => bytes / 1024d / 1024d;
}
