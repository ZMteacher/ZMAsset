using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ZM.ZMAsset
{
    /// <summary>
    /// Native 平台 Bundle 加载器。
    /// 本阶段只迁移现有文件加载和 AES 解密调用，不改变解密算法、读取路径或 Unity API 时序。
    /// </summary>
    internal sealed class NativeAssetBundleLoader : IAssetBundleLoader
    {
        public AssetBundle Load(AssetBundleLoadRequest request)
        {
            AssetBundleLocation location = request.Location;
            if (!location.IsEncrypted)
                return AssetBundle.LoadFromFile(location.UriOrPath);

            byte[] decryptedBytes = AES.AESFileByteDecrypt(location.UriOrPath, request.EncryptionKey);
            return AssetBundle.LoadFromMemory(decryptedBytes);
        }

        public async UniTask<AssetBundle> LoadAsync(AssetBundleLoadRequest request)
        {
            AssetBundleLocation location = request.Location;
            if (!location.IsEncrypted)
                return await AssetBundle.LoadFromFileAsync(location.UriOrPath);

            if (request.Purpose == AssetBundleLoadPurpose.Configuration)
            {
                // 配置 Bundle 保留原有“同步解密、异步创建 AssetBundle”的顺序，避免改变模块初始化时序。
                byte[] decryptedBytes = AES.AESFileByteDecrypt(location.UriOrPath, request.EncryptionKey);
                return await AssetBundle.LoadFromMemoryAsync(decryptedBytes);
            }

            // 普通加密 Bundle 保留原有“异步读取解密、同步创建 AssetBundle”的历史行为。
            bool isHotPath = location.SourceKind != AssetBundleSourceKind.Builtin;
            byte[] contentBytes = await AES.AESFileByteDecryptAwait(
                location.UriOrPath,
                request.EncryptionKey,
                isHotPath);
            return AssetBundle.LoadFromMemory(contentBytes);
        }
    }
}
