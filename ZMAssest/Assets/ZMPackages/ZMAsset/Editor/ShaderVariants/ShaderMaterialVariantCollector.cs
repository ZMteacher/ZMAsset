using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZM.Asset
{
    internal sealed class ShaderMaterialCollectionResult
    {
        internal readonly List<ShaderVariantAuditEntry> Entries = new List<ShaderVariantAuditEntry>();
        internal readonly List<string> Warnings = new List<string>();
        internal int RootAssetCount;
        internal int MaterialAssetCount;
    }

    /// <summary>
    /// Collects material keyword state from the exact roots submitted to BuildPipeline.
    /// The collector records observations only; it never mutates materials or generated variant assets.
    /// </summary>
    internal static class ShaderMaterialVariantCollector
    {
        internal static ShaderMaterialCollectionResult Collect(
            IEnumerable<AssetBundleBuild> bundleBuilds,
            UnityEditor.BuildTarget buildTarget,
            IReadOnlyDictionary<string, string> bundleOwners)
        {
            ShaderMaterialCollectionResult result = new ShaderMaterialCollectionResult();
            AssetBundleBuild[] buildSnapshot = bundleBuilds?.ToArray() ?? Array.Empty<AssetBundleBuild>();
            Dictionary<string, HashSet<string>> rootAssetsByModule =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            HashSet<string> allRootAssets = new HashSet<string>(StringComparer.Ordinal);
            foreach (AssetBundleBuild build in buildSnapshot)
            {
                string moduleName = ResolveModuleName(build.assetBundleName, bundleOwners);
                if (!rootAssetsByModule.TryGetValue(moduleName, out HashSet<string> moduleRoots))
                {
                    moduleRoots = new HashSet<string>(StringComparer.Ordinal);
                    rootAssetsByModule.Add(moduleName, moduleRoots);
                }

                foreach (string assetPath in build.assetNames ?? Array.Empty<string>())
                {
                    string normalizedPath = NormalizeAssetPath(assetPath);
                    if (string.IsNullOrWhiteSpace(normalizedPath)) continue;
                    moduleRoots.Add(normalizedPath);
                    allRootAssets.Add(normalizedPath);
                }
            }
            result.RootAssetCount = allRootAssets.Count;
            if (allRootAssets.Count == 0) return result;

            HashSet<string> materialAssetPaths = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, MaterialObservationAccumulator> observations =
                new Dictionary<string, MaterialObservationAccumulator>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, HashSet<string>> moduleRoots in rootAssetsByModule
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (moduleRoots.Value.Count == 0) continue;

                string[] dependencyPaths;
                try
                {
                    dependencyPaths = AssetDatabase.GetDependencies(
                        moduleRoots.Value.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                        true);
                }
                catch (Exception exception)
                {
                    result.Warnings.Add(
                        $"模块“{moduleRoots.Key}”的静态材质依赖扫描失败：{exception.Message}");
                    continue;
                }

                foreach (string dependencyPath in dependencyPaths
                             .Where(path => path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                             .Select(NormalizeAssetPath)
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    Material material = AssetDatabase.LoadAssetAtPath<Material>(dependencyPath);
                    if (material == null)
                    {
                        result.Warnings.Add($"无法加载材质依赖：{dependencyPath}");
                        continue;
                    }

                    materialAssetPaths.Add(dependencyPath);
                    if (material.shader == null)
                    {
                        result.Warnings.Add(
                            $"材质没有有效 Shader，无法生成变体观察记录：{dependencyPath}");
                        continue;
                    }

                    ShaderVariantAuditEntry entry = CreateMaterialEntry(
                        material,
                        dependencyPath,
                        moduleRoots.Key,
                        buildTarget);
                    if (!observations.TryGetValue(
                            entry.stableKey,
                            out MaterialObservationAccumulator accumulator))
                    {
                        accumulator = new MaterialObservationAccumulator(entry);
                        observations.Add(entry.stableKey, accumulator);
                    }
                    accumulator.AddSource(dependencyPath);
                }
            }

            result.MaterialAssetCount = materialAssetPaths.Count;
            result.Entries.AddRange(observations.Values
                .Select(observation => observation.ToEntry())
                .OrderBy(entry => entry.stableKey, StringComparer.Ordinal));
            return result;
        }

        private static ShaderVariantAuditEntry CreateMaterialEntry(
            Material material,
            string materialPath,
            string moduleName,
            UnityEditor.BuildTarget buildTarget)
        {
            Shader shader = material.shader;
            string shaderPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(shader));
            string shaderGuid = string.IsNullOrWhiteSpace(shaderPath)
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(shaderPath);
            ShaderVariantAuditEntry entry = new ShaderVariantAuditEntry
            {
                sourceKind = ShaderVariantAuditSourceKind.Material,
                shaderName = shader.name ?? string.Empty,
                shaderGuid = shaderGuid ?? string.Empty,
                shaderAssetPath = shaderPath,
                moduleName = moduleName,
                profileName = string.Empty,
                passName = ShaderVariantAuditSchema.MaterialPass,
                passType = ShaderVariantAuditSchema.UnknownValue,
                shaderStage = ShaderVariantAuditStage.Any.ToString(),
                buildTarget = buildTarget.ToString(),
                compilerPlatform = ShaderVariantAuditSchema.UnknownValue,
                graphicsTier = ShaderVariantAuditSchema.UnknownValue,
                keywords = ShaderVariantKeywordUtility.Normalize(material.shaderKeywords),
                sourceAssets = new[] { materialPath },
                reason = "材质依赖中序列化保存的 Keyword 状态。",
                occurrences = 1
            };
            entry.stableKey = ShaderVariantKeywordUtility.BuildStableKey(entry);
            return entry;
        }

        private static string ResolveModuleName(
            string bundleName,
            IReadOnlyDictionary<string, string> bundleOwners)
        {
            if (!string.IsNullOrWhiteSpace(bundleName) &&
                bundleOwners != null &&
                bundleOwners.TryGetValue(bundleName, out string moduleName) &&
                !string.IsNullOrWhiteSpace(moduleName))
            {
                return moduleName.Trim();
            }

            return "<未分配模块>";
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/');
        }

        private sealed class MaterialObservationAccumulator
        {
            private readonly ShaderVariantAuditEntry m_Entry;
            private readonly SortedSet<string> m_SourceAssets = new SortedSet<string>(StringComparer.Ordinal);

            internal MaterialObservationAccumulator(ShaderVariantAuditEntry entry)
            {
                m_Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            }

            internal void AddSource(string assetPath)
            {
                if (!string.IsNullOrWhiteSpace(assetPath)) m_SourceAssets.Add(NormalizeAssetPath(assetPath));
            }

            internal ShaderVariantAuditEntry ToEntry()
            {
                ShaderVariantAuditEntry result = m_Entry.Clone();
                result.sourceAssets = m_SourceAssets.ToArray();
                result.occurrences = m_SourceAssets.Count;
                return result;
            }
        }
    }
}
