using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using ZM.ZMAsset;

public class RemoteAssetDemo : MonoBehaviour
{
    public RawImage rawImageAsync;

    private AssetsRequest mInstanceRequest;
    private AssetHandle<Texture> mTextureHandle;
    private int mTextureRequestVersion;

    private void Awake()
    {
        InitializeAsync(this.GetCancellationTokenOnDestroy()).Forget(Debug.LogException);
    }

    private async UniTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            AssetsRequest request = await ZMAsset.Remote.InstantiateAsync(
                AssetsPathConfig.GAME_ITEM_PATH + "6013/6013",
                null,
                BundleModuleName.AddressAsset);

            if (cancellationToken.IsCancellationRequested)
            {
                request?.Release();
                return;
            }

            mInstanceRequest = request;
            if (request?.obj != null)
                request.obj.transform.SetParent(transform.GetChild(0).GetChild(0));

            await UniTask.Delay(1000, cancellationToken: cancellationToken);
            await ReplaceTextureAsync(
                AssetsPathConfig.GAME_ITEM_PATH + "6001/huafei.png",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 对象销毁属于正常取消路径，不向控制台报告错误。
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Q))
        {
            ReplaceTextureAsync(
                    AssetsPathConfig.GAME_ITEM_PATH + "6001/huafei.png",
                    this.GetCancellationTokenOnDestroy())
                .Forget(Debug.LogException);
        }
    }

    private async UniTask ReplaceTextureAsync(string path, CancellationToken cancellationToken)
    {
        int requestVersion = ++mTextureRequestVersion;
        AssetHandle<Texture> loadedHandle = null;
        try
        {
            loadedHandle = await ZMAsset.Remote.LoadAsync<Texture>(
                path,
                BundleModuleName.AddressAsset,
                cancellationToken: cancellationToken);

            if (cancellationToken.IsCancellationRequested ||
                requestVersion != mTextureRequestVersion ||
                rawImageAsync == null ||
                loadedHandle == null ||
                !loadedHandle.IsValid)
            {
                return;
            }

            AssetHandle<Texture> previousHandle = mTextureHandle;
            rawImageAsync.texture = loadedHandle.Asset;
            mTextureHandle = loadedHandle;
            loadedHandle = null;
            previousHandle?.Dispose();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 对象销毁或新请求替换旧请求属于正常取消路径。
        }
        finally
        {
            loadedHandle?.Dispose();
        }
    }

    private void OnDestroy()
    {
        ++mTextureRequestVersion;
        if (rawImageAsync != null)
            rawImageAsync.texture = null;
        mTextureHandle?.Dispose();
        mTextureHandle = null;
        mInstanceRequest?.Release();
        mInstanceRequest = null;
    }
}
