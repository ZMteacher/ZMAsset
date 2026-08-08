using UnityEngine;
using UnityEngine.UI;
using ZM.UI;

/// <summary>
/// RemoteAsset 测试工具：窗口组件绑定（对应 prefab 上的序列化引用）。
/// </summary>
public class RemoteAssetTestWindowDataComponent : MonoBehaviour
{
    public Text titleText;
    public Text serverText;
    public ZMUIListView itemListView;
    public Slider progressSlider;
    public Text progressText;
    public Button preDownloadButton;
    public Button loadAllButton;
    public Button closeButton;

    public void InitComponent(WindowBase target)
    {
        RemoteAssetTestWindow window = (RemoteAssetTestWindow)target;
        target.AddButtonClickListener(preDownloadButton, window.OnPreDownloadButtonClick);
        target.AddButtonClickListener(loadAllButton, window.OnLoadAllButtonClick);
        target.AddButtonClickListener(closeButton, window.OnCloseButtonClick);
    }
}