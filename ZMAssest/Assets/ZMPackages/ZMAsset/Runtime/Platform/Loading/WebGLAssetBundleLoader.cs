using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ZM.Asset
{
    /// <summary>
    /// WebGL AssetBundle 加载器。
    /// 浏览器中的 StreamingAssets 是 URL，所有未驻留 Bundle 都必须通过 UnityWebRequest 异步取得。
    /// </summary>
    internal sealed class WebGLAssetBundleLoader : IAssetBundleLoader
    {
        public AssetBundle Load(AssetBundleLoadRequest request)
        {
            AssetBundleLocation location = request.Location;
            throw new InvalidOperationException(
                $"WebGL 无法同步请求尚未驻留内存的 AssetBundle。模块：{location.ModuleName}，" +
                $"Bundle：{location.BundleName}。请改用 ZMAsset.Resources.LoadAsync、InstantiateAsync，" +
                "或先通过异步接口完成预加载。");
        }

        public async UniTask<AssetBundle> LoadAsync(AssetBundleLoadRequest request)
        {
            AssetBundleLocation location = request.Location;
            ValidateLocation(location);

            using (UnityWebRequest webRequest = CreateRequest(location))
            {
                await webRequest.SendWebRequest();
                if (webRequest.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException(
                        $"WebGL AssetBundle 请求失败。模块：{location.ModuleName}，Bundle：{location.BundleName}，" +
                        $"来源：{location.SourceKind}，地址：{AssetLogUtility.SanitizeUrl(location.UriOrPath)}，" +
                        $"UnityWebRequest：{webRequest.result}，错误：{webRequest.error}");
                }

                AssetBundle assetBundle = DownloadHandlerAssetBundle.GetContent(webRequest);
                if (assetBundle == null)
                {
                    throw new InvalidOperationException(
                        $"WebGL 请求已完成，但没有取得 AssetBundle。模块：{location.ModuleName}，" +
                        $"Bundle：{location.BundleName}，地址：{AssetLogUtility.SanitizeUrl(location.UriOrPath)}");
                }

                return assetBundle;
            }
        }

        /// <summary>
        /// 创建与 Location 校验信息一致的 Unity 请求。
        /// 内嵌 Bundle 可以没有 Hash；后续 RemoteAsset 阶段提供 Hash 时自动启用 Unity 缓存键。
        /// </summary>
        private static UnityWebRequest CreateRequest(AssetBundleLocation location)
        {
            if (string.IsNullOrWhiteSpace(location.ContentHash))
                return UnityWebRequestAssetBundle.GetAssetBundle(location.UriOrPath, location.Crc);

            Hash128 contentHash;
            try
            {
                contentHash = Hash128.Parse(location.ContentHash);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"WebGL AssetBundle Hash 无效。模块：{location.ModuleName}，" +
                    $"Bundle：{location.BundleName}，Hash：{location.ContentHash}",
                    exception);
            }

            if (!contentHash.isValid)
            {
                throw new InvalidOperationException(
                    $"WebGL AssetBundle Hash 解析后无效。模块：{location.ModuleName}，" +
                    $"Bundle：{location.BundleName}，Hash：{location.ContentHash}");
            }

            return UnityWebRequestAssetBundle.GetAssetBundle(
                location.UriOrPath,
                contentHash,
                location.Crc);
        }

        internal static void ValidateLocation(AssetBundleLocation location)
        {
            if (string.IsNullOrWhiteSpace(location.ModuleName))
                throw new ArgumentException("WebGL AssetBundle 模块名称不能为空。", nameof(location));
            if (string.IsNullOrWhiteSpace(location.BundleName))
                throw new ArgumentException("WebGL AssetBundle 文件名不能为空。", nameof(location));
            if (string.IsNullOrWhiteSpace(location.UriOrPath))
                throw new ArgumentException("WebGL AssetBundle URL 不能为空。", nameof(location));
            if (location.IsEncrypted)
            {
                throw new PlatformNotSupportedException(
                    $"WebGL 首发版本不支持当前整包 AES。模块：{location.ModuleName}，" +
                    $"Bundle：{location.BundleName}。请关闭资源加密并通过 HTTPS、Hash 和 CRC 发布 WebGL 资源。");
            }
            // WebGL 的 Remote 与 HotUpdate 都是经过 Hash/CRC 校验的 URL。HotUpdate 表示
            // 活动版本指针选中的浏览器缓存资源，并不表示 Native 平台的本地热更目录。
            if ((location.SourceKind == AssetBundleSourceKind.Remote ||
                 location.SourceKind == AssetBundleSourceKind.HotUpdate) &&
                string.IsNullOrWhiteSpace(location.ContentHash))
                throw new InvalidOperationException(
                    $"WebGL {location.SourceKind} 缺少浏览器缓存 Hash。模块：{location.ModuleName}，Bundle：{location.BundleName}。");
        }
    }
}
