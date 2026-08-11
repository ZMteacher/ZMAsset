using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// 区分配置 Bundle 和普通内容 Bundle 的历史异步加载时序。
    /// Native 配置 Bundle 使用异步内存创建，普通加密 Bundle 保留现有的同步内存创建行为。
    /// </summary>
    internal enum AssetBundleLoadPurpose
    {
        Content,
        Configuration
    }

    /// <summary>
    /// 封装一次 Bundle 加载所需的位置、用途和加密密钥。
    /// 密钥只在调用栈中传递，不会写入日志或持久化元数据。
    /// </summary>
    internal readonly struct AssetBundleLoadRequest
    {
        internal AssetBundleLoadRequest(
            AssetBundleLocation location,
            AssetBundleLoadPurpose purpose,
            string encryptionKey)
        {
            Location = location;
            Purpose = purpose;
            EncryptionKey = encryptionKey;
        }

        internal AssetBundleLocation Location { get; }

        internal AssetBundleLoadPurpose Purpose { get; }

        internal string EncryptionKey { get; }
    }

    /// <summary>
    /// 把平台无关的 Bundle 位置转换为 Unity AssetBundle。
    /// 实现只负责读取和创建 Bundle，不管理引用计数、依赖图或资源对象缓存。
    /// </summary>
    internal interface IAssetBundleLoader
    {
        AssetBundle Load(AssetBundleLoadRequest request);

        UniTask<AssetBundle> LoadAsync(AssetBundleLoadRequest request);
    }
}
