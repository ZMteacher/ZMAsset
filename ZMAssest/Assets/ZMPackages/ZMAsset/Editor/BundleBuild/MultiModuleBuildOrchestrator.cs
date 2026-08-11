using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// 00 统一编排单模块、多独立模块以及 Business→Shared 构建，所有产物都先进入临时目录再原子发布。
    /// 00 Shared 只负责扩展依赖闭包；未配置或未依赖 Shared 的工程仍可使用同一事务入口正常构建。
    /// </summary>
    internal static class MultiModuleBuildOrchestrator
    {
        //00 临时目录固定放在工程输出根目录下，既保证与正式目录同盘可原子移动，也方便异常后识别和清理。
        private const string StagingDirectoryName = ".zmasset-staging";

        /// <summary>
        /// 00 构建选中模块；依赖 Shared 即使未勾选也会自动加入构建闭包。
        /// </summary>
        internal static IEnumerator BuildStaged(
            IReadOnlyList<BundleModuleData> selectedModules,
            BuildType buildType = BuildType.AssetBundle,
            int hotPatchVersion = 0,
            string hotAppVersion = "0.0.0",
            string updateNotice = "",
            UnityEditor.BuildTarget buildTarget = UnityEditor.BuildTarget.NoTarget,
            bool allowSevereSharedChanges = false)
        {
            //00 立即冻结并校验调用方选择，避免 staged 枚举期间 UI 勾选变化影响本次事务。
            List<BundleModuleData> selectedSnapshot = NormalizeSelectedModules(selectedModules);
            //00 使用显式平台或当前编辑器平台的单次快照，整个构建过程中不再动态读取。
            UnityEditor.BuildTarget resolvedBuildTarget = buildTarget == UnityEditor.BuildTarget.NoTarget
                ? EditorUserBuildSettings.activeBuildTarget
                : buildTarget;

            // 在资源归属扫描、配置改写和 staging 创建前完成映射与 Build Support 校验。
            // 缺少目标平台模块时直接失败，避免构建过程中留下半成品或修改配置文件。
            BuildTargetPlatformMapper.EnsureEditorBuildTargetSupported(resolvedBuildTarget);
            BundleSettings bundleSettings = BundleSettings.Instance;
            BuildTargetPlatformMapper.EnsureWebGLBuildConfiguration(
                resolvedBuildTarget,
                bundleSettings?.buildbundleOptions ?? BuildAssetBundleOptions.None,
                bundleSettings?.bundleEncrypt?.isEncrypt == true,
                bundleSettings?.AssetBundleDownLoadUrl);

            //00 归属、目录重叠、角色矩阵和 CRC 必须在创建 staging 或改写配置前全部通过。
            ZMBuildProgress.Report("校验模块边界", "扫描物理归属与 Business→Shared 依赖", .02f);
            yield return null;
            ModuleOwnershipAnalysis analysis = AssetEntryOwnershipValidator.AnalyzeBeforeBuild(selectedSnapshot);

            //00 所有构建统一进入事务路径；无 Shared 依赖时闭包就是所选模块本身，不增加额外配置要求。
            //00 存在 Business→Shared 依赖时则自动补充未勾选的 Shared，并保持依赖优先的稳定顺序。
            List<BundleModuleData> buildClosure = ResolveBuildClosure(selectedSnapshot, analysis);
            //00 进度窗口的 Job 只有一个统一事务，因此此处显式登记真实模块范围，完成文案才不会误报为 1 个模块。
            ZMBuildProgress.SetModuleScope(buildClosure.Count);
            //00 日志把自动加入行为说清楚，避免用户误以为 isBuild 勾选被擅自修改。
            string autoIncludedModules = string.Join(", ", buildClosure
                .Where(module => selectedSnapshot.All(selected =>
                    !string.Equals(selected.moduleName, module.moduleName, StringComparison.OrdinalIgnoreCase)))
                .Select(module => module.moduleName));
            if (!string.IsNullOrWhiteSpace(autoIncludedModules))
                Debug.Log($"统一构建自动包含依赖模块：{autoIncludedModules}。配置中的 isBuild 勾选状态未被修改。");

            //00 每次事务使用不可预测的独立目录，失败构建不会覆盖另一轮并行/历史 staging。
            string transactionId = Guid.NewGuid().ToString("N");
            //00 staging 放在 AssetBundle 根下以保证普通输出目录的 Directory.Move 保持同一卷原子语义。
            string assetBundleRoot = GetProjectOutputRoot("AssetBundle");
            string stagingRoot = Path.Combine(assetBundleRoot, StagingDirectoryName, transactionId);
            //00 Unity 只向 raw 目录写一次全部模块原始 Bundle。
            string rawOutputPath = Path.Combine(stagingRoot, "raw");
            //00 modules 目录保存已提取和加密、但尚未发布的逐模块结果。
            string moduleStagingRoot = Path.Combine(stagingRoot, "modules");
            //00 hot 目录保存业务热更补丁副本和备份清单。
            string hotStagingRoot = Path.Combine(stagingRoot, "hot");
            //00 master-manifests 保存待替换的模块主清单文件。
            string manifestStagingRoot = Path.Combine(stagingRoot, "master-manifests");

            //00 配置 JSON 在 Unity 构建前必须写入 Assets，因此为失败回滚保存内存快照。
            List<ConfigFileSnapshot> configSnapshots = buildClosure
                .Select(ConfigFileSnapshot.Capture)
                .ToList();
            //00 发布完成前始终视为失败；finally 会恢复配置并清理 staging。
            bool buildSucceeded = false;

            try
            {
                //00 staging 的所有父目录由当前事务独占，创建前确认没有 GUID 冲突残留。
                Directory.CreateDirectory(rawOutputPath);
                Directory.CreateDirectory(moduleStagingRoot);
                //00 每个模块拥有独立编译器与上下文，彻底隔离收集列表和输出参数。
                Dictionary<string, BuildBundleCompiler> compilers =
                    new Dictionary<string, BuildBundleCompiler>(StringComparer.OrdinalIgnoreCase);

                for (int moduleIndex = 0; moduleIndex < buildClosure.Count; moduleIndex++)
                {
                    BundleModuleData module = buildClosure[moduleIndex];
                    //00 分阶段报告模块收集进度，Shared 通常位于闭包首位。
                    ZMBuildProgress.Report(
                        "收集统一构建输入",
                        module.moduleName,
                        .08f + .20f * moduleIndex / Mathf.Max(1, buildClosure.Count));
                    yield return null;
                    //00 一个上下文只服务当前模块，所有权分析使用本事务共享的不可变快照。
                    BuildBundleCompiler compiler = new BuildBundleCompiler(new ModuleBuildContext());
                    compiler.PrepareForOrchestration(
                        module,
                        analysis,
                        buildType,
                        hotPatchVersion,
                        hotAppVersion,
                        updateNotice,
                        resolvedBuildTarget,
                        rawOutputPath);
                    compilers.Add(module.moduleName, compiler);
                }

                //00 合并全部模块资源的唯一物理位置，配置写入和重复输入门禁都消费这一个映射。
                Dictionary<string, AssetBundleBuildLocation> globalAssetLocations =
                    MergeAssetLocations(buildClosure, compilers);
                //00 Bundle 名在文件系统层必须全局唯一，否则统一 BuildPipeline 会覆盖其他模块文件。
                List<AssetBundleBuild> unifiedBuilds = MergeBundleBuilds(
                    buildClosure,
                    compilers,
                    out Dictionary<string, string> unifiedBundleOwners);
                //00 在调用 Unity 前再次验证每个显式输入仍属于声明模块，防止收集期间配置被外部修改。
                ValidateCollectedOwnership(buildClosure, compilers, analysis);

                //00 先写入所有模块协议 v2 配置，再统一 Refresh，确保 BuildPipeline 读取同一批快照。
                for (int moduleIndex = 0; moduleIndex < buildClosure.Count; moduleIndex++)
                {
                    BundleModuleData module = buildClosure[moduleIndex];
                    ZMBuildProgress.Report(
                        "写入模块配置",
                        module.moduleName,
                        .30f + .08f * moduleIndex / Mathf.Max(1, buildClosure.Count));
                    //00 moduleDependencies 只保存直接依赖；当前矩阵下即 Business→Shared。
                    compilers[module.moduleName].WriteConfigForOrchestration(
                        globalAssetLocations,
                        analysis.GetDirectDependencies(module.moduleName));
                }
                //00 只分析开发者显式选择的 Shared；报告只提示风险，不会自动增选或构建任何业务模块。
                if (buildType == BuildType.HotPatch)
                {
                    IEnumerator confirmationRoutine = ConfirmSelectedSharedPatchChanges(
                        selectedSnapshot,
                        configSnapshots,
                        analysis,
                        allowSevereSharedChanges);
                    //00 无边框 Popup 通过逐帧让步等待选择，Unity 主线程仍可绘制窗口和处理按钮事件。
                    while (confirmationRoutine.MoveNext()) yield return confirmationRoutine.Current;
                }
                //00 JSON 已由各编译器写入 Assets，统一刷新一次让 Unity 导入最终文本。
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                yield return null;

                //00 Unity 的调用是不可取消同步区间，进度面板提前明确说明。
                ZMBuildProgress.Report(
                    "Unity 统一构建中",
                    $"{buildClosure.Count} 个模块 · {unifiedBuilds.Count} 个 Bundle",
                    .42f,
                    false);
                //00 沿用旧构建的 ChunkBasedCompression，第三期不改变压缩协议。
                AssetBundleManifest manifest;
                using (ShaderVariantAuditBuildScope shaderAuditScope = ShaderVariantAuditBuildCoordinator.Begin(
                           $"{buildType}_Unified",
                           buildClosure.Select(module => module.moduleName),
                           resolvedBuildTarget,
                           unifiedBuilds,
                           unifiedBundleOwners))
                {
                    manifest = BuildPipeline.BuildAssetBundles(
                        rawOutputPath,
                        unifiedBuilds.ToArray(),
                        UnityEditor.BuildAssetBundleOptions.ChunkBasedCompression,
                        resolvedBuildTarget);
                    shaderAuditScope.MarkBuildSucceeded(manifest != null);
                }
                //00 Unity 返回 null 表示构建失败，不能继续提取或覆盖任何正式目录。
                if (manifest == null)
                    throw new InvalidOperationException("Unity 统一 BuildPipeline 构建 AssetBundle 失败。");
                yield return null;

                //00 从同一个 raw 输出精确提取每个模块文件，并在各自 staging 内完成最终加密。
                Dictionary<string, string> stagedModulePaths =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int moduleIndex = 0; moduleIndex < buildClosure.Count; moduleIndex++)
                {
                    BundleModuleData module = buildClosure[moduleIndex];
                    ZMBuildProgress.Report(
                        "提取模块产物",
                        module.moduleName,
                        .64f + .12f * moduleIndex / Mathf.Max(1, buildClosure.Count));
                    yield return null;
                    //00 模块 staging 名使用原模块名，但其父目录只属于当前 GUID 事务。
                    string moduleStagingPath = Path.Combine(moduleStagingRoot, module.moduleName);
                    compilers[module.moduleName].MaterializeOrchestratedOutput(rawOutputPath, moduleStagingPath);
                    stagedModulePaths.Add(module.moduleName, moduleStagingPath);
                }

                //00 所有模块都成功后才准备正式发布项；中途失败不会触碰旧产物。
                List<AtomicPublishItem> publishItems = new List<AtomicPublishItem>();
                if (buildType == BuildType.AssetBundle)
                {
                    PrepareFullBuildPublishItems(
                        buildClosure,
                        compilers,
                        stagedModulePaths,
                        resolvedBuildTarget,
                        publishItems);
                }
                else
                {
                    PrepareHotPatchPublishItems(
                        selectedSnapshot,
                        buildClosure,
                        compilers,
                        stagedModulePaths,
                        resolvedBuildTarget,
                        hotPatchVersion,
                        hotAppVersion,
                        hotStagingRoot,
                        manifestStagingRoot,
                        publishItems);
                }

                //00 原子切换是唯一会改变正式输出的阶段；发布器会在任一失败时恢复全部备份。
                ZMBuildProgress.Report("原子发布", $"切换 {publishItems.Count} 个输出目标", .92f, false);
                AtomicPublisher.Publish(publishItems, transactionId);
                //00 所有模块输出已在同一原子事务中成功切换后，才确认整条闭包真正完成，失败路径不会产生假完成计数。
                ZMBuildProgress.CompletePublishedModules(buildClosure.Count);
                //00 成功发布后保留新配置 JSON，并让 AssetDatabase 同步磁盘最终状态。
                buildSucceeded = true;
                AssetDatabase.Refresh();
                ZMBuildProgress.Report("统一构建完成", string.Join(", ", selectedSnapshot.Select(m => m.moduleName)), 1f);
            }
            finally
            {
                if (!buildSucceeded)
                {
                    //00 构建或发布失败时恢复构建前配置，避免磁盘产物与 JSON 协议不一致。
                    foreach (ConfigFileSnapshot snapshot in configSnapshots) snapshot.Restore();
                    //00 恢复后立即刷新，Unity Inspector 和后续构建都重新看到旧配置。
                    AssetDatabase.Refresh();
                }
                //00 只清理当前 GUID 对应的精确 staging；历史正式目录和其他事务均不受影响。
                DeleteTransactionStaging(stagingRoot, assetBundleRoot);
            }
        }

        /// <summary>
        /// 00 同步执行统一构建，供 Unity 批处理门禁复用同一正式入口。
        /// </summary>
        internal static void Build(
            IReadOnlyList<BundleModuleData> selectedModules,
            BuildType buildType = BuildType.AssetBundle,
            int hotPatchVersion = 0,
            string hotAppVersion = "0.0.0",
            string updateNotice = "",
            UnityEditor.BuildTarget buildTarget = UnityEditor.BuildTarget.NoTarget,
            bool allowSevereSharedChanges = false)
        {
            //00 批处理模式没有编辑器更新循环，因此主动推进枚举器直到完成或抛出异常。
            IEnumerator routine = BuildStaged(
                selectedModules,
                buildType,
                hotPatchVersion,
                hotAppVersion,
                updateNotice,
                buildTarget,
                allowSevereSharedChanges);
            while (routine.MoveNext()) { }
        }

        /// <summary>
        /// 00 过滤空项、拒绝空名称和重复名称，并返回稳定的选中模块快照。
        /// </summary>
        private static List<BundleModuleData> NormalizeSelectedModules(IReadOnlyList<BundleModuleData> selectedModules)
        {
            //00 没有选择模块时不允许创建空构建任务。
            if (selectedModules == null)
                throw new ArgumentNullException(nameof(selectedModules));
            List<BundleModuleData> snapshot = selectedModules.Where(module => module != null).ToList();
            if (snapshot.Count == 0)
                throw new InvalidOperationException("没有选择任何可构建资源模块。");
            //00 模块名称会成为目录和文件前缀，空白名称不能进入文件系统。
            if (snapshot.Any(module => string.IsNullOrWhiteSpace(module.moduleName)))
                throw new InvalidOperationException("选中的资源模块包含空模块名称。");
            //00 忽略大小写重复会落到同一目录，必须在任何写操作前拒绝。
            string duplicateName = snapshot
                .GroupBy(module => module.moduleName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(duplicateName))
                throw new InvalidOperationException($"选中的资源模块名称重复：{duplicateName}");
            //00 Shared 优先、再按模块名排序，让构建闭包和日志顺序稳定。
            return snapshot
                .OrderByDescending(module => module.moduleRole == BundleModuleRole.Shared)
                .ThenBy(module => module.moduleName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 00 从选中模块出发计算直接/传递模块依赖闭包，未勾选 Shared 会被自动补入。
        /// </summary>
        private static List<BundleModuleData> ResolveBuildClosure(
            IReadOnlyList<BundleModuleData> selectedModules,
            ModuleOwnershipAnalysis analysis)
        {
            //00 全局配置用于解析未选中依赖；选中快照优先覆盖同名持久化对象。
            Dictionary<string, BundleModuleData> modulesByName =
                new Dictionary<string, BundleModuleData>(StringComparer.OrdinalIgnoreCase);
            BuildBundleConfigura configuration = BuildBundleConfigura.Instance;
            if (configuration != null && configuration.AssetBundleConfig != null)
            {
                foreach (BundleModuleData module in configuration.AssetBundleConfig)
                    if (module != null && !string.IsNullOrWhiteSpace(module.moduleName))
                        modulesByName[module.moduleName.Trim()] = module;
            }
            foreach (BundleModuleData selected in selectedModules)
                modulesByName[selected.moduleName.Trim()] = selected;

            //00 BFS 支持未来出现多层共享依赖时自然扩展，当前矩阵最多得到一层 Shared。
            Queue<BundleModuleData> pendingModules = new Queue<BundleModuleData>(selectedModules);
            Dictionary<string, BundleModuleData> closure =
                new Dictionary<string, BundleModuleData>(StringComparer.OrdinalIgnoreCase);
            while (pendingModules.Count > 0)
            {
                BundleModuleData module = pendingModules.Dequeue();
                //00 已访问模块不重复展开，避免配置错误形成循环时无限处理。
                if (closure.ContainsKey(module.moduleName)) continue;
                closure.Add(module.moduleName, module);
                foreach (string dependencyName in analysis.GetDirectDependencies(module.moduleName))
                {
                    //00 分析得出的依赖必须能回到配置对象，否则无法生成其 Bundle。
                    if (!modulesByName.TryGetValue(dependencyName, out BundleModuleData dependencyModule))
                        throw new InvalidOperationException(
                            $"模块 {module.moduleName} 依赖 {dependencyName}，但构建配置中找不到该模块。");
                    pendingModules.Enqueue(dependencyModule);
                }
            }
            //00 Shared 先收集、再按名称排序，保证上下文和统一列表顺序可复现。
            return closure.Values
                .OrderByDescending(module => module.moduleRole == BundleModuleRole.Shared)
                .ThenBy(module => module.moduleName, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// 00 合并资源路径到模块 Bundle 的映射，重复路径即使 Bundle 名相同也拒绝。
        /// </summary>
        private static Dictionary<string, AssetBundleBuildLocation> MergeAssetLocations(
            IReadOnlyList<BundleModuleData> modules,
            IReadOnlyDictionary<string, BuildBundleCompiler> compilers)
        {
            //00 Unity 资源路径在 Windows 上按忽略大小写比较，避免大小写变体绕过唯一归属。
            Dictionary<string, AssetBundleBuildLocation> globalLocations =
                new Dictionary<string, AssetBundleBuildLocation>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleModuleData module in modules)
            {
                Dictionary<string, AssetBundleBuildLocation> localLocations =
                    compilers[module.moduleName].CreateLocalAssetLocations();
                foreach (KeyValuePair<string, AssetBundleBuildLocation> pair in localLocations)
                {
                    //00 每个资源只允许一个物理 Bundle 身份，重复说明收集器边界仍有漏洞。
                    if (!globalLocations.TryAdd(pair.Key, pair.Value))
                    {
                        AssetBundleBuildLocation existing = globalLocations[pair.Key];
                        throw new InvalidOperationException(
                            $"统一构建资源重复归属：{pair.Key} 同时位于 " +
                            $"{existing.ModuleName}/{existing.BundleName} 与 " +
                            $"{pair.Value.ModuleName}/{pair.Value.BundleName}。");
                    }
                }
            }
            return globalLocations;
        }

        /// <summary>
        /// 00 合并所有模块的 Unity 构建输入，并拒绝跨模块同名 Bundle。
        /// </summary>
        private static List<AssetBundleBuild> MergeBundleBuilds(
            IReadOnlyList<BundleModuleData> modules,
            IReadOnlyDictionary<string, BuildBundleCompiler> compilers,
            out Dictionary<string, string> bundleOwners)
        {
            List<AssetBundleBuild> unifiedBuilds = new List<AssetBundleBuild>();
            bundleOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleModuleData module in modules)
            {
                foreach (AssetBundleBuild bundleBuild in compilers[module.moduleName].Context.BundleBuilds)
                {
                    //00 同名 Bundle 在统一输出目录会互相覆盖，明确报告两个模块而不是交给 Unity 模糊失败。
                    if (bundleOwners.TryGetValue(bundleBuild.assetBundleName, out string existingOwner))
                        throw new InvalidOperationException(
                            $"跨模块 Bundle 名称冲突：{bundleBuild.assetBundleName} 同时属于 " +
                            $"{existingOwner} 与 {module.moduleName}。");
                    bundleOwners.Add(bundleBuild.assetBundleName, module.moduleName);
                    unifiedBuilds.Add(bundleBuild);
                }
            }
            //00 空输入通常表示模块规则无效；Unity 返回空 Manifest 不够直观，因此提前拒绝。
            if (unifiedBuilds.Count == 0)
                throw new InvalidOperationException("统一构建没有生成任何 AssetBundleBuild 输入。");
            return unifiedBuilds;
        }

        /// <summary>
        /// 00 校验收集结果仍遵守物理模块边界；配置 JSON 等框架生成文件没有业务目录拥有者，可安全跳过。
        /// </summary>
        private static void ValidateCollectedOwnership(
            IReadOnlyList<BundleModuleData> modules,
            IReadOnlyDictionary<string, BuildBundleCompiler> compilers,
            ModuleOwnershipAnalysis analysis)
        {
            foreach (BundleModuleData module in modules)
            {
                foreach (AssetBundleBuild bundleBuild in compilers[module.moduleName].Context.BundleBuilds)
                {
                    foreach (string assetPath in bundleBuild.assetNames ?? Array.Empty<string>())
                    {
                        string ownerModule = analysis.GetPhysicalOwnerModule(assetPath);
                        //00 无主文件仅允许框架生成配置等非业务资源继续进入构建。
                        if (string.IsNullOrWhiteSpace(ownerModule)) continue;
                        //00 任一 Business Bundle 收入 Shared 路径都必须在 BuildPipeline 前终止。
                        if (!string.Equals(ownerModule, module.moduleName, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(
                                $"模块构建输入越界：{module.moduleName}/{bundleBuild.assetBundleName} " +
                                $"包含物理属于 {ownerModule} 的资源 {assetPath}。");
                    }
                }
            }
        }

        /// <summary>
        /// 00 对显式选择的 Shared 执行结构变化提示；当前工程消费者只作为参考，不参与模块集合计算。
        /// </summary>
        private static IEnumerator ConfirmSelectedSharedPatchChanges(
            IReadOnlyList<BundleModuleData> selectedModules,
            IReadOnlyList<ConfigFileSnapshot> configSnapshots,
            ModuleOwnershipAnalysis analysis,
            bool allowSevereSharedChanges)
        {
            foreach (BundleModuleData sharedModule in selectedModules.Where(module =>
                         module.moduleRole == BundleModuleRole.Shared))
            {
                ConfigFileSnapshot snapshot = configSnapshots.First(item =>
                    string.Equals(item.ModuleName, sharedModule.moduleName, StringComparison.OrdinalIgnoreCase));
                SharedPatchChangeReport report = SharedPatchChangeAnalyzer.Analyze(
                    snapshot.ReadOriginalConfig(),
                    snapshot.ReadCurrentConfig());
                if (report.Severity == SharedPatchChangeSeverity.None) continue;

                string detailText = string.Join("\n", report.Details.Take(20).Select(detail => "• " + detail));
                if (report.Details.Count > 20)
                    detailText += $"\n• 其余 {report.Details.Count - 20} 项请查看构建日志。";
                if (report.Severity == SharedPatchChangeSeverity.Additive)
                {
                    //00 纯新增不会破坏旧资源身份，只写入报告而不弹窗打断常规构建。
                    Debug.Log($"Shared 模块 {sharedModule.moduleName} 检测到新增结构：\n{detailText}");
                    continue;
                }

                IEnumerable<BundleModuleData> configuredModules =
                    BuildBundleConfigura.Instance?.AssetBundleConfig ?? new List<BundleModuleData>();
                List<string> possibleConsumers = configuredModules
                    .Where(module => module != null &&
                                     module.moduleRole == BundleModuleRole.Business &&
                                     analysis.GetDirectDependencies(module.moduleName)
                                         .Contains(sharedModule.moduleName, StringComparer.OrdinalIgnoreCase))
                    .Select(module => module.moduleName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                string consumerText = possibleConsumers.Count == 0
                    ? "当前工程未检测到直接消费者。"
                    : "当前工程可能受影响模块（仅供参考）：" + string.Join("、", possibleConsumers);
                string selectedText = string.Join("、", selectedModules.Select(module => module.moduleName));
                string message =
                    $"Shared 模块 {sharedModule.moduleName} 检测到严重结构变化：\n\n" +
                    detailText + "\n\n" + consumerText + "\n\n" +
                    $"本次仍只构建开发者显式选择的模块：{selectedText}。\n" +
                    "框架不会自动加入或热更其他模块；线上版本兼容关系由开发者决定。";

                //00 CI 无法交互，必须由调用方显式授权，避免严重变化在无人值守环境中静默发布。
                if (Application.isBatchMode && !allowSevereSharedChanges)
                    throw new InvalidOperationException(
                        message + "\n批处理继续构建请显式传入 allowSevereSharedChanges=true。");
                if (!Application.isBatchMode && !allowSevereSharedChanges)
                {
                    SharedStructureChangeWarningWindow.ConfirmationResult confirmation =
                        SharedStructureChangeWarningWindow.Show(
                        sharedModule.moduleName,
                        report.Details,
                        possibleConsumers,
                        selectedModules.Select(module => module.moduleName).ToArray());
                    while (!confirmation.IsResolved) yield return null;
                    if (!confirmation.Confirmed)
                        throw new OperationCanceledException("开发者取消了包含 Shared 严重结构变化的热更构建。");
                }
                Debug.LogWarning(message + "\n开发者已确认继续构建。");
            }
        }

        /// <summary>
        /// 00 为完整资源构建生成模块清单，并登记逐模块正式目录发布项。
        /// </summary>
        private static void PrepareFullBuildPublishItems(
            IReadOnlyList<BundleModuleData> buildClosure,
            IReadOnlyDictionary<string, BuildBundleCompiler> compilers,
            IReadOnlyDictionary<string, string> stagedModulePaths,
            UnityEditor.BuildTarget buildTarget,
            ICollection<AtomicPublishItem> publishItems)
        {
            foreach (BundleModuleData module in buildClosure)
            {
                BuildBundleCompiler compiler = compilers[module.moduleName];
                string moduleStagingPath = stagedModulePaths[module.moduleName];
                //00 普通资源目录携带基线热更清单；清单计算时目录内尚无清单本身，避免自引用。
                byte[] manifestBytes = compiler.CreateManifestBytesForOrchestration(moduleStagingPath);
                string manifestPath = Path.Combine(
                    moduleStagingPath,
                    compiler.GetManifestFileNameForOrchestration());
                File.WriteAllBytes(manifestPath, manifestBytes);
                //00 正式目录与旧单模块路径完全一致。
                string targetPath = GetAssetBundleModuleTarget(module.moduleName, buildTarget);
                publishItems.Add(AtomicPublishItem.Directory(moduleStagingPath, targetPath));
            }
        }

        /// <summary>
        /// 00 为显式选择的模块准备补丁；仅作为业务依赖自动加入的 Shared 继续执行内容不变门禁。
        /// </summary>
        private static void PrepareHotPatchPublishItems(
            IReadOnlyList<BundleModuleData> selectedModules,
            IReadOnlyList<BundleModuleData> buildClosure,
            IReadOnlyDictionary<string, BuildBundleCompiler> compilers,
            IReadOnlyDictionary<string, string> stagedModulePaths,
            UnityEditor.BuildTarget buildTarget,
            int hotPatchVersion,
            string hotAppVersion,
            string hotStagingRoot,
            string manifestStagingRoot,
            ICollection<AtomicPublishItem> publishItems)
        {
            HashSet<string> explicitlySelectedNames = new HashSet<string>(
                selectedModules.Select(module => module.moduleName),
                StringComparer.OrdinalIgnoreCase);
            //00 自动加入的 Shared 不发布补丁；显式选择的 Shared 则与普通模块一样生成自身补丁。
            foreach (BundleModuleData sharedModule in buildClosure.Where(module =>
                         module.moduleRole == BundleModuleRole.Shared &&
                         !explicitlySelectedNames.Contains(module.moduleName)))
            {
                BuildBundleCompiler compiler = compilers[sharedModule.moduleName];
                string currentTarget = GetAssetBundleModuleTarget(sharedModule.moduleName, buildTarget);
                string manifestName = compiler.GetManifestFileNameForOrchestration();
                if (!AreModuleContentsEqual(
                        stagedModulePaths[sharedModule.moduleName],
                        currentTarget,
                        manifestName))
                {
                    throw new InvalidOperationException(
                        $"业务热更依赖的 Shared 模块 {sharedModule.moduleName} 已发生变化，但本次没有显式选择该模块。" +
                        "框架不会自动扩展补丁集合；请确认发布策略后显式勾选 Shared，或恢复 Shared 变更。");
                }
            }

            //00 只为用户本轮选中的业务模块生成补丁，自动闭包模块不会改变勾选语义。
            foreach (BundleModuleData module in selectedModules)
            {
                BuildBundleCompiler compiler = compilers[module.moduleName];
                string moduleStagingPath = stagedModulePaths[module.moduleName];
                //00 热更仍更新普通 AssetBundle 基线目录，与旧构建入口行为一致。
                publishItems.Add(AtomicPublishItem.Directory(
                    moduleStagingPath,
                    GetAssetBundleModuleTarget(module.moduleName, buildTarget)));

                //00 补丁 staging 复制普通目录的最终加密文件；此时尚未写入备份清单。
                string hotPatchStagingPath = Path.Combine(hotStagingRoot, module.moduleName);
                CopyTopLevelFiles(moduleStagingPath, hotPatchStagingPath);
                //00 清单 MD5 只计算实际下载文件，不把清单自身纳入递归校验。
                byte[] manifestBytes = compiler.CreateManifestBytesForOrchestration(hotPatchStagingPath);
                string manifestName = compiler.GetManifestFileNameForOrchestration();
                //00 每个补丁目录保留一份清单，继续支持既有版本回退流程。
                File.WriteAllBytes(Path.Combine(hotPatchStagingPath, manifestName), manifestBytes);
                //00 主清单也先写 staging，最后与目录一起纳入原子发布事务。
                Directory.CreateDirectory(manifestStagingRoot);
                string masterManifestStagingPath = Path.Combine(
                    manifestStagingRoot,
                    module.moduleName + "_" + manifestName);
                File.WriteAllBytes(masterManifestStagingPath, manifestBytes);

                //00 补丁目标目录结构保持 HotAssets/模块/应用版本/补丁版本/平台。
                string hotPatchTarget = Path.GetFullPath(Path.Combine(
                    GetProjectOutputRoot("HotAssets"),
                    module.moduleName,
                    string.IsNullOrWhiteSpace(hotAppVersion) ? "0.0.0" : hotAppVersion,
                    hotPatchVersion.ToString(),
                    buildTarget.ToString()));
                //00 模块主清单位于 HotAssets/模块 根目录，下载检查继续读取旧位置。
                string masterManifestTarget = Path.GetFullPath(Path.Combine(
                    GetProjectOutputRoot("HotAssets"),
                    module.moduleName,
                    manifestName));
                publishItems.Add(AtomicPublishItem.Directory(hotPatchStagingPath, hotPatchTarget));
                publishItems.Add(AtomicPublishItem.File(masterManifestStagingPath, masterManifestTarget));
            }
        }

        /// <summary>
        /// 00 比较 Shared staging 与现有完整构建基线；忽略普通目录中由构建生成的基线清单。
        /// </summary>
        private static bool AreModuleContentsEqual(string stagedPath, string currentPath, string manifestFileName)
        {
            //00 没有历史完整构建时无法证明 Shared 未变化，业务补丁必须停止。
            if (!Directory.Exists(stagedPath) || !Directory.Exists(currentPath)) return false;
            Dictionary<string, string> stagedFiles = GetComparableFileHashes(stagedPath, manifestFileName);
            Dictionary<string, string> currentFiles = GetComparableFileHashes(currentPath, manifestFileName);
            //00 文件数量变化直接表示新增或删除，不需要继续逐项比较。
            if (stagedFiles.Count != currentFiles.Count) return false;
            foreach (KeyValuePair<string, string> stagedFile in stagedFiles)
            {
                //00 文件名或最终字节 MD5 任一变化都视为 Shared 基线变化。
                if (!currentFiles.TryGetValue(stagedFile.Key, out string currentHash) ||
                    !string.Equals(stagedFile.Value, currentHash, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 00 生成顶层文件名到 MD5 的稳定映射，排除普通构建基线清单。
        /// </summary>
        private static Dictionary<string, string> GetComparableFileHashes(string directoryPath, string manifestFileName)
        {
            Dictionary<string, string> hashes =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string filePath in Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(filePath);
                //00 普通完整构建目录比热更临时模块多一个基线清单，该文件不属于 Shared 内容变化。
                if (string.Equals(fileName, manifestFileName, StringComparison.OrdinalIgnoreCase)) continue;
                hashes.Add(fileName, MD5.GetMd5FromFile(filePath));
            }
            return hashes;
        }

        /// <summary>
        /// 00 复制目录顶层文件到新 staging；模块输出当前没有子目录，发现子目录时明确拒绝。
        /// </summary>
        private static void CopyTopLevelFiles(string sourcePath, string destinationPath)
        {
            if (!Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException($"找不到待复制模块 staging：{sourcePath}");
            //00 当前运行时下载协议只支持扁平文件名，意外子目录不能被静默遗漏。
            if (Directory.GetDirectories(sourcePath, "*", SearchOption.TopDirectoryOnly).Length > 0)
                throw new InvalidOperationException($"模块输出包含不支持的子目录：{sourcePath}");
            Directory.CreateDirectory(destinationPath);
            foreach (string sourceFile in Directory.GetFiles(sourcePath, "*", SearchOption.TopDirectoryOnly))
                File.Copy(sourceFile, Path.Combine(destinationPath, Path.GetFileName(sourceFile)), false);
        }

        /// <summary>
        /// 00 返回工程根目录下指定输出文件夹的规范绝对路径。
        /// </summary>
        private static string GetProjectOutputRoot(string folderName)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", folderName));
        }

        /// <summary>
        /// 00 返回普通 AssetBundle 的模块/平台正式目录。
        /// </summary>
        private static string GetAssetBundleModuleTarget(string moduleName, UnityEditor.BuildTarget buildTarget)
        {
            return Path.GetFullPath(Path.Combine(
                GetProjectOutputRoot("AssetBundle"),
                moduleName,
                buildTarget.ToString()));
        }

        /// <summary>
        /// 00 删除当前事务 staging，并校验它确实位于固定临时根目录之下。
        /// </summary>
        private static void DeleteTransactionStaging(string stagingPath, string assetBundleRoot)
        {
            if (string.IsNullOrWhiteSpace(stagingPath) || !Directory.Exists(stagingPath)) return;
            string normalizedStaging = Path.GetFullPath(stagingPath).TrimEnd(Path.DirectorySeparatorChar);
            string allowedRoot = Path.GetFullPath(Path.Combine(assetBundleRoot, StagingDirectoryName))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            //00 防御任何路径计算错误，绝不递归删除 AssetBundle 根或工程其他目录。
            if (!normalizedStaging.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"拒绝清理越界 staging：{normalizedStaging}");
            Directory.Delete(normalizedStaging, true);
            //00 最后一轮事务结束后移除空临时根，保持输出目录整洁。
            string stagingParent = Path.GetDirectoryName(normalizedStaging);
            if (!string.IsNullOrWhiteSpace(stagingParent) &&
                Directory.Exists(stagingParent) &&
                !Directory.EnumerateFileSystemEntries(stagingParent).Any())
                Directory.Delete(stagingParent, false);
        }

        /// <summary>
        /// 00 保存配置 JSON 与 meta 的构建前状态，用于统一构建失败回滚。
        /// </summary>
        private sealed class ConfigFileSnapshot
        {
            //00 JSON 与 meta 分别记录是否存在和原始字节，兼容首次构建新模块。
            private readonly string mConfigPath;
            private readonly string mMetaPath;
            private readonly byte[] mConfigBytes;
            private readonly byte[] mMetaBytes;
            private readonly bool mConfigExisted;
            private readonly bool mMetaExisted;
            internal string ModuleName { get; }

            private ConfigFileSnapshot(string moduleName, string configPath)
            {
                ModuleName = moduleName;
                mConfigPath = configPath;
                mMetaPath = configPath + ".meta";
                mConfigExisted = File.Exists(mConfigPath);
                mMetaExisted = File.Exists(mMetaPath);
                mConfigBytes = mConfigExisted ? File.ReadAllBytes(mConfigPath) : null;
                mMetaBytes = mMetaExisted ? File.ReadAllBytes(mMetaPath) : null;
            }

            internal static ConfigFileSnapshot Capture(BundleModuleData module)
            {
                //00 路径公式与 BuildBundleCompiler.WriteAssetBundleConfig 保持一致。
                string configPath = Path.GetFullPath(Path.Combine(
                    Application.dataPath,
                    BundleSettings.Instance.ZMAssetRootPath,
                    "Config",
                    module.moduleName.ToLowerInvariant() + "assetbundleconfig.json"));
                return new ConfigFileSnapshot(module.moduleName, configPath);
            }

            internal BundleConfig ReadOriginalConfig()
            {
                if (!mConfigExisted || mConfigBytes == null || mConfigBytes.Length == 0) return null;
                return JsonConvert.DeserializeObject<BundleConfig>(System.Text.Encoding.UTF8.GetString(mConfigBytes));
            }

            internal BundleConfig ReadCurrentConfig()
            {
                if (!File.Exists(mConfigPath)) return null;
                return JsonConvert.DeserializeObject<BundleConfig>(File.ReadAllText(mConfigPath));
            }

            internal void Restore()
            {
                //00 原来存在则原字节覆盖恢复；原来不存在则只删除本事务新建文件。
                RestoreFile(mConfigPath, mConfigExisted, mConfigBytes);
                RestoreFile(mMetaPath, mMetaExisted, mMetaBytes);
            }

            private static void RestoreFile(string path, bool existed, byte[] bytes)
            {
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
                    File.WriteAllBytes(path, bytes ?? Array.Empty<byte>());
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>
        /// 00 描述一个待原子切换的文件或目录。
        /// </summary>
        internal sealed class AtomicPublishItem
        {
            internal string StagingPath;
            internal string TargetPath;
            internal bool IsDirectory;

            internal static AtomicPublishItem Directory(string stagingPath, string targetPath)
            {
                return new AtomicPublishItem
                {
                    StagingPath = Path.GetFullPath(stagingPath),
                    TargetPath = Path.GetFullPath(targetPath),
                    IsDirectory = true
                };
            }

            internal static AtomicPublishItem File(string stagingPath, string targetPath)
            {
                return new AtomicPublishItem
                {
                    StagingPath = Path.GetFullPath(stagingPath),
                    TargetPath = Path.GetFullPath(targetPath),
                    IsDirectory = false
                };
            }
        }

        /// <summary>
        /// 00 使用“全部备份 → 全部切换 → 失败全部恢复”发布多个模块和清单。
        /// </summary>
        internal static class AtomicPublisher
        {
            internal static void Publish(IReadOnlyList<AtomicPublishItem> items, string transactionId)
            {
                if (items == null || items.Count == 0)
                    throw new InvalidOperationException("原子发布没有任何目标。");
                //00 目标去重防止同一路径被两个 staging 连续覆盖。
                string duplicateTarget = items
                    .GroupBy(item => item.TargetPath, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(duplicateTarget))
                    throw new InvalidOperationException($"原子发布目标重复：{duplicateTarget}");

                Dictionary<AtomicPublishItem, string> backupPaths =
                    new Dictionary<AtomicPublishItem, string>();
                List<AtomicPublishItem> publishedItems = new List<AtomicPublishItem>();
                try
                {
                    //00 先完整预检所有项，再移动任何正式目标；后一个 staging 缺失时不会先动前一个模块。
                    foreach (AtomicPublishItem item in items)
                    {
                        //00 staging 类型必须与声明一致，缺失项不能进入备份阶段。
                        bool stagingExists = item.IsDirectory
                            ? System.IO.Directory.Exists(item.StagingPath)
                            : System.IO.File.Exists(item.StagingPath);
                        if (!stagingExists)
                            throw new FileNotFoundException($"原子发布 staging 不存在：{item.StagingPath}");
                        //00 确保目标父目录存在，但不提前创建目标本身。
                        string targetParent = Path.GetDirectoryName(item.TargetPath);
                        if (string.IsNullOrWhiteSpace(targetParent))
                            throw new InvalidOperationException($"原子发布目标缺少父目录：{item.TargetPath}");
                        System.IO.Directory.CreateDirectory(targetParent);

                        string backupPath = item.TargetPath + ".zmbackup-" + transactionId;
                        //00 GUID 备份理论上不会存在；若存在说明事务标识冲突或历史异常，必须停止。
                        if (System.IO.Directory.Exists(backupPath) || System.IO.File.Exists(backupPath))
                            throw new IOException($"原子发布备份路径已存在：{backupPath}");
                        //00 目标若以相反文件类型存在，不能把它当作“无旧目标”，否则 Move 只会给出难理解的占用错误。
                        if (item.IsDirectory && System.IO.File.Exists(item.TargetPath))
                            throw new IOException($"原子发布目录目标当前是文件：{item.TargetPath}");
                        if (!item.IsDirectory && System.IO.Directory.Exists(item.TargetPath))
                            throw new IOException($"原子发布文件目标当前是目录：{item.TargetPath}");
                    }

                    //00 全部预检通过后才统一备份旧目标，形成可完整回滚的事务起点。
                    foreach (AtomicPublishItem item in items)
                    {
                        string backupPath = item.TargetPath + ".zmbackup-" + transactionId;
                        bool targetExists = item.IsDirectory
                            ? System.IO.Directory.Exists(item.TargetPath)
                            : System.IO.File.Exists(item.TargetPath);
                        if (targetExists)
                        {
                            if (item.IsDirectory)
                                System.IO.Directory.Move(item.TargetPath, backupPath);
                            else
                                System.IO.File.Move(item.TargetPath, backupPath);
                            backupPaths.Add(item, backupPath);
                        }
                    }

                    foreach (AtomicPublishItem item in items)
                    {
                        //00 staging 与目标位于同一工程磁盘，Move 在目录项层面完成最终切换。
                        if (item.IsDirectory)
                            System.IO.Directory.Move(item.StagingPath, item.TargetPath);
                        else
                            System.IO.File.Move(item.StagingPath, item.TargetPath);
                        publishedItems.Add(item);
                    }

                }
                catch (Exception publishException)
                {
                    //00 回滚每个目标都独立捕获异常，某个文件被占用时仍继续恢复其他模块和清单。
                    List<Exception> rollbackExceptions = new List<Exception>();
                    //00 先移除本事务已经发布的新目标，为旧备份恢复腾出精确路径。
                    for (int index = publishedItems.Count - 1; index >= 0; index--)
                    {
                        AtomicPublishItem published = publishedItems[index];
                        try
                        {
                            if (published.IsDirectory && System.IO.Directory.Exists(published.TargetPath))
                                System.IO.Directory.Delete(published.TargetPath, true);
                            else if (!published.IsDirectory && System.IO.File.Exists(published.TargetPath))
                                System.IO.File.Delete(published.TargetPath);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackExceptions.Add(new IOException(
                                $"删除本事务新目标失败：{published.TargetPath}",
                                rollbackException));
                        }
                    }
                    //00 再逐项恢复构建前目标；未存在旧目标的项无需创建。
                    foreach (KeyValuePair<AtomicPublishItem, string> backup in backupPaths)
                    {
                        try
                        {
                            if (backup.Key.IsDirectory && System.IO.Directory.Exists(backup.Value))
                                System.IO.Directory.Move(backup.Value, backup.Key.TargetPath);
                            else if (!backup.Key.IsDirectory && System.IO.File.Exists(backup.Value))
                                System.IO.File.Move(backup.Value, backup.Key.TargetPath);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackExceptions.Add(new IOException(
                                $"恢复旧发布目标失败：{backup.Key.TargetPath}，备份：{backup.Value}",
                                rollbackException));
                        }
                    }
                    //00 回滚不完整时同时保留原发布异常和全部恢复异常，避免真正根因被最后一次 IOException 覆盖。
                    if (rollbackExceptions.Count > 0)
                    {
                        rollbackExceptions.Insert(0, publishException);
                        throw new AggregateException(
                            "资源构建原子发布失败，且部分目标未能自动回滚；请按异常中的备份路径人工恢复。",
                            rollbackExceptions);
                    }
                    //00 回滚完整时原样抛出最初发布异常，保留原始堆栈供定位。
                    throw;
                }

                //00 只有“全部新目标均已切换成功”才会走到这里，原子事务至此已经正式提交。
                //00 备份清理属于提交后的维护动作，不能因为文件占用等清理异常再次回滚已生效的新产物。
                foreach (KeyValuePair<AtomicPublishItem, string> backup in backupPaths)
                {
                    try
                    {
                        //00 正常情况下立即删除旧目录或旧文件，避免长期占用双份磁盘空间。
                        if (backup.Key.IsDirectory)
                            System.IO.Directory.Delete(backup.Value, true);
                        else
                            System.IO.File.Delete(backup.Value);
                    }
                    catch (Exception cleanupException)
                    {
                        //00 保留清理失败的备份以便人工恢复，并给出精确路径；下次构建使用新事务 ID，不会覆盖它。
                        Debug.LogWarning(
                            $"资源构建已成功发布，但旧备份清理失败，已保留备份：{backup.Value}\n" +
                            cleanupException);
                    }
                }
            }
        }
    }
}
