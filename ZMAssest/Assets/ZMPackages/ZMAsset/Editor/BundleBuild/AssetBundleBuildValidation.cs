using System;
using System.Collections.Generic;

namespace ZM.ZMAsset
{
    /// <summary>
    /// Unity 批处理模式下的资源构建与配置重生成入口。
    /// </summary>
    public static class AssetBundleBuildValidation
    {
        /// <summary>
        /// 构建配置中全部启用模块；任一模块失败时让 Unity 进程以失败结束。
        /// </summary>
        public static void BuildAllEnabledModules()
        {
            BuildBundleConfigura configuration = BuildBundleConfigura.Instance;
            if (configuration == null || configuration.AssetBundleConfig == null)
                throw new InvalidOperationException("找不到 BuildBundleConfigura 或模块列表为空。");

            //00 批处理先收集全部启用模块，再交给正式统一编排器计算 Shared 依赖闭包。
            List<BundleModuleData> enabledModules = new List<BundleModuleData>();
            foreach (BundleModuleData module in configuration.AssetBundleConfig)
            {
                if (module == null || !module.isBuild) continue;
                //00 这里只冻结配置引用，不逐模块发布，保证 CI 与 AssetBundle Hub 都使用同一事务边界。
                enabledModules.Add(module);
            }

            if (enabledModules.Count == 0)
                throw new InvalidOperationException("没有启用的 AssetBundle 模块可供构建。");
            //00 同步入口会完整推进 staged 枚举器，任一校验、Unity 构建或发布错误都会让批处理失败。
            MultiModuleBuildOrchestrator.Build(enabledModules);
        }
    }
}
