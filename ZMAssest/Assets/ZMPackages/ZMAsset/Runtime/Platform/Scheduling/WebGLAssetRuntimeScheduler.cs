using System;
using Cysharp.Threading.Tasks;

namespace ZM.ZMAsset
{
    /// <summary>
    /// WebGL 主线程调度器。
    /// Unity 2022.3 WebGL 首发不使用托管线程，配置解析直接在当前浏览器主线程完成。
    /// </summary>
    internal sealed class WebGLAssetRuntimeScheduler : IAssetRuntimeScheduler
    {
        public UniTask<T> RunCpuBoundAsync<T>(Func<T> operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            return UniTask.FromResult(operation());
        }

        public UniTask SwitchToMainThreadAsync()
        {
            // WebGL 资源主链本身运行在 Unity 主线程，不排入线程池也不产生无意义的帧延迟。
            return UniTask.CompletedTask;
        }
    }
}
