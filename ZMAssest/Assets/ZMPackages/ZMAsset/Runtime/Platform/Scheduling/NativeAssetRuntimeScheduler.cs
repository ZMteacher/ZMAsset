using System;
using Cysharp.Threading.Tasks;

namespace ZM.Asset
{
    /// <summary>
    /// Native 调度实现，继续使用 UniTask 线程池执行配置解析等纯 CPU 工作。
    /// </summary>
    internal sealed class NativeAssetRuntimeScheduler : IAssetRuntimeScheduler
    {
        public UniTask<T> RunCpuBoundAsync<T>(Func<T> operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            return UniTask.RunOnThreadPool(operation);
        }

        public async UniTask SwitchToMainThreadAsync()
        {
            await UniTask.SwitchToMainThread();
        }
    }
}
