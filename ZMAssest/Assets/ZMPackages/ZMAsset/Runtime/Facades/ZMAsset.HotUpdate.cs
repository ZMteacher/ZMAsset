using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace ZM.ZMAsset
{
    public partial class ZMAsset
    {
        /// <summary>
        /// 资源版本检查、事务热更新和状态查询入口。
        /// </summary>
        public static class HotUpdate
        {
            
            /// <summary>
            /// 检测模块资源版本；只有 ConfirmedNoUpdate 或 UpdateAvailable 才代表远端确认成功，UnableToConfirm 时调用方必须阻止继续进入业务。
            /// </summary>
            public static UniTask<HotUpdateVersionCheckResult> CheckVersionAsync(string moduleName)
            {
                ValidateModuleName(moduleName);
                // 管理器层原生返回可等待结果，门面只做参数校验后直接透传。
                return InitializedInstance.mHotAssets.CheckAssetsVersionAsync(moduleName);
            }
            /// <summary>
            /// 按请求中的显式模块顺序执行原子热更新事务。
            /// </summary>
            public static UniTask<HotUpdateTransactionResult> UpdateAsync(HotUpdateTransactionRequest request)
            {
                if (request == null)
                    throw new ArgumentNullException(nameof(request));
                return InitializedInstance.mHotAssets.HotAssetsTransactionAsync(request);
            }

            /// <summary>
            /// 便捷执行单模块或多模块事务；框架不会自动推断或添加 Shared 的消费者模块。
            /// </summary>
            public static UniTask<HotUpdateTransactionResult> UpdateAsync(IReadOnlyList<string> orderedModules, bool checkAssetVersion = true, CancellationToken cancellationToken = default)
            {
                if (orderedModules == null)
                    throw new ArgumentNullException(nameof(orderedModules));

                return UpdateAsync(new HotUpdateTransactionRequest(orderedModules, checkAssetVersion, cancellationToken));
            }

           

            /// <summary>
            /// 获取模块热更新状态的只读快照，不向业务层暴露内部可变模块对象。
            /// </summary>
            public static HotAssetsModuleState GetModuleState(string moduleName)
            {
                ValidateModuleName(moduleName);
                return InitializedInstance.mHotAssets.GetHotAssetsModuleState(moduleName);
            }
        }
    }
}
