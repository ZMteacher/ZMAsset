using UnityEngine;
using UnityEngine.UI;
using ZM.UI;

/// <summary>
/// RemoteAsset 测试工具：列表行（图标 + 名称 + 状态）。
/// </summary>
public class RemoteAssetTestItem : MonoBehaviour, IZMUIViewListItem
{
    public RawImage iconRawImage;
    public Text nameText;
    public Text statusText;

    public void InitListItem()
    {
    }

    public void SetListItemShowData(int index, params object[] data)
    {
        RemoteAssetTestItemData itemData = (RemoteAssetTestItemData)data[0];
        nameText.text = itemData.Name;
        iconRawImage.texture = itemData.Texture;
        statusText.text = itemData.IsReady ? "就绪" : "未下载";
        statusText.color = itemData.IsReady ? Color.green : Color.gray;
    }

    public void OnRelease()
    {
    }
}
