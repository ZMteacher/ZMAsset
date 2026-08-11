using System;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// 资源句柄的内部所有者边界。实现方必须在 Unity 主线程释放资源。
    /// </summary>
    internal interface IAssetHandleOwner
    {
        bool IsAssetHandleMainThread { get; }

        bool ReleaseAssetHandle(long handleId);
    }

    /// <summary>
    /// 句柄与 ResourceManager 共享的失效状态；模块强制卸载时不需要持有句柄对象本身。
    /// </summary>
    internal sealed class AssetHandleState
    {
        internal AssetHandleState(long handleId)
        {
            HandleId = handleId;
            IsActive = true;
        }

        internal long HandleId { get; }

        internal bool IsActive { get; private set; }

        internal void Invalidate()
        {
            IsActive = false;
        }
    }

    /// <summary>
    /// 表示一次独立的 Unity 资源使用权。每次成功加载都会返回新的句柄，使用结束后必须 Dispose。
    /// </summary>
    /// <typeparam name="T">由句柄持有的 Unity 资源类型。</typeparam>
    public sealed class AssetHandle<T> : IDisposable where T : UnityEngine.Object
    {
        private IAssetHandleOwner mOwner;
        private AssetHandleState mState;
        private T mAsset;

        internal AssetHandle(IAssetHandleOwner owner, AssetHandleState state, T asset, string path)
        {
            mOwner = owner ?? throw new ArgumentNullException(nameof(owner));
            mState = state ?? throw new ArgumentNullException(nameof(state));
            mAsset = asset != null ? asset : throw new ArgumentNullException(nameof(asset));
            Path = path ?? string.Empty;
        }

        /// <summary>
        /// 创建该句柄的规范化资源路径，用于诊断和错误报告。
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// 句柄是否仍登记在 ResourceManager 中，且资源对象仍然有效。
        /// </summary>
        public bool IsValid => mState != null && mState.IsActive && mAsset != null;

        /// <summary>
        /// 获取句柄持有的资源。句柄已释放或被模块强制失效时抛出异常。
        /// </summary>
        public T Asset
        {
            get
            {
                if (!TryGetAsset(out T asset))
                {
                    throw new ObjectDisposedException(
                        $"AssetHandle<{typeof(T).Name}>",
                        $"资源句柄已经释放或被模块强制失效，路径：{Path}");
                }

                return asset;
            }
        }

        /// <summary>
        /// 安全尝试获取资源；句柄无效时返回 false。
        /// </summary>
        public bool TryGetAsset(out T asset)
        {
            asset = IsValid ? mAsset : null;
            return asset != null;
        }

        /// <summary>
        /// 归还本句柄的独立使用权。该操作幂等，但必须在 Unity 主线程执行。
        /// </summary>
        public void Dispose()
        {
            AssetHandleState state = mState;
            if (state == null)
                return;

            if (!state.IsActive)
            {
                ClearReferences();
                return;
            }

            IAssetHandleOwner owner = mOwner;
            if (owner == null)
            {
                state.Invalidate();
                ClearReferences();
                return;
            }

            if (!owner.IsAssetHandleMainThread)
            {
                throw new InvalidOperationException(
                    $"AssetHandle<{typeof(T).Name}> 必须在 Unity 主线程释放，路径：{Path}");
            }

            owner.ReleaseAssetHandle(state.HandleId);
            state.Invalidate();
            ClearReferences();
        }

        private void ClearReferences()
        {
            mOwner = null;
            mState = null;
            mAsset = null;
        }
    }
}
