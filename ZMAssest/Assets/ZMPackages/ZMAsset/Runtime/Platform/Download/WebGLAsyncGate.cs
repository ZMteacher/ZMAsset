using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace ZM.ZMAsset
{
    /// <summary>
    /// WebGL 单线程环境使用的协作式异步门。避免 SemaphoreSlim 等待者依赖线程池续体而永久挂起。
    /// </summary>
    internal sealed class WebGLAsyncGate
    {
        private readonly object mLock = new object();
        private readonly Queue<UniTaskCompletionSource<bool>> mWaiters =
            new Queue<UniTaskCompletionSource<bool>>();
        private int mAvailableCount;

        internal WebGLAsyncGate(int initialCount)
        {
            if (initialCount <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(initialCount));
            mAvailableCount = initialCount;
        }

        internal async UniTask WaitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UniTaskCompletionSource<bool> waiter;
            lock (mLock)
            {
                if (mAvailableCount > 0)
                {
                    mAvailableCount--;
                    return;
                }

                waiter = new UniTaskCompletionSource<bool>();
                mWaiters.Enqueue(waiter);
            }

            using (cancellationToken.Register(
                       state => ((UniTaskCompletionSource<bool>)state).TrySetCanceled(cancellationToken),
                       waiter))
            {
                await waiter.Task;
            }
        }

        internal void Release()
        {
            lock (mLock)
            {
                while (mWaiters.Count > 0)
                {
                    if (mWaiters.Dequeue().TrySetResult(true))
                        return;
                }

                mAvailableCount++;
            }
        }
    }
}
