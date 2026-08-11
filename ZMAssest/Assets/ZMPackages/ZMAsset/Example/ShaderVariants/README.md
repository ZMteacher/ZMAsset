# Shader 变体审计、剔除与预热使用示例

## 1. 首次生成与打包

1. 打开 ZMAsset 构建窗口，进入 **Shader 变体** 页面。
2. 将剔除策略保持为 **仅审计（不剔除）**，配置档填写 `Default`，保存配置。
3. 正常构建一次目标模块。构建会生成审计报告、报告 JSON 和规范清单。
4. 回到 **最近构建报告**，点击 **从报告生成 SVC**。
5. 勾选 **注入生成 SVC**，再次构建目标模块。
6. 确认预热和画面正确后，再把剔除策略切换到 **生成 Allowlist（安全剔除）** 并重新构建。

安全剔除模式会校验模块、配置档、BuildTarget、Unity 版本、报告来源哈希、SVC 内容和 Shader 依赖。任一内容过期都会中止构建，不会带着不可信的 allowlist 静默出包。

## 2. 最小运行时调用

建议在 Loading 场景、首个使用该模块材质的场景出现之前执行：

```csharp
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using ZM.Asset;

public sealed class GameBootstrap : MonoBehaviour
{
    private void Start()
    {
        WarmUpAsync(this.GetCancellationTokenOnDestroy()).Forget(Debug.LogException);
    }

    private async UniTask WarmUpAsync(CancellationToken cancellationToken)
    {
        ShaderVariantPrewarmResult result =
            await ZMAsset.ShaderVariants.WarmUpModuleAsync(
                "Hall",
                "Default",
                cancellationToken: cancellationToken);

        if (!result.IsSuccess)
            Debug.LogError($"Shader 预热失败：{result.Status}，{result.Message}");
    }
}
```

该接口会自动初始化 `Hall` 模块，并从模块 AssetBundle 中加载对应的预热 Manifest 和 SVC。无需在调用前重复执行 `ZMAsset.Modules.InitializeAsync`。

## 3. 分帧、进度、取消和超时

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using ZM.Asset;

public static class LoadingShaderWarmup
{
    public static async UniTask<bool> RunAsync(CancellationToken cancellationToken)
    {
        // 中低端设备可降低到 8~16；PC/主机可根据实测提高。
        var options = new ShaderVariantPrewarmOptions(
            variantsPerFrame: 16,
            timeoutSeconds: 20f);

        var progress = new Progress<ShaderVariantPrewarmProgress>(value =>
        {
            Debug.Log($"Shader 预热：{value.WarmedVariantCount}/" +
                      $"{value.TotalVariantCount} ({value.NormalizedProgress:P0})");
        });

        ShaderVariantPrewarmResult result = await ZMAsset.ShaderVariants.WarmUpModuleAsync(
            "Battle",
            "Low",
            options,
            progress,
            cancellationToken);

        switch (result.Status)
        {
            case ShaderVariantPrewarmStatus.Succeeded:
            case ShaderVariantPrewarmStatus.AlreadyWarmed:
            case ShaderVariantPrewarmStatus.EmptyCollection:
                return true;
            case ShaderVariantPrewarmStatus.Cancelled:
                return false;
            case ShaderVariantPrewarmStatus.TimedOut:
                Debug.LogWarning("Shader 预热超时，可进入降级流程或延长 Loading 时间。");
                return false;
            default:
                Debug.LogError($"Shader 预热失败：{result.Status}，{result.Message}");
                return false;
        }
    }
}
```

传入 `MonoBehaviour.GetCancellationTokenOnDestroy()`，可在对象销毁或切场景时自动取消：

```csharp
bool warmed = await LoadingShaderWarmup.RunAsync(this.GetCancellationTokenOnDestroy());
```

## 4. 多模块顺序预热

同一模块和配置档不应并发预热。多个模块建议在 Loading 流程中顺序执行，以稳定单帧耗时和峰值内存：

```csharp
string[] modules = { "Hall", "Battle", "CommonUI" };
var options = new ShaderVariantPrewarmOptions(16, 30f);

foreach (string moduleName in modules)
{
    ShaderVariantPrewarmResult result = await ZMAsset.ShaderVariants.WarmUpModuleAsync(
        moduleName,
        "Default",
        options,
        cancellationToken: this.GetCancellationTokenOnDestroy());

    if (!result.IsSuccess)
        throw new InvalidOperationException(
            $"模块 {moduleName} Shader 预热失败：{result.Status}，{result.Message}");
}
```

## 5. 可直接挂载的示例组件

把 `ShaderVariantPrewarmExample` 挂到 Loading 场景对象，填写：

- `Module Name`：AssetBundle 模块名，例如 `Hall`。
- `Profile Name`：生成 SVC 时使用的配置档，例如 `Default`、`Low`、`High`。
- `Variants Per Frame`：每帧预热数，先用 `16` 或 `32`，再按真机 Profiler 调整。
- `Timeout Seconds`：业务允许的最长 Loading 时间。

组件公开了 `StartWarmUp()` 和 `CancelWarmUp()`，可以直接绑定 UI Button；也可以监听 `ProgressChanged`、`Completed`，把进度同步到自己的 Loading UI。

## 6. 应纳入版本管理的文件

- `Assets/ZMPackages/ZMAsset/Generated/ShaderVariants/...`：生成的 SVC 与运行时 Manifest，应提交。
- `ProjectSettings/ZMAssetShaderVariants/.../GeneratedAllowlist.json`：开启安全剔除时应提交。
- Shader 审计报告目录：建议作为 CI 构建产物保存；是否提交由团队审计制度决定。

每次修改 Shader、材质关键字、显式规则、Unity 版本、BuildTarget 或配置档后，都应重新走“审计构建 → 生成 SVC → 正式构建”。

## 7. 常见状态

- `Succeeded`：本次已完成预热。
- `AlreadyWarmed`：集合此前已预热，可直接继续。
- `EmptyCollection`：集合为空，无需执行预热，按成功完成处理。
- `Busy`：同一模块和配置档已有预热任务，避免重复请求。
- `MissingManifest`：未生成 SVC，或打包时没有注入生成资源。
- `InvalidManifest`：运行时清单与模块、配置档或集合不匹配，应重新生成并打包。
- `TimedOut`：在超时内未完成，降低每帧数量不会缩短总时间；可延长超时或拆分配置档。
- `ModuleInitializationFailed`：模块配置、下载文件或 AssetBundle 初始化失败，先排查模块加载链路。
