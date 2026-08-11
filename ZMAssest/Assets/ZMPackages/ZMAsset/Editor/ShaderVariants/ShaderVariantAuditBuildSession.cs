using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZM.Asset
{
    /// <summary>
    /// Opens a bounded audit session around one Unity AssetBundle build invocation. AuditOnly is observational;
    /// GeneratedAllowlist applies its immutable policy only at the final configured preprocessor stage.
    /// </summary>
    internal static class ShaderVariantAuditBuildCoordinator
    {
        private static readonly object Gate = new object();
        private static ShaderVariantAuditBuildSession s_CurrentSession;

        internal static ShaderVariantAuditBuildScope Begin(
            string contextName,
            IEnumerable<string> moduleNames,
            UnityEditor.BuildTarget buildTarget,
            IReadOnlyList<AssetBundleBuild> bundleBuilds,
            IReadOnlyDictionary<string, string> bundleOwners = null)
        {
            ShaderVariantAuditConfiguration configuration;
            try
            {
                configuration = ShaderVariantAuditSettings.instance.CreateSnapshot();
            }
            catch (Exception exception)
            {
                Debug.LogError($"无法加载 Shader 变体审计设置，本次构建跳过审计。{exception}");
                return ShaderVariantAuditBuildScope.Disabled;
            }

            if (!configuration.IsEnabled) return ShaderVariantAuditBuildScope.Disabled;

            try
            {
                ShaderVariantAuditBuildSession session = new ShaderVariantAuditBuildSession(
                    contextName,
                    moduleNames,
                    buildTarget,
                    bundleBuilds,
                    bundleOwners,
                    configuration);
                lock (Gate)
                {
                    if (s_CurrentSession != null)
                    {
                        if (configuration.StrippingMode != ShaderVariantStrippingMode.AuditOnly)
                            throw new InvalidOperationException(
                                "GeneratedAllowlist 剔除不支持嵌套 AssetBundle 构建会话。");
                        Debug.LogError(
                            "已有 Shader 变体审计会话正在运行，嵌套构建将继续执行但不重复审计。");
                        return ShaderVariantAuditBuildScope.Disabled;
                    }

                    s_CurrentSession = session;
                }

                return new ShaderVariantAuditBuildScope(session);
            }
            catch (Exception exception)
            {
                if (configuration.StrippingMode != ShaderVariantStrippingMode.AuditOnly)
                    throw new InvalidOperationException(
                        "GeneratedAllowlist Shader 剔除会话无法启动，构建已在编译前中止。",
                        exception);
                // AuditOnly must never change whether the existing AssetBundle build succeeds.
                Debug.LogError($"Shader 变体审计无法启动，AssetBundle 构建将继续执行。{exception}");
                return ShaderVariantAuditBuildScope.Disabled;
            }
        }

        internal static void RecordBefore(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> compilerData)
        {
            RecordSafely(
                ShaderVariantAuditSourceKind.BuildCandidateBeforeProcessing,
                shader,
                snippet,
                compilerData);
        }

        internal static void RecordAfter(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> compilerData)
        {
            RecordSafely(
                ShaderVariantAuditSourceKind.BuildCandidateAfterProcessing,
                shader,
                snippet,
                compilerData);
        }

        internal static void ProcessAfter(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> compilerData)
        {
            RecordAfter(shader, snippet, compilerData);
            ShaderVariantAuditBuildSession session = GetCurrentSession();
            if (session == null) return;

            try
            {
                session.ApplyStripping(shader, snippet, compilerData);
            }
            catch (Exception exception)
            {
                if (session.IsStrippingEnabled)
                    throw new InvalidOperationException(
                        $"Shader {shader?.name ?? "<null>"} 的 GeneratedAllowlist 剔除失败。",
                        exception);
                string warning =
                    $"Shader 剔除统计失败：{exception.GetType().Name}: {exception.Message}";
                if (session.AddWarning(warning)) Debug.LogError(warning);
            }
        }

        internal static void Complete(ShaderVariantAuditBuildSession session, bool buildSucceeded)
        {
            if (session == null) return;

            lock (Gate)
            {
                if (!ReferenceEquals(s_CurrentSession, session)) return;
                s_CurrentSession = null;
            }

            try
            {
                session.Complete(buildSucceeded);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"Shader 变体审计报告生成失败，AssetBundle 构建结果不受影响。{exception}");
            }
        }

        private static ShaderVariantAuditBuildSession GetCurrentSession()
        {
            lock (Gate) return s_CurrentSession;
        }

        private static void RecordSafely(
            ShaderVariantAuditSourceKind sourceKind,
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> compilerData)
        {
            ShaderVariantAuditBuildSession session = GetCurrentSession();
            if (session == null) return;

            try
            {
                session.RecordCandidates(sourceKind, shader, snippet, compilerData);
            }
            catch (Exception exception)
            {
                string warning =
                    $"Shader 候选变体在 {sourceKind} 阶段审计失败：" +
                    $"{exception.GetType().Name}: {exception.Message}";
                if (session.AddWarning(warning))
                    Debug.LogError(warning + "AssetBundle 编译将按原有行为继续执行。");
            }
        }
    }

    internal sealed class ShaderVariantAuditBuildScope : IDisposable
    {
        internal static readonly ShaderVariantAuditBuildScope Disabled =
            new ShaderVariantAuditBuildScope(null);

        private ShaderVariantAuditBuildSession m_Session;
        private bool m_BuildSucceeded;

        internal ShaderVariantAuditBuildScope(ShaderVariantAuditBuildSession session)
        {
            m_Session = session;
        }

        internal void MarkBuildSucceeded(bool buildSucceeded)
        {
            if (m_Session != null) m_BuildSucceeded = buildSucceeded;
        }

        public void Dispose()
        {
            ShaderVariantAuditBuildSession session = Interlocked.Exchange(ref m_Session, null);
            if (session != null)
                ShaderVariantAuditBuildCoordinator.Complete(session, m_BuildSucceeded);
        }
    }

    internal sealed class ShaderVariantAuditBuildSession
    {
        private readonly object m_Gate = new object();
        private readonly ShaderVariantAuditConfiguration m_Configuration;
        private readonly ShaderVariantAuditReport m_Report;
        private readonly List<string> m_Warnings = new List<string>();
        private readonly HashSet<string> m_WarningSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderVariantAuditEntry> m_BeforeDetails =
            new Dictionary<string, ShaderVariantAuditEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderVariantAuditEntry> m_AfterDetails =
            new Dictionary<string, ShaderVariantAuditEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderSummaryAccumulator> m_ShaderSummaries =
            new Dictionary<string, ShaderSummaryAccumulator>(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> m_ShaderModuleOwners =
            new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly List<ShaderVariantAuditEntry> m_MaterialEntries;
        private readonly List<ShaderVariantAuditEntry> m_ExplicitEntries;
        private readonly ShaderVariantStrippingPolicy m_StrippingPolicy;

        internal bool IsStrippingEnabled => m_StrippingPolicy.IsEnabled;

        internal ShaderVariantAuditBuildSession(
            string contextName,
            IEnumerable<string> moduleNames,
            UnityEditor.BuildTarget buildTarget,
            IReadOnlyList<AssetBundleBuild> bundleBuilds,
            IReadOnlyDictionary<string, string> bundleOwners,
            ShaderVariantAuditConfiguration configuration)
        {
            m_Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            AssetBundleBuild[] buildSnapshot = bundleBuilds?.ToArray() ?? Array.Empty<AssetBundleBuild>();
            m_Report = new ShaderVariantAuditReport
            {
                contextName = string.IsNullOrWhiteSpace(contextName) ? "AssetBundle构建" : contextName.Trim(),
                moduleNames = (moduleNames ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray(),
                activeProfileName = configuration.ActiveProfileName,
                strippingMode = configuration.StrippingMode.ToString(),
                buildTarget = buildTarget.ToString(),
                unityVersion = Application.unityVersion,
                startedUtc = DateTime.UtcNow.ToString("O"),
                detailedVariantLimit = configuration.MaxDetailedVariants,
                bundleCount = buildSnapshot.Length
            };

            m_Report.prewarmCollections = CollectPrewarmCollections(buildSnapshot);
            m_Report.prewarmCollectionCount = m_Report.prewarmCollections.Length;
            m_Report.prewarmShaderCount = m_Report.prewarmCollections.Sum(summary => summary.shaderCount);
            m_Report.prewarmVariantCount = m_Report.prewarmCollections.Sum(summary => summary.variantCount);
            m_StrippingPolicy = ShaderVariantStrippingPolicy.Create(
                m_Report.moduleNames,
                buildTarget,
                buildSnapshot,
                configuration);
            m_Report.strippingAllowlistVariantCount = m_StrippingPolicy.AllowedVariantCount;
            m_Report.strippingManagedShaderCount = m_StrippingPolicy.ManagedShaderCount;

            ShaderMaterialCollectionResult materialResult =
                ShaderMaterialVariantCollector.Collect(
                    buildSnapshot,
                    buildTarget,
                    bundleOwners ?? CreateSingleModuleBundleOwners(buildSnapshot, m_Report.moduleNames));
            m_MaterialEntries = materialResult.Entries;
            m_Report.rootAssetCount = materialResult.RootAssetCount;
            m_Report.materialAssetCount = materialResult.MaterialAssetCount;
            foreach (string warning in materialResult.Warnings) AddWarning(warning);

            List<string> explicitRuleWarnings = new List<string>();
            m_ExplicitEntries = ShaderVariantExplicitRuleCollector.Collect(
                configuration.ExplicitRules,
                buildTarget,
                explicitRuleWarnings);
            foreach (string warning in explicitRuleWarnings) AddWarning(warning);
            m_Report.explicitRuleCount = m_ExplicitEntries.Count;

            foreach (ShaderVariantAuditEntry entry in m_MaterialEntries)
                GetOrCreateSummary(entry).MaterialStates += Math.Max(1, entry.occurrences);
            foreach (ShaderVariantAuditEntry entry in m_ExplicitEntries)
                GetOrCreateSummary(entry).ExplicitRules += Math.Max(1, entry.occurrences);
            BuildShaderModuleOwnerMap();
        }

        internal void ApplyStripping(
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> compilerData)
        {
            ShaderVariantStripOperationResult result =
                m_StrippingPolicy.Apply(shader, snippet.passType, compilerData);
            string shaderPath = shader == null
                ? string.Empty
                : NormalizeAssetPath(AssetDatabase.GetAssetPath(shader));
            string shaderGuid = string.IsNullOrWhiteSpace(shaderPath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(shaderPath);

            lock (m_Gate)
            {
                m_Report.candidateCountAfterStripping += result.FinalCount;
                m_Report.strippedCandidateCount += result.RemovedCount;
                ShaderSummaryAccumulator summary = GetOrCreateSummary(
                    shader?.name,
                    shaderGuid,
                    shaderPath);
                summary.CandidatesAfterStripping += result.FinalCount;

                if (m_StrippingPolicy.IsEnabled && result.OriginalCount > 0)
                {
                    if (result.WasManaged)
                        m_Report.strippingManagedSnippetCount++;
                    else
                        m_Report.strippingUnmanagedSnippetCount++;
                    if (result.UsedPassFallback)
                        m_Report.strippingPassFallbackSnippetCount++;
                    if (result.UsedEmergencyFallback)
                        m_Report.strippingEmergencyFallbackSnippetCount++;
                }
            }

            if (result.UsedEmergencyFallback)
            {
                AddWarning(
                    $"Shader {shader?.name ?? "<null>"} Pass {snippet.passType} 的候选与 allowlist 全部不匹配，" +
                    "已保留一个确定性保底变体。请重新审计并生成 SVC。");
            }
        }

        internal void RecordCandidates(
            ShaderVariantAuditSourceKind sourceKind,
            Shader shader,
            ShaderSnippetData snippet,
            IList<ShaderCompilerData> compilerData)
        {
            if (shader == null || compilerData == null) return;
            bool isBefore = sourceKind == ShaderVariantAuditSourceKind.BuildCandidateBeforeProcessing;
            if (!isBefore && sourceKind != ShaderVariantAuditSourceKind.BuildCandidateAfterProcessing) return;

            string shaderPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(shader));
            string shaderGuid = string.IsNullOrWhiteSpace(shaderPath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(shaderPath);

            lock (m_Gate)
            {
                if (isBefore)
                    m_Report.candidateCountBeforeProcessing += compilerData.Count;
                else
                    m_Report.candidateCountAfterProcessing += compilerData.Count;

                ShaderSummaryAccumulator summary = GetOrCreateSummary(
                    shader.name,
                    shaderGuid,
                    shaderPath);
                if (isBefore)
                    summary.CandidatesBeforeProcessing += compilerData.Count;
                else
                    summary.CandidatesAfterProcessing += compilerData.Count;

                Dictionary<string, ShaderVariantAuditEntry> details =
                    isBefore ? m_BeforeDetails : m_AfterDetails;
                string shaderIdentity = !string.IsNullOrWhiteSpace(shaderGuid)
                    ? shaderGuid
                    : shader.name ?? string.Empty;
                string[] moduleOwners = m_ShaderModuleOwners.TryGetValue(
                    shaderIdentity,
                    out string[] owners)
                    ? owners
                    : new[] { string.Empty };
                for (int index = 0; index < compilerData.Count; index++)
                {
                    foreach (string moduleOwner in moduleOwners)
                    {
                        ShaderVariantAuditEntry entry = CreateCompilerEntry(
                            sourceKind,
                            shader,
                            shaderGuid,
                            shaderPath,
                            moduleOwner,
                            m_Configuration.ActiveProfileName,
                            snippet,
                            compilerData[index]);
                        if (details.TryGetValue(entry.stableKey, out ShaderVariantAuditEntry existing))
                        {
                            existing.occurrences++;
                            continue;
                        }

                        if (details.Count < m_Configuration.MaxDetailedVariants)
                        {
                            details.Add(entry.stableKey, entry);
                        }
                        else if (isBefore)
                        {
                            m_Report.beforeDetailsTruncated = true;
                        }
                        else
                        {
                            m_Report.afterDetailsTruncated = true;
                        }
                    }
                }
            }
        }

        internal bool AddWarning(string warning)
        {
            if (string.IsNullOrWhiteSpace(warning)) return false;
            lock (m_Gate)
            {
                if (!m_WarningSet.Add(warning)) return false;
                m_Warnings.Add(warning);
                return true;
            }
        }

        internal void Complete(bool buildSucceeded)
        {
            ShaderVariantCanonicalManifest manifest;
            lock (m_Gate)
            {
                m_Report.buildSucceeded = buildSucceeded;
                m_Report.completedUtc = DateTime.UtcNow.ToString("O");
                m_Report.materialVariants = SortEntries(m_MaterialEntries);
                m_Report.explicitVariants = SortEntries(m_ExplicitEntries);
                m_Report.candidatesBeforeProcessing = SortEntries(m_BeforeDetails.Values);
                m_Report.candidatesAfterProcessing = SortEntries(m_AfterDetails.Values);
                m_Report.detailedUniqueBeforeCount = m_Report.candidatesBeforeProcessing.Length;
                m_Report.detailedUniqueAfterCount = m_Report.candidatesAfterProcessing.Length;
                m_Report.reportIsComplete =
                    !m_Report.beforeDetailsTruncated && !m_Report.afterDetailsTruncated;
                if (m_Report.beforeDetailsTruncated || m_Report.afterDetailsTruncated)
                {
                    m_Warnings.Add(
                        $"详细编译候选记录超过配置上限 {m_Configuration.MaxDetailedVariants}，" +
                        "汇总计数仍然有效，但详细清单不完整。");
                }

                m_Report.shaderSummaries = m_ShaderSummaries.Values
                    .Select(summary => summary.ToSerializable())
                    .OrderByDescending(summary => summary.candidatesBeforeProcessing)
                    .ThenBy(summary => summary.shaderName, StringComparer.Ordinal)
                    .ToArray();
                m_Report.warnings = m_Warnings
                    .Where(warning => !string.IsNullOrWhiteSpace(warning))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(warning => warning, StringComparer.Ordinal)
                    .ToArray();

                manifest = ShaderVariantCanonicalManifestBuilder.Build(
                    m_Report.contextName,
                    m_Report.buildTarget,
                    m_MaterialEntries.Concat(m_ExplicitEntries));
                m_Report.canonicalManifestHash = manifest.contentHash;
            }

            ShaderVariantAuditReportWriter.Write(m_Report, manifest, m_Configuration.ReportDirectory);
            Debug.Log(
                $"Shader 变体审计完成：{m_Report.contextName}，" +
                $"处理前={m_Report.candidateCountBeforeProcessing}，" +
                $"处理后={m_Report.candidateCountAfterProcessing}，" +
                $"剔除后={m_Report.candidateCountAfterStripping}，" +
                $"清单={m_Report.canonicalManifestHash}。");
        }

        private static ShaderVariantAuditEntry CreateCompilerEntry(
            ShaderVariantAuditSourceKind sourceKind,
            Shader shader,
            string shaderGuid,
            string shaderPath,
            string moduleName,
            string profileName,
            ShaderSnippetData snippet,
            ShaderCompilerData compilerData)
        {
            ShaderKeyword[] shaderKeywords = compilerData.shaderKeywordSet.GetShaderKeywords();
            string[] keywordNames = ShaderVariantKeywordUtility.Normalize(
                shaderKeywords.Select(keyword => keyword.name));
            ShaderVariantAuditEntry entry = new ShaderVariantAuditEntry
            {
                sourceKind = sourceKind,
                shaderName = shader.name ?? string.Empty,
                shaderGuid = shaderGuid ?? string.Empty,
                shaderAssetPath = shaderPath ?? string.Empty,
                moduleName = moduleName ?? string.Empty,
                profileName = profileName ?? string.Empty,
                passName = snippet.passName ?? string.Empty,
                passType = snippet.passType.ToString(),
                shaderStage = snippet.shaderType.ToString(),
                buildTarget = compilerData.buildTarget.ToString(),
                compilerPlatform = compilerData.shaderCompilerPlatform.ToString(),
                graphicsTier = compilerData.graphicsTier.ToString(),
                keywords = keywordNames,
                sourceAssets = Array.Empty<string>(),
                reason = sourceKind == ShaderVariantAuditSourceKind.BuildCandidateBeforeProcessing
                    ? "项目 Shader 预处理器执行前观察到的编译候选。"
                    : "项目 Shader 预处理器执行后观察到的编译候选。",
                occurrences = 1
            };
            entry.stableKey = ShaderVariantKeywordUtility.BuildStableKey(entry);
            return entry;
        }

        private void BuildShaderModuleOwnerMap()
        {
            IEnumerable<ShaderVariantAuditEntry> sourceEntries =
                m_MaterialEntries.Concat(m_ExplicitEntries);
            foreach (IGrouping<string, ShaderVariantAuditEntry> shaderGroup in sourceEntries
                         .Where(entry => entry != null)
                         .GroupBy(
                             entry => !string.IsNullOrWhiteSpace(entry.shaderGuid)
                                 ? entry.shaderGuid
                                 : entry.shaderName ?? string.Empty,
                             StringComparer.Ordinal))
            {
                string[] owners = shaderGroup
                    .Where(entry =>
                        string.IsNullOrWhiteSpace(entry.profileName) ||
                        string.Equals(
                            entry.profileName,
                            m_Configuration.ActiveProfileName,
                            StringComparison.Ordinal))
                    .SelectMany(entry => string.IsNullOrWhiteSpace(entry.moduleName)
                        ? m_Report.moduleNames
                        : new[] { entry.moduleName.Trim() })
                    .Where(module => !string.IsNullOrWhiteSpace(module))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(module => module, StringComparer.Ordinal)
                    .ToArray();
                if (owners.Length > 0) m_ShaderModuleOwners[shaderGroup.Key] = owners;
            }
        }

        private ShaderSummaryAccumulator GetOrCreateSummary(ShaderVariantAuditEntry entry)
        {
            return GetOrCreateSummary(entry.shaderName, entry.shaderGuid, entry.shaderAssetPath);
        }

        private ShaderSummaryAccumulator GetOrCreateSummary(
            string shaderName,
            string shaderGuid,
            string shaderAssetPath)
        {
            string identity = !string.IsNullOrWhiteSpace(shaderGuid) ? shaderGuid : shaderName ?? string.Empty;
            if (!m_ShaderSummaries.TryGetValue(identity, out ShaderSummaryAccumulator summary))
            {
                summary = new ShaderSummaryAccumulator(shaderName, shaderGuid, shaderAssetPath);
                m_ShaderSummaries.Add(identity, summary);
            }
            return summary;
        }

        private static ShaderVariantAuditEntry[] SortEntries(IEnumerable<ShaderVariantAuditEntry> entries)
        {
            return (entries ?? Array.Empty<ShaderVariantAuditEntry>())
                .Where(entry => entry != null)
                .OrderBy(entry => entry.stableKey, StringComparer.Ordinal)
                .ToArray();
        }

        private ShaderVariantAuditPrewarmCollectionSummary[] CollectPrewarmCollections(
            IEnumerable<AssetBundleBuild> bundleBuilds)
        {
            Dictionary<string, ShaderVariantAuditPrewarmCollectionSummary> summaries =
                new Dictionary<string, ShaderVariantAuditPrewarmCollectionSummary>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (AssetBundleBuild bundleBuild in bundleBuilds ?? Array.Empty<AssetBundleBuild>())
            {
                foreach (string assetPath in bundleBuild.assetNames ?? Array.Empty<string>())
                {
                    string normalizedPath = NormalizeAssetPath(assetPath);
                    if (string.IsNullOrWhiteSpace(normalizedPath)) continue;
                    ShaderVariantPrewarmManifest manifest =
                        AssetDatabase.LoadAssetAtPath<ShaderVariantPrewarmManifest>(normalizedPath);
                    if (manifest == null) continue;

                    if (manifest.SchemaVersion != ShaderVariantPrewarmManifest.CurrentSchemaVersion)
                    {
                        AddWarning(
                            $"Shader 预热清单版本不受支持：{normalizedPath}，" +
                            $"期望 {ShaderVariantPrewarmManifest.CurrentSchemaVersion}，实际 {manifest.SchemaVersion}。");
                    }

                    ShaderVariantAuditPrewarmCollectionSummary summary =
                        new ShaderVariantAuditPrewarmCollectionSummary
                        {
                            moduleName = manifest.ModuleName,
                            profileName = manifest.ProfileName,
                            buildTarget = manifest.BuildTarget,
                            bundleName = bundleBuild.assetBundleName ?? string.Empty,
                            manifestAssetPath = normalizedPath,
                            collectionAssetPath = NormalizeAssetPath(
                                AssetDatabase.GetAssetPath(manifest.Collection)),
                            sourceManifestHash = manifest.SourceManifestHash,
                            collectionContentHash = manifest.CollectionContentHash,
                            shaderCount = manifest.ShaderCount,
                            variantCount = manifest.VariantCount
                        };
                    if (!summaries.TryAdd(normalizedPath, summary))
                    {
                        AddWarning($"Shader 预热清单被重复加入构建列表：{normalizedPath}");
                    }
                }
            }

            return summaries.Values
                .OrderBy(summary => summary.moduleName, StringComparer.Ordinal)
                .ThenBy(summary => summary.profileName, StringComparer.Ordinal)
                .ThenBy(summary => summary.manifestAssetPath, StringComparer.Ordinal)
                .ToArray();
        }

        private static IReadOnlyDictionary<string, string> CreateSingleModuleBundleOwners(
            IEnumerable<AssetBundleBuild> bundleBuilds,
            IReadOnlyList<string> moduleNames)
        {
            if (moduleNames == null || moduleNames.Count != 1)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> owners =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetBundleBuild bundleBuild in bundleBuilds ?? Array.Empty<AssetBundleBuild>())
            {
                if (string.IsNullOrWhiteSpace(bundleBuild.assetBundleName)) continue;
                owners[bundleBuild.assetBundleName] = moduleNames[0];
            }
            return owners;
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/');
        }

        private sealed class ShaderSummaryAccumulator
        {
            internal readonly string ShaderName;
            internal readonly string ShaderGuid;
            internal readonly string ShaderAssetPath;
            internal int MaterialStates;
            internal int ExplicitRules;
            internal int CandidatesBeforeProcessing;
            internal int CandidatesAfterProcessing;
            internal int CandidatesAfterStripping;

            internal ShaderSummaryAccumulator(string shaderName, string shaderGuid, string shaderAssetPath)
            {
                ShaderName = shaderName ?? string.Empty;
                ShaderGuid = shaderGuid ?? string.Empty;
                ShaderAssetPath = shaderAssetPath ?? string.Empty;
            }

            internal ShaderVariantAuditShaderSummary ToSerializable()
            {
                return new ShaderVariantAuditShaderSummary
                {
                    shaderName = ShaderName,
                    shaderGuid = ShaderGuid,
                    shaderAssetPath = ShaderAssetPath,
                    materialStates = MaterialStates,
                    explicitRules = ExplicitRules,
                    candidatesBeforeProcessing = CandidatesBeforeProcessing,
                    candidatesAfterProcessing = CandidatesAfterProcessing,
                    candidatesAfterStripping = CandidatesAfterStripping
                };
            }
        }
    }
}
