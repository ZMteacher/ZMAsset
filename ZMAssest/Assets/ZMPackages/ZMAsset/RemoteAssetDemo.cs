using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using ZM.ZMAsset;

public class RemoteAssetDemo : MonoBehaviour
{
    public RawImage rawImageAsync;

    private async void Awake()
    {
        AssetsRequest request = await ZMAsset.Remote.InstantiateAsync(
            AssetsPathConfig.GAME_ITEM_PATH + "6013/6013",
            null,
            BundleModuleName.AddressAsset);

        if (request?.obj != null)
            request.obj.transform.SetParent(transform.GetChild(0).GetChild(0));

        await UniTask.Delay(1000);
        rawImageAsync.texture = await ZMAsset.Remote.LoadAsync<Texture>(
            AssetsPathConfig.GAME_ITEM_PATH + "6001/huafei.png",
            BundleModuleName.AddressAsset);
    }

    private async void Update()
    {
        if (Input.GetKeyDown(KeyCode.Q))
        {
            rawImageAsync.texture = await ZMAsset.Remote.LoadAsync<Texture>(
                AssetsPathConfig.GAME_ITEM_PATH + "6001/huafei.png",
                BundleModuleName.AddressAsset);
        }
    }
}
