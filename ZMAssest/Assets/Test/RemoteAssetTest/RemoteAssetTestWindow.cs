using Cysharp.Threading.Tasks;
using UnityEngine;
using ZM.UI;
using ZM.ZMAsset;

/// <summary>
/// RemoteAsset 测试窗口：预下载整模块 + 逐项加载验证，展示整体进度条。
/// </summary>
public class RemoteAssetTestWindow : WindowBase
{
    public RemoteAssetTestWindowDataComponent dataCompt;

    private const string ModuleName = BundleModuleName.RemoteAsset;
    private bool mIsBusy;

    public override void OnAwake()
    {
        dataCompt = gameObject.GetComponent<RemoteAssetTestWindowDataComponent>();
        dataCompt.InitComponent(this);
        FullScreenWindow = true;
        mDisableAnim = true;
        base.OnAwake();
        dataCompt.serverText.text = "服务器：" + BundleSettings.Instance.AssetBundleDownLoadUrl;
        RefreshViewList();
    }

    public override void OnShow()
    {
        base.OnShow();
        RefreshViewList();
    }

    public override void OnHide()
    {
        base.OnHide();
        dataCompt.itemListView.OnRelease();
    }

    private void RefreshViewList()
    {
        dataCompt.itemListView.RefreshListView(true, RemoteAssetTestItemData.sItems.Length, OnGetItemDataCallBack);
    }

    private object OnGetItemDataCallBack(int index)
    {
        return RemoteAssetTestItemData.sItems[index];
    }

    /// <summary>
    /// 预下载整个模块：串行下载缺失文件并汇报整体进度。
    /// </summary>
    public async void OnPreDownloadButtonClick()
    {
        if (mIsBusy) return;
        mIsBusy = true;
        try
        {
            RemotePreDownloadResult result = await ZMAsset.Remote.PreDownloadAsync(ModuleName, OnProgressChanged);
            Debug.Log($"[RemoteAssetTest] 预下载完成：总数 {result.TotalCount}，成功 {result.SuccessCount}，失败 {result.FailedFileNames.Count}");
            RefreshViewList();
        }
        finally
        {
            mIsBusy = false;
        }
    }

    /// <summary>
    /// 逐项加载 9 个 icon 并显示到列表；整体进度 = (已完成 + 当前文件进度) / 总数。
    /// </summary>
    public async void OnLoadAllButtonClick()
    {
        if (mIsBusy) return;
        mIsBusy = true;
        try
        {
            int total = RemoteAssetTestItemData.sItems.Length;
            for (int i = 0; i < total; i++)
            {
                RemoteAssetTestItemData itemData = RemoteAssetTestItemData.sItems[i];
                int completed = i;
                Texture texture = await ZMAsset.Remote.LoadAsync<Texture>(
                    itemData.Path,
                    ModuleName,
                    value => OnProgressChanged((completed + value) / total));

                itemData.Texture = texture;
                itemData.IsReady = texture != null;
                OnProgressChanged((i + 1f) / total);
                RefreshViewList();
            }
            Debug.Log("[RemoteAssetTest] 逐项加载完成");
        }
        finally
        {
            mIsBusy = false;
        }
    }

    private void OnProgressChanged(float progress)
    {
        dataCompt.progressSlider.value = Mathf.Clamp01(progress);
        dataCompt.progressText.text = $"{Mathf.FloorToInt(progress * 100)}%";
    }

    public void OnCloseButtonClick()
    {
        HideWindow();
    }
}