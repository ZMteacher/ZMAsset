using System;
using System.Collections.Generic;

namespace ZM.Asset
{
    /// <summary>
    /// 集中管理一组资源句柄，适合窗口、页面或其他具有明确关闭时机的业务对象。
    /// </summary>
    public sealed class AssetHandleScope : IDisposable
    {
        private readonly List<IDisposable> mHandles = new List<IDisposable>();

        /// <summary>
        /// Scope 是否已经释放。释放后加入的新句柄会被立即 Dispose。
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 将句柄所有权交给 Scope。Scope 已释放时会立即释放传入句柄并返回 false。
        /// </summary>
        public bool TryAdd<T>(AssetHandle<T> handle) where T : UnityEngine.Object
        {
            if (handle == null)
                return false;

            if (IsDisposed)
            {
                handle.Dispose();
                return false;
            }

            mHandles.Add(handle);
            return true;
        }

        /// <summary>
        /// 从 Scope 移除指定句柄，并按需立即释放。
        /// </summary>
        public bool Remove<T>(AssetHandle<T> handle, bool dispose = true) where T : UnityEngine.Object
        {
            if (handle == null || !mHandles.Contains(handle))
                return false;

            if (dispose)
                handle.Dispose();
            mHandles.Remove(handle);
            return true;
        }

        /// <summary>
        /// 按逆序释放全部句柄。即使单个句柄失败，也会继续清理剩余句柄并汇总异常。
        /// </summary>
        public void Dispose()
        {
            if (IsDisposed)
                return;

            List<Exception> exceptions = null;
            for (int index = mHandles.Count - 1; index >= 0; index--)
            {
                try
                {
                    mHandles[index]?.Dispose();
                    mHandles.RemoveAt(index);
                }
                catch (Exception exception)
                {
                    exceptions ??= new List<Exception>();
                    exceptions.Add(exception);
                }
            }

            IsDisposed = mHandles.Count == 0;
            if (exceptions != null)
                throw new AggregateException("释放 AssetHandleScope 时有一个或多个句柄失败。", exceptions);
        }
    }
}
