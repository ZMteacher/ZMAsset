using System;
using System.Collections.Generic;

namespace ZM.Asset
{
    /// <summary>
    /// 00 保存一次资源模块构建期间的全部可变状态。
    /// 00 每次构建必须创建独立上下文，禁止不同模块通过静态字段共享资源列表、输出路径或热更参数。
    /// </summary>
    internal sealed class ModuleBuildContext
    {
        //00 当前上下文对应的模块配置；资源收集、配置写入和产物发布必须始终使用同一份配置快照。
        internal BundleModuleData ModuleData;

        //00 当前模块名称会进入 Bundle 名称、配置文件名和发布目录，因此由上下文统一持有。
        internal string ModuleName;

        //00 当前构建类型决定最终生成普通资源还是热更补丁。
        internal BuildType BuildType;

        //00 当前 Unity 构建目标必须贯穿收集、构建和清单生成，避免中途读取到编辑器切换后的平台。
        //00 工程运行时另有同名 BuildTarget 枚举，这里必须使用完全限定名锁定 Unity 编辑器构建平台。
        internal UnityEditor.BuildTarget BuildTarget;

        //00 热更应用版本、补丁版本和公告属于本次构建事务，不能泄漏到下一个模块。
        internal string HotAppVersion;
        internal int HotPatchVersion;
        internal string UpdateNotice;

        //00 普通 Bundle、热更补丁和业务配置分别使用独立路径，由编排器在构建开始时一次性确定。
        internal string BundleOutputPath;
        internal string HotAssetsOutputPath;
        internal string ConfigDataPath;

        //00 物理归属分析让 Prefab 收集器识别外部 Shared 依赖，避免再次把它写进 Business Bundle。
        internal ModuleOwnershipAnalysis OwnershipAnalysis;

        //00 保存本模块已经分配给某个 Bundle 的全部资源路径，用于防止规则之间重复收集。
        internal readonly List<string> AllBundlePaths = new List<string>();

        //00 文件夹规则和自动 Shared 分片最终都会汇总到这里，再统一生成 AssetBundleBuild。
        internal readonly Dictionary<string, List<string>> FolderBundles =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        //00 Prefab Bundle 单独保存，便于维持既有构建顺序和命名规则。
        internal readonly Dictionary<string, List<string>> PrefabBundles =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        //00 单文件包 Bundle 单独保存，每个文件独占一个 Bundle，不参与共享分析。
        internal readonly Dictionary<string, List<string>> SingleFileBundles =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        //00 Unity BuildPipeline 的最终输入列表只属于当前上下文。
        //00 AssetBundleBuild 也属于 UnityEditor 命名空间，完全限定可避免运行时同名类型污染上下文协议。
        internal readonly List<UnityEditor.AssetBundleBuild> BundleBuilds =
            new List<UnityEditor.AssetBundleBuild>();

        //00 WebGL CRC 必须在 Unity 原始构建目录仍保留伴随 manifest 时冻结；模块 staging 不再重复解析 Bundle。
        internal readonly Dictionary<string, uint> WebGlBundleCrcs =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        //00 显式 Entry 来源必须在规则收集时保留，配置写入阶段不能从 Bundle 列表反推。
        internal readonly HashSet<string> ExplicitEntryPaths =
            new HashSet<string>(StringComparer.Ordinal);

        //00 源文件规则保存稳定快照，防止配置写入和文件复制之间再次扫描得到不同结果。
        internal readonly List<SourceBuildEntry> SourceEntries = new List<SourceBuildEntry>();

        /// <summary>
        /// 00 清空所有由资源扫描产生的集合，但保留本次构建参数和输出路径。
        /// 00 该方法只在上下文首次初始化或明确重新收集时调用，不会影响其他模块。
        /// </summary>
        internal void ResetCollectedData()
        {
            //00 每个集合都原地清空，避免让正在引用上下文的协作组件拿到失效集合实例。
            AllBundlePaths.Clear();
            FolderBundles.Clear();
            PrefabBundles.Clear();
            SingleFileBundles.Clear();
            BundleBuilds.Clear();
            WebGlBundleCrcs.Clear();
            ExplicitEntryPaths.Clear();
            SourceEntries.Clear();
        }
    }

    /// <summary>
    /// 00 描述一个源文件规则在本次构建中固定下来的输入和输出信息。
    /// </summary>
    internal sealed class SourceBuildEntry
    {
        //00 Unity 工程内的 Assets/... 路径，用于 CRC、Entry 和配置生成。
        internal string AssetPath;

        //00 源文件的规范化绝对路径，用于构建完成后的确定性复制。
        internal string FullPath;

        //00 发布目录中的文件名；同一模块内必须保持忽略大小写唯一。
        internal string OutputFileName;
    }

    /// <summary>
    /// 00 描述一个资源路径在统一构建输入中的真实 Bundle 位置。
    /// </summary>
    internal readonly struct AssetBundleBuildLocation
    {
        //00 物理 Bundle 所属模块。
        internal readonly string ModuleName;
        //00 Unity 构建使用的完整 Bundle 文件名。
        internal readonly string BundleName;

        /// <summary>
        /// 00 创建不可变位置，防止配置写入期间被其他模块改写。
        /// </summary>
        internal AssetBundleBuildLocation(string moduleName, string bundleName)
        {
            ModuleName = moduleName;
            BundleName = bundleName;
        }
    }
}
