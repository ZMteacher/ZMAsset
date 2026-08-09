using System;
using Cysharp.Threading.Tasks;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 隔离 Native 后台线程与 WebGL 主线程异步执行差异。
    /// 调用方仍负责保证 Unity 对象 API 只在 Unity 主线程使用。
    /// </summary>
    internal interface IAssetRuntimeScheduler
    {
        UniTask<T> RunCpuBoundAsync<T>(Func<T> operation);

        UniTask SwitchToMainThreadAsync();
    }
}
