using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZM.Asset
{
    internal readonly struct ShaderVariantStripOperationResult
    {
        internal readonly int OriginalCount;
        internal readonly int FinalCount;
        internal readonly bool WasManaged;
        internal readonly bool UsedPassFallback;
        internal readonly bool UsedEmergencyFallback;

        internal int RemovedCount => Math.Max(0, OriginalCount - FinalCount);

        internal ShaderVariantStripOperationResult(
            int originalCount,
            int finalCount,
            bool wasManaged,
            bool usedPassFallback,
            bool usedEmergencyFallback)
        {
            OriginalCount = Math.Max(0, originalCount);
            FinalCount = Math.Max(0, finalCount);
            WasManaged = wasManaged;
            UsedPassFallback = usedPassFallback;
            UsedEmergencyFallback = usedEmergencyFallback;
        }
    }

    internal readonly struct ShaderVariantStripSelection
    {
        internal readonly bool[] KeepCandidates;
        internal readonly bool WasManaged;
        internal readonly bool UsedPassFallback;
        internal readonly bool UsedEmergencyFallback;

        internal int FinalCount => KeepCandidates?.Count(keep => keep) ?? 0;

        internal ShaderVariantStripSelection(
            bool[] keepCandidates,
            bool wasManaged,
            bool usedPassFallback,
            bool usedEmergencyFallback)
        {
            KeepCandidates = keepCandidates ?? Array.Empty<bool>();
            WasManaged = wasManaged;
            UsedPassFallback = usedPassFallback;
            UsedEmergencyFallback = usedEmergencyFallback;
        }
    }

    internal sealed class ShaderVariantAllowlistIndex
    {
        private readonly HashSet<string> m_ManagedShaders =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> m_AllowedKeywordSignatures =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        internal int VariantCount { get; }
        internal int ManagedShaderCount => m_ManagedShaders.Count;

        internal ShaderVariantAllowlistIndex(IEnumerable<ShaderVariantStripEntry> entries)
        {
            HashSet<string> variantKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShaderVariantStripEntry entry in entries ?? Array.Empty<ShaderVariantStripEntry>())
            {
                if (entry == null)
                    throw new InvalidDataException("Shader 剔除 allowlist 包含空变体记录。");
                if (string.IsNullOrWhiteSpace(entry.ShaderIdentity))
                    throw new InvalidDataException("Shader 剔除 allowlist 包含缺失 Shader 身份的变体。");
                if (!Enum.TryParse(entry.passType, true, out PassType passType) ||
                    !Enum.IsDefined(typeof(PassType), passType))
                    throw new InvalidDataException(
                        $"Shader 剔除 allowlist 的 PassType 无效：{entry.passType}");

                string identity = entry.ShaderIdentity;
                string passKey = BuildPassKey(identity, passType);
                string keywordSignature = BuildKeywordSignature(entry.keywords);
                string variantKey = passKey + "\u001f" + keywordSignature;
                m_ManagedShaders.Add(identity);
                if (!m_AllowedKeywordSignatures.TryGetValue(
                        passKey,
                        out HashSet<string> allowedSignatures))
                {
                    allowedSignatures = new HashSet<string>(StringComparer.Ordinal);
                    m_AllowedKeywordSignatures.Add(passKey, allowedSignatures);
                }
                allowedSignatures.Add(keywordSignature);
                variantKeys.Add(variantKey);
            }
            VariantCount = variantKeys.Count;
        }

        internal bool IsShaderManaged(string shaderIdentity)
        {
            return !string.IsNullOrWhiteSpace(shaderIdentity) &&
                   m_ManagedShaders.Contains(shaderIdentity);
        }

        internal bool TryGetAllowedKeywords(
            string shaderIdentity,
            PassType passType,
            out HashSet<string> allowedSignatures)
        {
            return m_AllowedKeywordSignatures.TryGetValue(
                BuildPassKey(shaderIdentity, passType),
                out allowedSignatures);
        }

        internal static string BuildKeywordSignature(IEnumerable<string> keywords)
        {
            return string.Join("\u001e", ShaderVariantKeywordUtility.Normalize(keywords));
        }

        internal ShaderVariantStripSelection SelectCandidates(
            string shaderIdentity,
            PassType passType,
            IReadOnlyList<string> candidateSignatures)
        {
            int count = candidateSignatures?.Count ?? 0;
            bool[] keepAll = Enumerable.Repeat(true, count).ToArray();
            if (!IsShaderManaged(shaderIdentity))
                return new ShaderVariantStripSelection(keepAll, false, false, false);
            if (!TryGetAllowedKeywords(shaderIdentity, passType, out HashSet<string> allowedSignatures))
                return new ShaderVariantStripSelection(keepAll, true, true, false);

            bool[] keep = new bool[count];
            int keptCount = 0;
            for (int index = 0; index < count; index++)
            {
                keep[index] = allowedSignatures.Contains(candidateSignatures[index] ?? string.Empty);
                if (keep[index]) keptCount++;
            }
            if (keptCount > 0 || count == 0)
                return new ShaderVariantStripSelection(keep, true, false, false);

            int fallbackIndex = 0;
            for (int index = 1; index < count; index++)
            {
                if (string.CompareOrdinal(
                        candidateSignatures[index] ?? string.Empty,
                        candidateSignatures[fallbackIndex] ?? string.Empty) < 0)
                    fallbackIndex = index;
            }
            keep[fallbackIndex] = true;
            return new ShaderVariantStripSelection(keep, true, false, true);
        }

        private static string BuildPassKey(string shaderIdentity, PassType passType)
        {
            return (shaderIdentity ?? string.Empty) + "\u001f" + passType;
        }
    }

    /// <summary>
    /// Immutable per-build stripping policy. It only removes variants for shader/pass pairs represented by the
    /// generated allowlist. Unknown shaders and unknown passes remain untouched, and an emergency fallback always
    /// preserves one deterministic candidate if compiler state ever diverges from generated evidence.
    /// </summary>
    internal sealed class ShaderVariantStrippingPolicy
    {
        internal static readonly ShaderVariantStrippingPolicy AuditOnly =
            new ShaderVariantStrippingPolicy(null);

        private readonly ShaderVariantAllowlistIndex m_Index;

        internal bool IsEnabled => m_Index != null;
        internal int AllowedVariantCount => m_Index?.VariantCount ?? 0;
        internal int ManagedShaderCount => m_Index?.ManagedShaderCount ?? 0;

        private ShaderVariantStrippingPolicy(ShaderVariantAllowlistIndex index)
        {
            m_Index = index;
        }

        internal static ShaderVariantStrippingPolicy CreateFromEntries(
            IEnumerable<ShaderVariantStripEntry> entries)
        {
            return new ShaderVariantStrippingPolicy(new ShaderVariantAllowlistIndex(entries));
        }

        internal static ShaderVariantStrippingPolicy Create(
            IReadOnlyList<string> moduleNames,
            UnityEditor.BuildTarget buildTarget,
            IReadOnlyList<AssetBundleBuild> bundleBuilds,
            ShaderVariantAuditConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (configuration.StrippingMode == ShaderVariantStrippingMode.AuditOnly)
                return AuditOnly;

            string[] modules = (moduleNames ?? Array.Empty<string>())
                .Where(module => !string.IsNullOrWhiteSpace(module))
                .Select(module => module.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(module => module, StringComparer.Ordinal)
                .ToArray();
            if (modules.Length == 0)
                throw new InvalidOperationException("GeneratedAllowlist 剔除没有可验证的模块范围。");

            HashSet<string> buildAssetPaths = new HashSet<string>(
                (bundleBuilds ?? Array.Empty<AssetBundleBuild>())
                    .SelectMany(build => build.assetNames ?? Array.Empty<string>())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeAssetPath),
                StringComparer.OrdinalIgnoreCase);
            List<ShaderVariantStripEntry> combinedEntries = new List<ShaderVariantStripEntry>();
            foreach (string moduleName in modules)
            {
                ValidateAndAppendModule(
                    moduleName,
                    configuration.ActiveProfileName,
                    buildTarget,
                    buildAssetPaths,
                    combinedEntries);
            }

            return new ShaderVariantStrippingPolicy(
                new ShaderVariantAllowlistIndex(combinedEntries));
        }

        internal ShaderVariantStripOperationResult Apply(
            Shader shader,
            PassType passType,
            IList<ShaderCompilerData> compilerData)
        {
            int originalCount = compilerData?.Count ?? 0;
            if (!IsEnabled || shader == null || compilerData == null || originalCount == 0)
                return new ShaderVariantStripOperationResult(
                    originalCount,
                    originalCount,
                    false,
                    false,
                    false);

            string shaderIdentity = GetShaderIdentity(shader);
            return Apply(shaderIdentity, passType, compilerData);
        }

        internal ShaderVariantStripOperationResult Apply(
            string shaderIdentity,
            PassType passType,
            IList<ShaderCompilerData> compilerData)
        {
            int originalCount = compilerData?.Count ?? 0;
            if (!IsEnabled || string.IsNullOrWhiteSpace(shaderIdentity) ||
                compilerData == null || originalCount == 0)
                return new ShaderVariantStripOperationResult(
                    originalCount,
                    originalCount,
                    false,
                    false,
                    false);

            string[] candidateSignatures = new string[originalCount];
            for (int index = 0; index < originalCount; index++)
                candidateSignatures[index] = GetKeywordSignature(compilerData[index]);
            ShaderVariantStripSelection selection = m_Index.SelectCandidates(
                shaderIdentity,
                passType,
                candidateSignatures);
            for (int index = compilerData.Count - 1; index >= 0; index--)
            {
                if (!selection.KeepCandidates[index])
                    compilerData.RemoveAt(index);
            }

            return new ShaderVariantStripOperationResult(
                originalCount,
                compilerData.Count,
                selection.WasManaged,
                selection.UsedPassFallback,
                selection.UsedEmergencyFallback);
        }

        internal ShaderVariantStripSelection SelectCandidates(
            string shaderIdentity,
            PassType passType,
            params string[] candidateSignatures)
        {
            if (!IsEnabled)
                return new ShaderVariantStripSelection(
                    Enumerable.Repeat(true, candidateSignatures?.Length ?? 0).ToArray(),
                    false,
                    false,
                    false);
            return m_Index.SelectCandidates(
                shaderIdentity,
                passType,
                candidateSignatures ?? Array.Empty<string>());
        }

        private static void ValidateAndAppendModule(
            string moduleName,
            string profileName,
            UnityEditor.BuildTarget buildTarget,
            ISet<string> buildAssetPaths,
            ICollection<ShaderVariantStripEntry> combinedEntries)
        {
            string manifestPath = ShaderVariantPrewarmPaths.GetManifestAssetPath(
                moduleName,
                profileName);
            string collectionPath = ShaderVariantPrewarmPaths.GetCollectionAssetPath(
                moduleName,
                profileName);
            if (!buildAssetPaths.Contains(manifestPath) || !buildAssetPaths.Contains(collectionPath))
                throw new InvalidOperationException(
                    $"模块 {moduleName} 配置档 {profileName} 的预热 Manifest/SVC 未进入本次构建，" +
                    "GeneratedAllowlist 剔除已中止。");

            ShaderVariantPrewarmManifest runtimeManifest =
                AssetDatabase.LoadAssetAtPath<ShaderVariantPrewarmManifest>(manifestPath);
            ShaderVariantCollection collection =
                AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(collectionPath);
            if (runtimeManifest == null || collection == null)
                throw new InvalidOperationException(
                    $"模块 {moduleName} 配置档 {profileName} 的预热资源无法加载。");
            if (runtimeManifest.SchemaVersion != ShaderVariantPrewarmManifest.CurrentSchemaVersion)
                throw new InvalidOperationException(
                    $"模块 {moduleName} 的预热 Manifest 版本不受支持：{runtimeManifest.SchemaVersion}。");
            if (!string.Equals(runtimeManifest.ModuleName, moduleName, StringComparison.Ordinal) ||
                !string.Equals(runtimeManifest.ProfileName, profileName, StringComparison.Ordinal) ||
                !string.Equals(runtimeManifest.BuildTarget, buildTarget.ToString(), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"模块 {moduleName} 配置档 {profileName} 的预热 Manifest 身份或平台不匹配。");
            if (!ReferenceEquals(runtimeManifest.Collection, collection))
                throw new InvalidOperationException(
                    $"模块 {moduleName} 的预热 Manifest 未引用预期 SVC：{collectionPath}");

            ShaderVariantStripManifest stripManifest =
                ShaderVariantStripManifestStore.LoadRequired(moduleName, profileName);
            ValidateStripManifest(
                moduleName,
                profileName,
                buildTarget,
                collectionPath,
                runtimeManifest,
                collection,
                stripManifest);
            foreach (ShaderVariantStripEntry entry in stripManifest.variants)
            {
                if (entry != null) combinedEntries.Add(entry);
            }
        }

        private static void ValidateStripManifest(
            string moduleName,
            string profileName,
            UnityEditor.BuildTarget buildTarget,
            string collectionPath,
            ShaderVariantPrewarmManifest runtimeManifest,
            ShaderVariantCollection collection,
            ShaderVariantStripManifest stripManifest)
        {
            if (stripManifest.schemaVersion != ShaderVariantStripManifest.CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"模块 {moduleName} 的 Shader 剔除 allowlist 版本不受支持：" +
                    stripManifest.schemaVersion);
            if (!string.Equals(stripManifest.moduleName, moduleName, StringComparison.Ordinal) ||
                !string.Equals(stripManifest.profileName, profileName, StringComparison.Ordinal) ||
                !string.Equals(stripManifest.buildTarget, buildTarget.ToString(), StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"模块 {moduleName} 配置档 {profileName} 的 Shader 剔除 allowlist 身份或平台不匹配。");
            if (!string.Equals(stripManifest.unityVersion, Application.unityVersion, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Shader 剔除 allowlist 基于 Unity {stripManifest.unityVersion} 生成，" +
                    $"当前版本为 {Application.unityVersion}。请重新审计并生成 SVC。");
            if (!string.Equals(
                    stripManifest.sourceManifestHash,
                    runtimeManifest.SourceManifestHash,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    stripManifest.collectionContentHash,
                    runtimeManifest.CollectionContentHash,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"模块 {moduleName} 的 Shader 剔除 allowlist 与预热 Manifest 哈希不一致。" +
                    "请重新生成 SVC。");
            if (collection.variantCount != runtimeManifest.VariantCount ||
                collection.shaderCount != runtimeManifest.ShaderCount ||
                stripManifest.variants.Length != collection.variantCount)
                throw new InvalidDataException(
                    $"模块 {moduleName} 的 SVC 计数与生成清单不一致。请重新生成 SVC。");

            string currentCollectionHash = AssetDatabase.GetAssetDependencyHash(collectionPath).ToString();
            if (!string.Equals(
                    currentCollectionHash,
                    stripManifest.collectionAssetHash,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"模块 {moduleName} 的 SVC 资产在生成 allowlist 后发生变化。请重新生成 SVC。");
            string currentShaderDependencyHash =
                ShaderVariantStripManifestStore.ComputeShaderDependencyHash(stripManifest.variants);
            if (!string.Equals(
                    currentShaderDependencyHash,
                    stripManifest.shaderDependencyHash,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"模块 {moduleName} 的 Shader 源码或依赖在生成 allowlist 后发生变化。" +
                    "请重新完成审计构建并生成 SVC。");
        }

        private static string GetShaderIdentity(Shader shader)
        {
            string path = NormalizeAssetPath(AssetDatabase.GetAssetPath(shader));
            string guid = string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrWhiteSpace(guid) ? shader.name ?? string.Empty : guid;
        }

        private static string GetKeywordSignature(ShaderCompilerData compilerData)
        {
            ShaderKeyword[] keywords = compilerData.shaderKeywordSet.GetShaderKeywords();
            return ShaderVariantAllowlistIndex.BuildKeywordSignature(
                keywords.Select(keyword => keyword.name));
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/');
        }
    }
}
