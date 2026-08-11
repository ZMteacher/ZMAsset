using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    ///  保存构建期根据真实资源依赖得到的模块依赖图。
    ///  值集合使用模块名称而不是对象引用，便于构建编排和运行时配置生成共享同一协议。
    /// </summary>
    internal sealed class ModuleOwnershipAnalysis
    {
        //00 每个模块最多保存一次相同依赖，避免多个资源路径让模块级关系重复膨胀。
        private readonly Dictionary<string, HashSet<string>> mDependencies =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        //00 共享模块名 -> 资源路径 -> 消费业务模块集合，用于让 Shared 构建上下文显式接管外部引用资源。
        private readonly Dictionary<string, Dictionary<string, HashSet<string>>> mExternalAssetConsumers =
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        //00 目录按物理路径保存，资源收集器可据此把外部 Shared 依赖排除出 Business Bundle 的 assetNames。
        private readonly List<(string moduleName, string assetDirectory)> mDirectoryOwners =
            new List<(string moduleName, string assetDirectory)>();

        /// <summary>
        ///  登记一条已经通过角色矩阵校验的模块依赖。
        /// </summary>
        internal void AddDependency(string consumerModule, string dependencyModule, string dependencyAssetPath)
        {
            //00 自依赖属于模块内部资源关系，不进入跨模块依赖图。
            if (string.Equals(consumerModule, dependencyModule, StringComparison.OrdinalIgnoreCase)) return;
            //00 第一次看到消费者时创建独立集合。
            if (!mDependencies.TryGetValue(consumerModule, out HashSet<string> dependencies))
            {
                dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                mDependencies.Add(consumerModule, dependencies);
            }
            //00 HashSet 保证一条 Business→Shared 关系只占一个模块租约。
            dependencies.Add(dependencyModule);

            //00 模块级关系用于运行时租约，资源级消费者用于构建时把真实文件分配给 Shared Bundle。
            if (string.IsNullOrWhiteSpace(dependencyAssetPath)) return;
            //00 第一次看到该 Shared 模块时创建资源消费者表。
            if (!mExternalAssetConsumers.TryGetValue(
                    dependencyModule,
                    out Dictionary<string, HashSet<string>> assetConsumers))
            {
                assetConsumers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                mExternalAssetConsumers.Add(dependencyModule, assetConsumers);
            }
            //00 同一个 Shared 资源可能被多个 Business 模块引用，消费者集合决定稳定分组签名。
            if (!assetConsumers.TryGetValue(dependencyAssetPath, out HashSet<string> consumers))
            {
                consumers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                assetConsumers.Add(dependencyAssetPath, consumers);
            }
            //00 相同业务模块通过多条 Prefab 链路抵达同一资源时只登记一次。
            consumers.Add(consumerModule);
        }

        /// <summary>
        ///  返回稳定排序的直接依赖快照，调用方不能修改内部集合。
        /// </summary>
        internal IReadOnlyList<string> GetDirectDependencies(string moduleName)
        {
            //00 没有跨模块依赖时返回空数组，避免调用方处理 null。
            if (string.IsNullOrWhiteSpace(moduleName) ||
                !mDependencies.TryGetValue(moduleName, out HashSet<string> dependencies))
                return Array.Empty<string>();
            //00 稳定排序保证构建闭包、配置 JSON 和测试结果可复现。
            return dependencies.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        ///  返回指定 Shared 模块被业务模块实际消费的全部资源路径快照。
        /// </summary>
        internal IReadOnlyList<string> GetExternallyConsumedAssets(string moduleName)
        {
            //00 普通模块或当前没有外部消费者时返回空数组，调用方无需处理 null。
            if (string.IsNullOrWhiteSpace(moduleName) ||
                !mExternalAssetConsumers.TryGetValue(
                    moduleName,
                    out Dictionary<string, HashSet<string>> assetConsumers))
                return Array.Empty<string>();
            //00 路径稳定排序保证自动 Shared Bundle 分片在不同机器上保持一致。
            return assetConsumers.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        ///  返回一个 Shared 资源对应的业务模块消费者签名。
        /// </summary>
        internal string GetExternalConsumerSignature(string moduleName, string assetPath)
        {
            //00 缺少登记表示调用方传入了不属于外部消费集合的资源，必须返回空让构建器拒绝。
            if (string.IsNullOrWhiteSpace(moduleName) || string.IsNullOrWhiteSpace(assetPath) ||
                !mExternalAssetConsumers.TryGetValue(
                    moduleName,
                    out Dictionary<string, HashSet<string>> assetConsumers) ||
                !assetConsumers.TryGetValue(assetPath, out HashSet<string> consumers))
                return string.Empty;
            //00 使用完整模块名并稳定排序，消费者集合不变时 Bundle 摘要和分片名称不变。
            return string.Join("|", consumers.OrderBy(name => name, StringComparer.Ordinal));
        }

        /// <summary>
        ///  登记已经通过非重叠门禁的目录拥有者。
        /// </summary>
        internal void AddDirectoryOwner(string moduleName, string assetDirectory)
        {
            //00 同一目录不会重复出现；保持列表即可支持目录数量很小的模块配置。
            mDirectoryOwners.Add((moduleName, assetDirectory));
        }

        /// <summary>
        ///  根据资源物理路径返回唯一所属模块；返回空表示资源无主。
        /// </summary>
        internal string GetPhysicalOwnerModule(string assetPath)
        {
            //00 路径边界比较与正式校验保持一致，避免 GameA 错误匹配 GameAB。
            foreach ((string moduleName, string assetDirectory) owner in mDirectoryOwners)
            {
                if (string.Equals(assetPath, owner.assetDirectory, StringComparison.OrdinalIgnoreCase) ||
                    assetPath.StartsWith(owner.assetDirectory + "/", StringComparison.OrdinalIgnoreCase))
                    return owner.moduleName;
            }
            //00 无匹配目录时返回 null；正式构建前的校验会先给出无主依赖错误。
            return null;
        }
    }

    /// <summary>
    ///  在构建写入任何产物前，校验所有资源模块的物理目录归属和依赖边界。
    ///  资源归属只由文件所在目录决定，与资源最终是否为可主动加载 Entry 无关。
    ///  第三期只允许 Business → Shared；Business → Business 与 Shared → Business 继续作为构建错误。
    /// </summary>
    internal static class AssetEntryOwnershipValidator
    {
        /// <summary>
        ///  单次最多展示的违规数量，避免错误窗口因历史工程问题过多而失去可读性。
        /// </summary>
        private const int MaxReportedViolationCount = 50;

        /// <summary>
        /// 保存一条配置目录的唯一拥有者；assetDirectory 始终使用 Assets/... 规范路径。
        /// </summary>
        private sealed class DirectoryOwner
        {
            public string moduleName;
            public string ruleName;
            public string assetDirectory;
            public bool isModuleRoot;
        }

        /// <summary>
        ///  保存规则直接选中的资源以及是否需要交给 Unity 解析依赖。
        /// </summary>
        private sealed class ExplicitAsset
        {
            public string moduleName;
            public string ruleName;
            public string assetPath;
            public bool inspectUnityDependencies;
            //00 二期选择“开放 Prefab 及其依赖”时，合法递归依赖也会进入运行时 CRC 索引，必须同步执行真碰撞校验。
            public bool exposeUnityDependenciesAsEntries;
        }

        /// <summary>
        ///  保存显式资源的唯一配置归属，用于同时拒绝同模块规则重叠和跨模块重复配置。
        /// </summary>
        private sealed class EntryOwner
        {
            public string moduleName;
            public string ruleName;
        }

        /// <summary>
        ///  描述一条依赖边界违规；依赖链仅在确认违规以后惰性计算。
        /// </summary>
        private sealed class DependencyViolation
        {
            public ExplicitAsset sourceAsset;
            public string dependencyPath;
            public DirectoryOwner dependencyOwner;
            public IReadOnlyList<string> dependencyChain;
        }

        /// <summary>
        ///  校验全部已配置模块以及本次目标模块，不受 isBuild 开关影响。
        ///  校验发生在旧构建目录被清理之前，失败时不会破坏上一次可用产物。
        /// </summary>
        /// <param name="targetModule">本次准备构建的资源模块。</param>
        /// <exception cref="InvalidOperationException">模块名称、物理归属、依赖边界或 CRC 发生冲突。</exception>
        public static void ValidateBeforeBuild(BundleModuleData targetModule)
        {
            //00 单模块旧入口复用多模块分析，确保 UI、批处理和统一构建得到完全一致的门禁结果。
            AnalyzeBeforeBuild(new[] { targetModule });
        }

        /// <summary>
        ///  校验全部模块物理归属并返回允许的跨模块依赖图，供统一构建编排器消费。
        /// </summary>
        internal static ModuleOwnershipAnalysis AnalyzeBeforeBuild(IEnumerable<BundleModuleData> targetModules)
        {
            //00 枚举立即固化，避免调用方在扫描过程中改变选中集合。
            List<BundleModuleData> targetSnapshot = targetModules?
                .Where(module => module != null)
                .ToList() ?? new List<BundleModuleData>();
            //00 至少需要一个有效目标，才能确定尚未保存配置对象的替换关系。
            if (targetSnapshot.Count == 0 || targetSnapshot.Any(module => string.IsNullOrWhiteSpace(module.moduleName)))
                throw new InvalidOperationException("AssetBundle 构建模块为空或模块名称未配置。");

            //00 所有模块共同参与扫描，才能发现目标模块与未勾选构建模块之间的潜在冲突。
            List<BundleModuleData> modules = CollectModules(targetSnapshot);
            //00 目录拥有者用于后续按物理位置判断每一条依赖究竟属于哪个模块。
            List<DirectoryOwner> directoryOwners = new List<DirectoryOwner>();
            //00 显式资源列表用于覆盖预制体分包、整目录打包和子目录分包等所有 AssetBundle 入口。
            List<ExplicitAsset> explicitAssets = new List<ExplicitAsset>();
            //00 Entry 字典保持原有的规则重复检测能力，路径比较忽略 Windows 大小写差异。
            Dictionary<string, EntryOwner> entryOwners =
                new Dictionary<string, EntryOwner>(StringComparer.OrdinalIgnoreCase);
            //00 CRC 字典继续提前拦截不同资源路径映射到相同 CRC32 的真实碰撞。
            Dictionary<uint, string> crcPaths = new Dictionary<uint, string>();
            //00 模块名称比较忽略大小写，因为输出文件名最终会被转换为小写。
            Dictionary<string, string> moduleNames =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            //00 角色字典用于依赖矩阵判定，不把 Entry 状态误当成模块身份。
            Dictionary<string, BundleModuleRole> moduleRoles =
                new Dictionary<string, BundleModuleRole>(StringComparer.OrdinalIgnoreCase);
            //00 当前版本最多允许一个 Shared，避免业务模块的公共依赖目标产生二义。
            string sharedModuleName = null;
            //00 合法 Business→Shared 关系会集中写入该分析结果。
            ModuleOwnershipAnalysis analysis = new ModuleOwnershipAnalysis();

            //00 第一轮只收集配置事实，不在目录列表尚不完整时提前判断依赖归属。
            foreach (BundleModuleData module in modules)
            {
                //00 模块名称在 CollectModules 中已经过滤空值，此处统一去除用户误输入的首尾空格。
                string moduleName = module.moduleName.Trim();
                //00 忽略大小写后同名的模块会生成相同产物前缀，必须在构建前明确拒绝。
                if (moduleNames.TryGetValue(moduleName, out string existingModuleName))
                {
                    throw new InvalidOperationException(
                        $"AssetBundle 模块名称重复或忽略大小写后冲突：{existingModuleName} 与 {moduleName}。请保证模块名称唯一。");
                }

                //00 登记模块名称后，再收集它的配置目录、显式资源和 CRC 信息。
                moduleNames.Add(moduleName, moduleName);
                //00 未知角色值不能回退为 Business，否则未来序列化值可能绕过依赖矩阵。
                if (module.moduleRole != BundleModuleRole.Business && module.moduleRole != BundleModuleRole.Shared)
                    throw new InvalidOperationException($"模块 {moduleName} 使用了不支持的模块角色：{module.moduleRole}。");
                //00 第二个 Shared 在配置层和构建层都会被拒绝，批处理或手改资产也无法绕过。
                if (module.moduleRole == BundleModuleRole.Shared)
                {
                    if (sharedModuleName != null)
                        throw new InvalidOperationException(
                            $"当前版本最多允许一个共享模块：{sharedModuleName} 与 {moduleName} 同时被标记为 Shared。");
                    sharedModuleName = moduleName;
                }
                //00 保存本轮不可变角色快照，后续不再读取可变模块对象。
                moduleRoles.Add(moduleName, module.moduleRole);
                CollectModuleDefinition(module, directoryOwners, explicitAssets, entryOwners, crcPaths);
            }

            //00 必须先拒绝相等或嵌套目录，后续 FindPhysicalOwner 才能保证最多返回一个拥有者。
            ValidateDirectoryOwnership(directoryOwners);
            //00 只有目录完全无歧义后才把归属快照暴露给构建收集器。
            foreach (DirectoryOwner owner in directoryOwners)
                analysis.AddDirectoryOwner(owner.moduleName, owner.assetDirectory);
            //00 依赖行为只校验本次选中模块及扫描中实际发现的 Shared 闭包，未参与构建的业务模块不能阻塞当前任务。
            IReadOnlyCollection<string> targetModuleNames = targetSnapshot
                .Select(module => module.moduleName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ValidateDependencyOwnership(
                explicitAssets,
                directoryOwners,
                crcPaths,
                moduleRoles,
                targetModuleNames,
                analysis);
            //00 单独构建 Shared 时仍要知道未选业务模块正在消费哪些 Shared 资源，否则 Shared 收集结果会把它们误判为删除。
            //00 该扫描只补充消费者事实，不校验未选业务模块、不改变构建目标，也不会把业务模块加入构建闭包。
            CollectSelectedSharedConsumers(
                targetSnapshot,
                explicitAssets,
                directoryOwners,
                moduleRoles,
                analysis);
            //00 只有全部目录、CRC 和依赖边界都通过后才返回可供构建使用的依赖图。
            return analysis;
        }

        /// <summary>
        ///  为显式选择的 Shared 收集当前工程全部 Business 消费者，保证 Shared 独立构建输入保持完整。
        /// </summary>
        private static void CollectSelectedSharedConsumers(
            IReadOnlyList<BundleModuleData> targetModules,
            IReadOnlyList<ExplicitAsset> explicitAssets,
            List<DirectoryOwner> directoryOwners,
            IReadOnlyDictionary<string, BundleModuleRole> moduleRoles,
            ModuleOwnershipAnalysis analysis)
        {
            HashSet<string> selectedSharedNames = new HashSet<string>(
                targetModules
                    .Where(module => module.moduleRole == BundleModuleRole.Shared)
                    .Select(module => module.moduleName.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (selectedSharedNames.Count == 0) return;

            IEnumerable<ExplicitAsset> businessAssets = explicitAssets
                .Where(asset => asset.inspectUnityDependencies &&
                                moduleRoles.TryGetValue(asset.moduleName, out BundleModuleRole role) &&
                                role == BundleModuleRole.Business)
                .OrderBy(asset => asset.moduleName, StringComparer.Ordinal)
                .ThenBy(asset => asset.assetPath, StringComparer.Ordinal);
            foreach (ExplicitAsset sourceAsset in businessAssets)
            {
                IEnumerable<string> dependencies = AssetDatabase.GetDependencies(sourceAsset.assetPath, true)
                    .Select(NormalizeAssetPath)
                    .Where(IsBundleableAssetPath)
                    .Where(path => !string.Equals(path, sourceAsset.assetPath, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (string dependencyPath in dependencies)
                {
                    DirectoryOwner owner = FindPhysicalOwner(dependencyPath, directoryOwners);
                    if (owner == null || !selectedSharedNames.Contains(owner.moduleName)) continue;
                    //00 只登记 Business→所选 Shared 的真实消费资源；其他跨模块关系仍由正常目标闭包校验负责。
                    analysis.AddDependency(sourceAsset.moduleName, owner.moduleName, dependencyPath);
                }
            }
        }

        /// <summary>
        ///  收集配置中的所有有效模块，并确保本次目标模块即使尚未保存也能参与校验。
        /// </summary>
        private static List<BundleModuleData> CollectModules(IReadOnlyCollection<BundleModuleData> targetModules)
        {
            //00 返回独立列表，避免校验过程修改 ScriptableObject 中持久化的模块集合。
            List<BundleModuleData> modules = new List<BundleModuleData>();
            //00 配置资产可能尚未创建或加载失败，空值由目标模块兜底。
            List<BundleModuleData> configuredModules = BuildBundleConfigura.Instance == null
                ? null
                : BuildBundleConfigura.Instance.AssetBundleConfig;

            //00 以忽略大小写的模块名保存目标快照，未保存的多个编辑对象都可以一次替换进全局配置。
            Dictionary<string, BundleModuleData> targetsByName = targetModules.ToDictionary(
                module => module.moduleName.Trim(),
                module => module,
                StringComparer.OrdinalIgnoreCase);
            //00 记录已经替换进结果的目标名称，末尾再补充真正的新建模块。
            HashSet<string> includedTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            //00 isBuild 只控制本轮是否打包，不能取消模块对其目录中资源的所有权。
            if (configuredModules != null)
            {
                foreach (BundleModuleData module in configuredModules)
                {
                    //00 旧序列化数据可能包含空槽或空名称，这些无效项不参与归属判定。
                    if (module == null || string.IsNullOrWhiteSpace(module.moduleName)) continue;
                    //00 同名目标无论是否为同一对象，都使用调用方传入的最新快照。
                    string configuredName = module.moduleName.Trim();
                    if (targetsByName.TryGetValue(configuredName, out BundleModuleData targetModule))
                    {
                        modules.Add(targetModule);
                        includedTargetNames.Add(configuredName);
                        continue;
                    }

                    //00 非目标模块保留原配置引用，继续参与全局物理归属检查。
                    modules.Add(module);
                }
            }

            //00 新建但尚未保存的模块仍需完整校验，避免第一次构建绕过唯一归属规则。
            foreach (KeyValuePair<string, BundleModuleData> targetPair in targetsByName)
                if (!includedTargetNames.Contains(targetPair.Key)) modules.Add(targetPair.Value);
            //00 返回本轮稳定模块快照，后续阶段不再访问可变的配置集合。
            return modules;
        }

        /// <summary>
        ///  收集一个模块的物理拥有目录和规则直接选中的资源。
        /// </summary>
        private static void CollectModuleDefinition(
            BundleModuleData module,
            List<DirectoryOwner> directoryOwners,
            List<ExplicitAsset> explicitAssets,
            Dictionary<string, EntryOwner> entryOwners,
            Dictionary<uint, string> crcPaths)
        {
            //00 后续所有诊断统一使用去除首尾空格后的模块名称。
            string moduleName = module.moduleName.Trim();
            //00 按全局资源总目录与模块名自动计算约定根目录，模块窗口不再要求重复填写物理路径。
            string moduleRootDirectory = TryResolveConventionalModuleRoot(moduleName);
            //00 同名目录存在时只登记一次，避免多个收集规则在同一模块内产生虚假的父子重叠。
            if (moduleRootDirectory != null)
                RegisterDirectoryOwner(
                    moduleName,
                    "模块资源根目录",
                    moduleRootDirectory,
                    directoryOwners,
                    true);
            //00 未知策略值可能来自损坏或未来版本配置，即使当前目录尚无 Prefab 也不能静默通过构建门禁。
            if (module.prefabDependencyEntryMode != PrefabDependencyEntryMode.PrefabOnly &&
                module.prefabDependencyEntryMode != PrefabDependencyEntryMode.PrefabAndDependencies)
            {
                //00 明确指出模块和非法值，开发者可以直接在配置资产中定位问题。
                throw new InvalidOperationException(
                    $"模块 {moduleName} 使用了不支持的 Prefab 资源加载策略：{module.prefabDependencyEntryMode}。");
            }

            //00 整目录打包规则拥有配置目录下的全部文件，并把其中每个资源直接交给 Unity 打包。
            if (module.signFolderPathArr != null)
            {
                foreach (BundleFileInfo bundleFileInfo in module.signFolderPathArr)
                {
                    //00 空配置槽按未配置处理，避免占位符进入文件系统 API。
                    if (bundleFileInfo == null || string.IsNullOrWhiteSpace(bundleFileInfo.bundlePath)) continue;
                    //00 路径先验证并转换为统一的 Assets/... 目录，归属判断不使用绝对路径。
                    string assetDirectory = ResolveAssetDirectory(moduleName, bundleFileInfo.bundlePath, "整目录打包目录");
                    //00 Bundle 名只用于诊断，不参与物理归属计算。
                    string ruleName = $"整目录打包：{bundleFileInfo.abName}";
                    //00 整个目录属于当前模块，目录中的 Entry=false 依赖同样继承这一所有权。
                    RegisterRuleDirectoryOwnership(moduleName, ruleName, assetDirectory, moduleRootDirectory, directoryOwners);
                    //00 整目录打包中的每个可打包文件都可能继续引用其他模块资源，因此都需依赖检查。
                    CollectDirectoryAssets(moduleName, ruleName, assetDirectory, true, explicitAssets, entryOwners, crcPaths);
                }
            }

            //00 子目录分包规则在构建时只处理根目录的直接子目录，但物理所有权覆盖整个配置根目录。
            if (module.rootFolderPathArr != null)
            {
                foreach (string configuredPath in module.rootFolderPathArr)
                {
                    //00 空字符串是合法的“尚未配置”状态，不调用 Directory.Exists。
                    if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                    //00 根目录用于归属判定，防止其子目录被另一个模块重复声明。
                    string rootAssetDirectory = ResolveAssetDirectory(moduleName, configuredPath, "子目录分包根目录");
                    //00 先登记根目录，使目录前缀重叠检查覆盖尚且为空的子目录。
                    RegisterRuleDirectoryOwnership(moduleName, "子目录分包根目录", rootAssetDirectory, moduleRootDirectory, directoryOwners);
                    //00 与 BuildRootSubFolder 保持一致，只把直接子目录内的文件登记为显式资源。
                    string rootFullDirectory = ToFullPath(rootAssetDirectory);
                    foreach (string childDirectory in Directory.GetDirectories(rootFullDirectory).OrderBy(path => path, StringComparer.Ordinal))
                    {
                        //00 子目录仍由根目录的唯一拥有者覆盖，不再重复登记 DirectoryOwner。
                        string childAssetDirectory = ToAssetPath(moduleName, childDirectory);
                        //00 子文件夹中的 Unity 资源都需要检查递归依赖，避免 Scene/Material 等非 Prefab 入口漏检。
                        CollectDirectoryAssets(moduleName, "子目录分包", childAssetDirectory, true, explicitAssets, entryOwners, crcPaths);
                    }
                }
            }

            //00 预制体分包目录拥有目录中的所有资源，但只有搜索到的 Prefab 是本规则的显式入口。
            if (module.prefabPathArr != null)
            {
                foreach (string configuredPath in module.prefabPathArr)
                {
                    //00 兼容旧配置中的空数组项，避免 FindAssets 意外扫描整个工程。
                    if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                    //00 物理目录所有权与 Prefab 是否被找到无关，即使空目录也不能被另一个模块嵌套声明。
                    string assetDirectory = ResolveAssetDirectory(moduleName, configuredPath, "预制体分包目录");
                    //00 登记整个目录，确保 Prefab 的 Entry=false 材质和纹理仍被判定为当前模块资源。
                    RegisterRuleDirectoryOwnership(moduleName, "预制体分包目录", assetDirectory, moduleRootDirectory, directoryOwners);
                    //00 FindAssets 返回 GUID 顺序没有稳定契约，因此转换路径后排序以获得可复现诊断。
                    IEnumerable<string> prefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { assetDirectory })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Select(NormalizeAssetPath)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.Ordinal);
                    //00 每个 Prefab 都是显式入口，同时也是需要进行递归依赖边界检查的起点。
                    foreach (string prefabPath in prefabPaths)
                    {
                        //00 记录本模块策略，使后续复用同一次递归依赖结果完成公开依赖 CRC 校验，不重复扫描 AssetDatabase。
                        bool exposeDependenciesAsEntries =
                            module.prefabDependencyEntryMode == PrefabDependencyEntryMode.PrefabAndDependencies;
                        //00 Prefab 自身始终是 Entry；只有开放依赖策略需要把合法递归依赖同步纳入 CRC 门禁。
                        RegisterExplicitAsset(
                            moduleName,
                            "Prefab 规则",
                            prefabPath,
                            true,
                            explicitAssets,
                            entryOwners,
                            crcPaths,
                            exposeDependenciesAsEntries);
                    }
                }
            }

            //00 源文件复制规则负责原样复制，不调用 Unity 依赖解析，但目录仍必须拥有唯一物理归属。
            if (module.sourceFolderPathArr != null)
            {
                foreach (string configuredPath in module.sourceFolderPathArr)
                {
                    //00 空源文件路径按未配置处理，保持与当前构建器行为一致。
                    if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                    //00 源文件目录也不能与其他规则相等或嵌套，否则同一路径会产生二义归属。
                    string assetDirectory = ResolveAssetDirectory(moduleName, configuredPath, "源文件复制目录");
                    //00 登记源文件目录的物理拥有者，供其他 Unity 资源依赖它时识别归属。
                    RegisterRuleDirectoryOwnership(moduleName, "源文件复制规则", assetDirectory, moduleRootDirectory, directoryOwners);
                    //00 false 明确表示源文件本身不触发 AssetDatabase.GetDependencies。
                    CollectDirectoryAssets(moduleName, "源文件复制规则", assetDirectory, false, explicitAssets, entryOwners, crcPaths);
                }
            }

            //00 逐文件分包规则：目录完整所有权 + 排除 .prefab，避免与 Prefab 规则重叠登记。
            if (module.singleFilePathArr != null)
            {
                foreach (string configuredPath in module.singleFilePathArr)
                {
                    //00 空配置槽按未配置处理，避免占位符进入文件系统 API。
                    if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                    string assetDirectory = ResolveAssetDirectory(moduleName, configuredPath, "逐文件分包目录");
                    //00 登记目录所有权，自动参与目录重叠检查。
                    RegisterRuleDirectoryOwnership(moduleName, "逐文件分包规则", assetDirectory, moduleRootDirectory, directoryOwners);
                    //00 目录内每个可打包文件都是显式入口；依赖检查开启，跨模块引用在构建期失败。
                    CollectDirectoryAssets(
                        moduleName,
                        "逐文件分包规则",
                        assetDirectory,
                        true,
                        explicitAssets,
                        entryOwners,
                        crcPaths,
                        extraFilter: path => !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        /// <summary>
        ///  登记一条模块物理目录配置；是否重叠在完整收集后统一判断。
        /// </summary>
        private static void RegisterDirectoryOwner(
            string moduleName,
            string ruleName,
            string assetDirectory,
            List<DirectoryOwner> directoryOwners,
            bool isModuleRoot = false)
        {
            //00 保存规范路径，后续使用带“/”边界的前缀比较，避免 Assets/GameA 误匹配 Assets/GameAB。
            directoryOwners.Add(new DirectoryOwner
            {
                moduleName = moduleName,
                ruleName = ruleName,
                assetDirectory = NormalizeAssetPath(assetDirectory).TrimEnd('/'),
                isModuleRoot = isModuleRoot
            });
        }

        /// <summary>
        ///  根据“全局模块资源总目录/模块名称”解析约定物理根目录；返回空表示旧模块尚未迁移到同名目录。
        /// </summary>
        private static string TryResolveConventionalModuleRoot(string moduleName)
        {
            //00 旧 AssetsBundleSettings 资源缺少新字段时仍使用框架默认值，不让升级顺序影响归属结果。
            string configuredBasePath = BundleSettings.Instance == null ||
                                        string.IsNullOrWhiteSpace(BundleSettings.Instance.ModuleAssetRootPath)
                ? "Assets/GameData"
                : BundleSettings.Instance.ModuleAssetRootPath;
            //00 路径统一为 Unity 的正斜杠格式，并去除尾斜杠后再拼接模块名称。
            string normalizedBasePath = NormalizeAssetPath(configuredBasePath).TrimEnd('/');
            //00 总目录必须位于 Assets 内，防止错误设置把 ProjectSettings 或工程外目录纳入资源所有权。
            if (!string.Equals(normalizedBasePath, "Assets", StringComparison.Ordinal) &&
                !normalizedBasePath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"资源模块总目录必须位于当前 Unity 工程的 Assets 目录内：{configuredBasePath}");
            }

            //00 模块名直接作为一级目录名，形成 GameOne→Assets/GameData/GameOne 的唯一、可预测映射。
            string conventionalRoot = $"{normalizedBasePath}/{moduleName}";
            //00 同名目录存在时启用严格约定模式，兄弟 Prefabs、Materials、Textures 都归属于同一模块。
            if (AssetDatabase.IsValidFolder(conventionalRoot)) return conventionalRoot;
            //00 历史工程可能尚未按同名目录整理；暂时回退到规则目录，保持已有项目可构建和可迁移。
            return null;
        }

        /// <summary>
        ///  校验收集规则必须位于约定模块根目录内；旧目录结构未迁移时由规则目录临时承担物理归属。
        /// </summary>
        private static void RegisterRuleDirectoryOwnership(
            string moduleName,
            string ruleName,
            string ruleDirectory,
            string moduleRootDirectory,
            List<DirectoryOwner> directoryOwners)
        {
            //00 空根目录代表尚未迁移的旧配置，沿用原行为可以避免升级后立即破坏全部历史模块。
            if (string.IsNullOrWhiteSpace(moduleRootDirectory))
            {
                RegisterDirectoryOwner(moduleName, ruleName, ruleDirectory, directoryOwners);
                return;
            }

            //00 规则目录可以等于模块根目录，也可以位于其任意深度子目录，但绝不能越过模块边界收集资源。
            bool isInsideModuleRoot = string.Equals(ruleDirectory, moduleRootDirectory, StringComparison.OrdinalIgnoreCase) ||
                                      ruleDirectory.StartsWith(moduleRootDirectory + "/", StringComparison.OrdinalIgnoreCase);
            if (isInsideModuleRoot)
            {
                // 模块根解决“资源物理属于谁”，规则目录解决“同一模块内是否被两条构建规则重复声明”，两者不能互相替代。
                RegisterDirectoryOwner(moduleName, ruleName, ruleDirectory, directoryOwners);
                return;
            }

            //00 越界配置会重新引入跨模块物理所有权二义，因此在扫描 AssetDatabase 前给出可操作错误。
            throw new InvalidOperationException(
                $"模块 {moduleName} 的{ruleName}超出模块资源根目录。\n" +
                $"模块根目录：{moduleRootDirectory}\n" +
                $"规则目录：{ruleDirectory}\n" +
                "请把打包规则移动到模块根目录内，或修正模块资源根目录。");
        }

        /// <summary>
        ///  按实际构建规则递归收集目录中的文件，并登记其显式 Entry 和依赖检查要求。
        /// </summary>
        private static void CollectDirectoryAssets(
            string moduleName,
            string ruleName,
            string assetDirectory,
            bool inspectUnityDependencies,
            List<ExplicitAsset> explicitAssets,
            Dictionary<string, EntryOwner> entryOwners,
            Dictionary<uint, string> crcPaths,
            Predicate<string> extraFilter = null)
        {
            //00 使用绝对路径访问磁盘，但写入配置和 CRC 的路径仍保持 Assets/... 形式。
            string fullDirectory = ToFullPath(assetDirectory);
            //00 稳定排序使首个报错与操作系统的目录枚举顺序无关。
            IEnumerable<string> filePaths = Directory.GetFiles(fullDirectory, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal);
            //00 每个非脚本、非 meta 文件都与当前 BuildBundleCompiler 的显式收集行为保持一致。
            foreach (string filePath in filePaths)
            {
                //00 先转换为 Unity 资源路径，再使用统一扩展名过滤规则。
                string assetPath = ToAssetPath(moduleName, filePath);
                //00 C# 脚本和 meta 文件不会进入 AssetBundleBuild.assetNames，因此不参与 Entry 和 CRC 登记。
                if (!IsBundleableAssetPath(assetPath)) continue;
                //00 可选过滤：逐文件分包规则排除 .prefab，其他规则不传参数则保持原行为。
                if (extraFilter != null && !extraFilter(assetPath)) continue;
                //00 登记后由统一逻辑处理重复规则、CRC 碰撞和依赖检查起点。
                RegisterExplicitAsset(moduleName, ruleName, assetPath, inspectUnityDependencies, explicitAssets, entryOwners, crcPaths);
            }
        }

        /// <summary>
        ///  登记规则直接选中的资源，并保持旧校验器的 Entry 与 CRC 安全约束。
        /// </summary>
        private static void RegisterExplicitAsset(
            string moduleName,
            string ruleName,
            string path,
            bool inspectUnityDependencies,
            List<ExplicitAsset> explicitAssets,
            Dictionary<string, EntryOwner> entryOwners,
            Dictionary<uint, string> crcPaths,
            bool exposeUnityDependenciesAsEntries = false)
        {
            //00 所有字典键统一为正斜杠的 Assets/... 路径，防止相同文件因分隔符不同重复登记。
            string assetPath = NormalizeAssetPath(path);
            //00 同一资源被两条规则选中会使 Bundle 分配依赖遍历顺序，因此同模块重复也必须拒绝。
            if (entryOwners.TryGetValue(assetPath, out EntryOwner existingOwner))
            {
                throw new InvalidOperationException(
                    $"资源 Entry 重复归属：{assetPath}\n" +
                    $"已有归属：模块 {existingOwner.moduleName}，规则 {existingOwner.ruleName}\n" +
                    $"冲突归属：模块 {moduleName}，规则 {ruleName}");
            }

            //00 规则直接选中的资源一定是公开 Entry，统一复用 CRC 登记逻辑拒绝不同路径真碰撞。
            RegisterLoadableEntryCrc(assetPath, crcPaths);

            //00 保存显式归属，供后续模块继续检测同一路径是否被重复配置。
            entryOwners.Add(assetPath, new EntryOwner { moduleName = moduleName, ruleName = ruleName });
            //00 源文件也进入显式资源列表，但 inspectUnityDependencies=false 会在依赖阶段被跳过。
            explicitAssets.Add(new ExplicitAsset
            {
                moduleName = moduleName,
                ruleName = ruleName,
                assetPath = assetPath,
                inspectUnityDependencies = inspectUnityDependencies,
                //00 该标记只影响公开索引安全校验，不改变资源的物理 Bundle 分组。
                exposeUnityDependenciesAsEntries = exposeUnityDependenciesAsEntries
            });
        }

        /// <summary>
        ///  把一个最终可主动加载的资源路径登记到全局 CRC 索引门禁。
        /// </summary>
        private static void RegisterLoadableEntryCrc(string assetPath, Dictionary<uint, string> crcPaths)
        {
            //00 运行时仍以完整资源路径 CRC32 为唯一键，因此 Editor 必须在产物生成前保证一对一映射。
            uint crc = Crc32.GetCrc32(assetPath);
            //00 同一路径可能既是文件夹 Entry 又被 Prefab 开放策略引用，重复登记本身合法且无需报错。
            if (crcPaths.TryGetValue(crc, out string existingPath) &&
                !string.Equals(existingPath, assetPath, StringComparison.Ordinal))
            {
                //00 不同路径映射到同一 CRC 会令运行时无法区分资源，必须要求开发者调整其中一个路径。
                throw new InvalidOperationException(
                    $"资源路径发生 CRC32 真碰撞：CRC={crc}，路径 {existingPath} 与 {assetPath}。请调整资源路径。");
            }

            //00 保存或覆盖同一路径映射，后续模块和二期公开依赖共用这一份全局碰撞字典。
            crcPaths[crc] = assetPath;
        }

        /// <summary>
        ///  拒绝完全相同或任意方向嵌套的配置目录，确保每个物理文件最多只有一个拥有者。
        /// </summary>
        private static void ValidateDirectoryOwnership(List<DirectoryOwner> directoryOwners)
        {
            //00 排序不改变语义，只让冲突诊断在不同机器上保持稳定。
            List<DirectoryOwner> orderedOwners = directoryOwners
                .OrderBy(owner => owner.assetDirectory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(owner => owner.moduleName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            //00 两两比较可以同时发现相等目录和父子目录；模块配置规模很小，O(M²) 成本可忽略。
            for (int leftIndex = 0; leftIndex < orderedOwners.Count; leftIndex++)
            {
                //00 右侧从 leftIndex+1 开始，避免自己与自己比较或重复报告同一对冲突。
                for (int rightIndex = leftIndex + 1; rightIndex < orderedOwners.Count; rightIndex++)
                {
                    //00 读取当前目录对，后续诊断同时展示模块、规则和路径。
                    DirectoryOwner left = orderedOwners[leftIndex];
                    DirectoryOwner right = orderedOwners[rightIndex];
                    //00 只有相等或带目录边界的前缀关系才算重叠，名称相似但不嵌套不应误报。
                    if (!DirectoriesOverlap(left.assetDirectory, right.assetDirectory)) continue;
                    // 同一模块的约定根目录必然包含它自己的规则目录，这是物理归属与构建规则的正常双重描述。
                    // 只有“根目录 ↔ 本模块规则”可以豁免；两条规则之间或不同模块之间仍必须阻止。
                    bool isSameModuleRootAndRulePair =
                        string.Equals(left.moduleName, right.moduleName, StringComparison.OrdinalIgnoreCase) &&
                        left.isModuleRoot != right.isModuleRoot;
                    if (isSameModuleRootAndRulePair) continue;
                    //00 目录二义会使 Entry=false 依赖无法确定所有者，因此在任何依赖扫描前立即终止。
                    throw new InvalidOperationException(
                        "资源模块配置目录发生相等或嵌套重叠，无法确定唯一物理归属。\n" +
                        $"目录一：模块 {left.moduleName}，规则 {left.ruleName}，路径 {left.assetDirectory}\n" +
                        $"目录二：模块 {right.moduleName}，规则 {right.ruleName}，路径 {right.assetDirectory}\n" +
                        "请调整配置，使每个资源目录只被一条规则声明且不存在父子包含关系。");
                }
            }
        }

        /// <summary>
        ///  使用目录边界判断相等或嵌套关系，避免 GameA 与 GameAB 之间产生错误前缀命中。
        /// </summary>
        private static bool DirectoriesOverlap(string leftDirectory, string rightDirectory)
        {
            //00 完全相同的目录无论属于相同模块还是不同模块都存在配置二义。
            if (string.Equals(leftDirectory, rightDirectory, StringComparison.OrdinalIgnoreCase)) return true;
            //00 左目录是右目录父级时，右路径必须紧跟“/”才能视为真正的子目录。
            if (rightDirectory.StartsWith(leftDirectory + "/", StringComparison.OrdinalIgnoreCase)) return true;
            //00 对称检查右目录是否为左目录父级。
            return leftDirectory.StartsWith(rightDirectory + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///  检查所有 AssetBundle 显式资源的递归依赖是否仍位于本模块拥有目录中。
        /// </summary>
        private static void ValidateDependencyOwnership(
            List<ExplicitAsset> explicitAssets,
            List<DirectoryOwner> directoryOwners,
            Dictionary<uint, string> crcPaths,
            IReadOnlyDictionary<string, BundleModuleRole> moduleRoles,
            IReadOnlyCollection<string> targetModuleNames,
            ModuleOwnershipAnalysis analysis)
        {
            //00 违规列表集中输出，方便开发者一次整改多个资源，而不必反复启动构建。
            List<DependencyViolation> violations = new List<DependencyViolation>();
            //00 直接依赖缓存只在违规链重建时使用，避免同一中间资源被 AssetDatabase 重复查询。
            Dictionary<string, IReadOnlyList<string>> directDependencyCache =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            //00 待处理集合从用户选中的模块开始；发现合法 Business→Shared 时才把对应 Shared 加入本次校验闭包。
            HashSet<string> pendingModuleNames = new HashSet<string>(targetModuleNames, StringComparer.OrdinalIgnoreCase);
            //00 已处理集合保证同一 Shared 被多个业务模块依赖时只扫描一次。
            HashSet<string> processedModuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            //00 每轮取稳定排序后的模块名，保证多选构建在不同机器上输出一致的诊断顺序。
            while (pendingModuleNames.Count > 0 && violations.Count < MaxReportedViolationCount)
            {
                string currentModuleName = pendingModuleNames.OrderBy(name => name, StringComparer.Ordinal).First();
                pendingModuleNames.Remove(currentModuleName);
                if (!processedModuleNames.Add(currentModuleName)) continue;

                //00 只枚举当前闭包模块的入口；GameOne 构建不会再扫描或报告未选中的 GameTow。
                IEnumerable<ExplicitAsset> currentModuleAssets = explicitAssets
                    .Where(asset => asset.inspectUnityDependencies &&
                                    string.Equals(asset.moduleName, currentModuleName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(asset => asset.assetPath, StringComparer.Ordinal);
                foreach (ExplicitAsset sourceAsset in currentModuleAssets)
                {
                    //00 recursive=true 只调用一次取得扁平依赖集，正常资源不承担逐层 BFS 的额外开销。
                    IEnumerable<string> dependencies = AssetDatabase.GetDependencies(sourceAsset.assetPath, true)
                        .Select(NormalizeAssetPath)
                        .Where(IsBundleableAssetPath)
                        .Where(path => !string.Equals(path, sourceAsset.assetPath, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.Ordinal);

                    //00 O(1) 前缀归属判断先筛出真正违规的目标，只有命中后才还原完整依赖链。
                    foreach (string dependencyPath in dependencies)
                    {
                        //00 目录已通过非重叠校验，因此一次查询最多只会得到一个物理拥有者。
                        DirectoryOwner dependencyOwner = FindPhysicalOwner(dependencyPath, directoryOwners);
                    //00 无主资源始终非法；跨模块资源还需要经过第三期角色矩阵判定。
                    bool isUnowned = dependencyOwner == null;
                    //00 即使模块名称只存在大小写差异，前置模块名检查也已拒绝，因此这里可忽略大小写比较。
                    bool isCrossModule = dependencyOwner != null &&
                                         !string.Equals(dependencyOwner.moduleName, sourceAsset.moduleName, StringComparison.OrdinalIgnoreCase);
                    //00 当前模块目录内的依赖拥有合法物理归属，可以继续执行二期公开索引校验。
                    if (!isUnowned && !isCrossModule)
                    {
                        //00 只在当前 Prefab 选择开放依赖时登记 CRC；PrefabOnly 的 DependencyOnly 资源不进入运行时索引。
                        if (sourceAsset.exposeUnityDependenciesAsEntries)
                            RegisterLoadableEntryCrc(dependencyPath, crcPaths);
                        //00 合法依赖无需构造昂贵的 BFS 诊断链，直接处理下一个依赖。
                        continue;
                    }
                    //00 Business→Shared 是第三期唯一允许的跨模块方向；归属仍完全由物理目录决定。
                    bool isAllowedSharedDependency = false;
                    if (isCrossModule &&
                        moduleRoles.TryGetValue(sourceAsset.moduleName, out BundleModuleRole sourceRole) &&
                        moduleRoles.TryGetValue(dependencyOwner.moduleName, out BundleModuleRole dependencyRole))
                    {
                        //00 只有普通业务模块消费共享模块时成立，Shared 反向引用和业务互引都保持禁止。
                        isAllowedSharedDependency = sourceRole == BundleModuleRole.Business &&
                                                    dependencyRole == BundleModuleRole.Shared;
                    }
                    //00 合法跨模块依赖只登记模块关系；Shared 资源不能被错误复制进 Business 的公开 Entry 索引。
                    if (isAllowedSharedDependency)
                    {
                        analysis.AddDependency(sourceAsset.moduleName, dependencyOwner.moduleName, dependencyPath);
                        //00 Shared 成为实际构建依赖后加入待处理集合，使其自身非法反向依赖仍会在本次构建前被阻止。
                        if (!processedModuleNames.Contains(dependencyOwner.moduleName))
                            pendingModuleNames.Add(dependencyOwner.moduleName);
                        continue;
                    }
                    //00 只对已经确认违规的目标运行非递归 BFS，满足大工程下的构建性能约束。
                    IReadOnlyList<string> dependencyChain =
                        FindDependencyChain(sourceAsset.assetPath, dependencyPath, directDependencyCache);
                    //00 保存完整诊断；超过展示上限后仍可提前结束，避免历史工程产生不可控扫描成本。
                    violations.Add(new DependencyViolation
                    {
                        sourceAsset = sourceAsset,
                        dependencyPath = dependencyPath,
                        dependencyOwner = dependencyOwner,
                        dependencyChain = dependencyChain
                    });
                    //00 达到上限后停止继续重建依赖链，最终错误会明确提示仅展示前若干项。
                        if (violations.Count >= MaxReportedViolationCount) break;
                    }

                    //00 当前入口达到报告上限后立即停止扫描同模块剩余入口。
                    if (violations.Count >= MaxReportedViolationCount) break;
                }
            }

            //00 没有违规时正常返回，且整个过程从未调用非递归 BFS 链路重建。
            if (violations.Count == 0) return;
            //00 将所有已收集违规格式化成一次可操作的构建错误。
            throw new InvalidOperationException(BuildViolationMessage(violations));
        }

        /// <summary>
        ///  根据资源的物理 Assets 路径查找唯一目录拥有者，与 Entry 状态完全无关。
        /// </summary>
        private static DirectoryOwner FindPhysicalOwner(string assetPath, List<DirectoryOwner> directoryOwners)
        {
            //00 路径必须等于目录或位于“目录/”之下，避免 Assets/GameA 错误拥有 Assets/GameAB。
            return directoryOwners.FirstOrDefault(owner =>
                string.Equals(assetPath, owner.assetDirectory, StringComparison.OrdinalIgnoreCase) ||
                assetPath.StartsWith(owner.assetDirectory + "/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        ///  仅在命中违规后，通过 direct dependencies BFS 还原入口到违规资源的一条最短依赖链。
        /// </summary>
        private static IReadOnlyList<string> FindDependencyChain(
            string sourcePath,
            string targetPath,
            Dictionary<string, IReadOnlyList<string>> directDependencyCache)
        {
            //00 BFS 队列按层级搜索，第一次命中目标时得到边数最少、最容易理解的依赖链。
            Queue<string> pendingPaths = new Queue<string>();
            //00 parentByPath 同时承担 visited 集合职责，并保存命中后反向还原链路所需的父节点。
            Dictionary<string, string> parentByPath =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            //00 根节点没有父节点，以 null 作为回溯终点。
            parentByPath[sourcePath] = null;
            //00 从产生违规的显式资源开始逐层读取直接依赖。
            pendingPaths.Enqueue(sourcePath);

            //00 队列为空表示图中没有找到目标；极端导入器行为会走后面的安全兜底链。
            while (pendingPaths.Count > 0)
            {
                //00 取出当前层最早入队的资源，维持标准 BFS 顺序。
                string currentPath = pendingPaths.Dequeue();
                //00 同一资源的直接依赖可能被多个违规目标复用，因此通过缓存减少 AssetDatabase 调用。
                IReadOnlyList<string> directDependencies = GetDirectDependencies(currentPath, directDependencyCache);
                //00 稳定排序后的直接依赖保证相同图存在多条最短链时始终选择同一条。
                foreach (string dependencyPath in directDependencies)
                {
                    //00 已访问节点不重复入队，既避免环路，也控制大型依赖图的内存和调用次数。
                    if (parentByPath.ContainsKey(dependencyPath)) continue;
                    //00 保存首次到达该节点的父节点，即 BFS 最短路径上的前驱。
                    parentByPath.Add(dependencyPath, currentPath);
                    //00 命中违规目标后立即回溯，不继续探索无关分支。
                    if (string.Equals(dependencyPath, targetPath, StringComparison.OrdinalIgnoreCase))
                        return RebuildDependencyChain(targetPath, parentByPath);
                    //00 未命中目标的资源继续作为下一层依赖搜索起点。
                    pendingPaths.Enqueue(dependencyPath);
                }
            }

            //00 Unity 导入器若只在递归结果中暴露目标，仍至少返回入口与目标，避免诊断丢失关键信息。
            return new[] { sourcePath, targetPath };
        }

        /// <summary>
        ///  读取并缓存一个资源的直接可打包依赖。
        /// </summary>
        private static IReadOnlyList<string> GetDirectDependencies(
            string assetPath,
            Dictionary<string, IReadOnlyList<string>> directDependencyCache)
        {
            //00 命中缓存时不再访问 AssetDatabase，多个违规链共享中间节点时收益明显。
            if (directDependencyCache.TryGetValue(assetPath, out IReadOnlyList<string> cachedDependencies))
                return cachedDependencies;
            //00 recursive=false 只取一层依赖，是重建真实 Prefab→Material→Texture 链的基础。
            IReadOnlyList<string> dependencies = AssetDatabase.GetDependencies(assetPath, false)
                .Select(NormalizeAssetPath)
                .Where(IsBundleableAssetPath)
                .Where(path => !string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            //00 保存不可变数组视图，后续 BFS 只读复用，不允许调用方修改缓存内容。
            directDependencyCache.Add(assetPath, dependencies);
            //00 返回刚写入的稳定依赖快照。
            return dependencies;
        }

        /// <summary>
        ///  使用父节点字典从违规目标反向回溯并得到正向依赖链。
        /// </summary>
        private static IReadOnlyList<string> RebuildDependencyChain(
            string targetPath,
            Dictionary<string, string> parentByPath)
        {
            //00 先按目标到入口的方向收集节点，最后统一反转得到开发者阅读顺序。
            List<string> reversedChain = new List<string>();
            //00 当前节点从违规目标开始，沿 parentByPath 逐级回溯。
            string currentPath = targetPath;
            //00 根节点父级为 null，因此循环会在完整加入入口以后自然结束。
            while (currentPath != null)
            {
                //00 保存当前链路节点，用完整路径避免同名资源造成诊断歧义。
                reversedChain.Add(currentPath);
                //00 字典缺失属于防御性异常情况，此时停止回溯并保留已经获取的部分链路。
                if (!parentByPath.TryGetValue(currentPath, out currentPath)) break;
            }

            //00 将目标→入口反转成入口→目标，和资源引用方向保持一致。
            reversedChain.Reverse();
            //00 返回只读语义的列表供错误格式化使用。
            return reversedChain;
        }

        /// <summary>
        ///  把多条违规组织成包含归属、规则、依赖链和修复建议的构建错误。
        /// </summary>
        private static string BuildViolationMessage(List<DependencyViolation> violations)
        {
            //00 StringBuilder 避免大量字符串拼接产生中间分配，错误较多时仍保持可控。
            StringBuilder messageBuilder = new StringBuilder();
            //00 标题明确这是发布前硬校验，而不是可以忽略的普通警告。
            messageBuilder.AppendLine("资源模块唯一归属校验失败：发现非法跨模块依赖或无主依赖，已阻止构建。");
            //00 明确第三期角色矩阵，避免合法 Business→Shared 与非法方向混淆。
            messageBuilder.AppendLine("仅允许 Business → Shared；Business → Business、Shared → Business 和无主依赖均禁止。");

            //00 逐条输出稳定编号，方便开发者在多人协作时引用具体问题。
            for (int index = 0; index < violations.Count; index++)
            {
                //00 当前违规包含入口、目标和已经惰性还原的依赖链。
                DependencyViolation violation = violations[index];
                //00 空拥有者表示依赖没有落入任何模块配置目录。
                bool isUnowned = violation.dependencyOwner == null;
                //00 每条问题前空一行，保证 Unity Console 展开后仍然易读。
                messageBuilder.AppendLine();
                //00 问题类型直接区分跨模块和无主资源，便于选择移动或复制策略。
                messageBuilder.AppendLine($"[{index + 1}] 问题类型：{(isUnowned ? "无主依赖" : "跨模块依赖")}");
                //00 当前模块是准备使用该依赖的资源模块。
                messageBuilder.AppendLine($"当前模块：{violation.sourceAsset.moduleName}");
                //00 发起资源及规则帮助开发者定位哪条配置把引用带入构建。
                messageBuilder.AppendLine(
                    $"发起资源：{violation.sourceAsset.assetPath}（规则：{violation.sourceAsset.ruleName}）");
                //00 违规资源使用完整 Assets 路径，支持直接在 Project 窗口中搜索。
                messageBuilder.AppendLine($"违规资源：{violation.dependencyPath}");
                //00 有主资源展示其真实物理拥有者；无主资源明确说明不在任何模块目录。
                messageBuilder.AppendLine(isUnowned
                    ? "资源归属：无（未位于任何模块配置目录）"
                    : $"资源归属：模块 {violation.dependencyOwner.moduleName}，规则 {violation.dependencyOwner.ruleName}");
                //00 箭头链展示真实引用路径，而非 AssetDatabase.GetDependencies 返回的扁平集合。
                messageBuilder.AppendLine($"依赖链：{string.Join(" → ", violation.dependencyChain)}");
                //00 第一阶段可选修复只有移入本模块或复制完整依赖闭包，Shared 要等第三阶段完成。
                messageBuilder.AppendLine(isUnowned
                    ? "处理建议：把资源及其依赖闭包移入当前模块目录，或为当前模块复制独立资源并重新绑定 GUID 引用。"
                    : "处理建议：禁止业务模块互相引用；请把完整依赖闭包移入当前模块，或复制独立资源并重新绑定 GUID 引用。");
            }

            //00 达到上限时说明输出被截断，避免开发者误以为工程只有这些问题。
            if (violations.Count >= MaxReportedViolationCount)
            {
                messageBuilder.AppendLine();
                messageBuilder.AppendLine($"仅展示前 {MaxReportedViolationCount} 条违规；请修复后重新构建以继续审计。");
            }

            //00 去除末尾多余换行，使 InvalidOperationException 在 Console 中显示更整洁。
            return messageBuilder.ToString().TrimEnd();
        }

        /// <summary>
        ///  验证配置目录存在且位于当前 Unity 工程 Assets 下，并返回规范资源目录路径。
        /// </summary>
        private static string ResolveAssetDirectory(string moduleName, string configuredPath, string ruleName)
        {
            //00 统一分隔符和末尾斜杠，避免同一目录因书写差异逃过重叠校验。
            string normalizedPath = configuredPath.Trim().Replace('\\', '/').TrimEnd('/');
            //00 非空但不存在的配置属于用户可修复错误，诊断必须带模块、规则和原路径。
            if (!Directory.Exists(normalizedPath))
                throw new DirectoryNotFoundException($"模块 {moduleName} 的{ruleName}不存在：{normalizedPath}");
            //00 转换为工程相对路径后再验证 Assets 边界，ProjectSettings/Packages 不能冒充模块资源目录。
            string assetDirectory = ToAssetPath(moduleName, normalizedPath).TrimEnd('/');
            //00 使用区分大小写的规范前缀，Windows 上误写 assets/... 也不能静默生成空 Bundle。
            if (!string.Equals(assetDirectory, "Assets", StringComparison.Ordinal) &&
                !assetDirectory.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"模块 {moduleName} 的{ruleName}必须位于当前 Unity 工程的 Assets 目录内：{configuredPath}");
            }

            //00 AssetDatabase.IsValidFolder 再确认目录是 Unity 已导入的真实资源目录，而非普通磁盘目录。
            if (!AssetDatabase.IsValidFolder(assetDirectory))
            {
                throw new InvalidOperationException(
                    $"模块 {moduleName} 的{ruleName}不是有效的 Unity Assets 目录：{assetDirectory}");
            }

            //00 返回已通过物理存在、工程边界和 AssetDatabase 三重验证的规范目录。
            return assetDirectory;
        }

        /// <summary>
        ///  将磁盘路径转换为当前 Unity 工程内的 Assets/... 路径。
        /// </summary>
        private static string ToAssetPath(string moduleName, string path)
        {
            //00 Path.GetFullPath 负责解析相对路径、点号路径和平台分隔符。
            string fullPath = Path.GetFullPath(path).Replace('\\', '/');
            //00 Unity 工程根目录是 Application.dataPath 的上一级。
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
            //00 必须使用目录边界前缀，防止名称相似的工程外目录被错误接受。
            if (!fullPath.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"模块 {moduleName} 的资源不在当前 Unity 工程内：{path}");
            //00 去除工程根前缀后得到稳定的 Assets/... 资源路径。
            return NormalizeAssetPath(fullPath.Substring(projectRoot.Length + 1));
        }

        /// <summary>
        ///  将 Assets/... 资源路径转换为可供 System.IO 使用的绝对路径。
        /// </summary>
        private static string ToFullPath(string assetPath)
        {
            //00 从 Application.dataPath 上一级取得工程根，再拼接已规范化的资源路径。
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            //00 Path.GetFullPath 消除中间目录符号，返回稳定可访问的磁盘路径。
            return Path.GetFullPath(Path.Combine(projectRoot, NormalizeAssetPath(assetPath)));
        }

        /// <summary>
        ///  判断 AssetDatabase 返回的路径是否属于项目内可打进 AssetBundle 的资源。
        /// </summary>
        private static bool IsBundleableAssetPath(string path)
        {
            //00 Packages、内置资源、脚本和 meta 不会作为框架 Bundle 资源登记，因此不参与归属校验。
            return !string.IsNullOrWhiteSpace(path) &&
                   path.StartsWith("Assets/", StringComparison.Ordinal) &&
                   !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                   !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///  统一资源路径分隔符并移除无意义的前导斜杠。
        /// </summary>
        private static string NormalizeAssetPath(string path)
        {
            //00 空路径保持为空，调用方可以通过 IsBundleableAssetPath 安全过滤。
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('\\', '/').TrimStart('/');
        }
    }
}
