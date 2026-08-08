using UnityEngine;

/// <summary>
/// RemoteAsset 测试工具：单个 icon 的数据与加载状态。
/// </summary>
public class RemoteAssetTestItemData
{
    public string Name;
    public string Path;
    public Texture Texture;
    public bool IsReady;

    /// <summary>
    /// 硬编码 9 个远端 icon（对应 Assets/GameData/RemoteAsset/Itemicon/ 下的文件）。
    /// </summary>
    public static readonly RemoteAssetTestItemData[] sItems = CreateItems();

    private static RemoteAssetTestItemData[] CreateItems()
    {
        const string prefix = "Assets/GameData/RemoteAsset/Itemicon/icon";
        var items = new RemoteAssetTestItemData[9];
        for (int i = 0; i < items.Length; i++)
        {
            int index = i + 1;
            items[i] = new RemoteAssetTestItemData
            {
                Name = "icon" + index,
                Path = prefix + index + ".png",
            };
        }
        return items;
    }
}
