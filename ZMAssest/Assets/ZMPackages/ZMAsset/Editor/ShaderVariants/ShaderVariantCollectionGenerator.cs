using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZM.Asset
{
    internal sealed class ShaderVariantCollectionGenerationResult
    {
        internal readonly List<string> ManifestAssetPaths = new List<string>();
        internal readonly List<string> StripManifestPaths = new List<string>();
        internal readonly List<string> Warnings = new List<string>();
        internal int ModuleCount;
        internal int ShaderCount;
        internal int VariantCount;
    }

    /// <summary>
    /// Converts a complete post-processing audit report into deterministic module-scoped SVC assets.
    /// Generated content is validated in memory before an existing generated asset is replaced.
    /// </summary>
    internal static class ShaderVariantCollectionGenerator
    {
        internal static ShaderVariantCollectionGenerationResult GenerateLatest(
            string configuredReportDirectory,
            string profileName,
            UnityEditor.BuildTarget buildTarget)
        {
            if (!ShaderVariantAuditArtifactLocator.TryFindLatest(
                    configuredReportDirectory,
                    out ShaderVariantAuditArtifactLocator.Result artifacts,
                    out string locatorError))
                throw new InvalidOperationException(locatorError);
            if (artifacts == null || string.IsNullOrWhiteSpace(artifacts.ReportJsonPath) ||
                !File.Exists(artifacts.ReportJsonPath))
                throw new FileNotFoundException("找不到可用于生成 SVC 的 Shader 审计 JSON 报告。");

            ShaderVariantAuditReport report = JsonUtility.FromJson<ShaderVariantAuditReport>(
                File.ReadAllText(artifacts.ReportJsonPath));
            return Generate(report, profileName, buildTarget);
        }

        internal static ShaderVariantCollectionGenerationResult Generate(
            ShaderVariantAuditReport report,
            string profileName,
            UnityEditor.BuildTarget buildTarget)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            string normalizedProfile = string.IsNullOrWhiteSpace(profileName)
                ? ShaderVariantPrewarmPaths.DefaultProfileName
                : profileName.Trim();
            if (!report.buildSucceeded)
                throw new InvalidOperationException("最近 Shader 审计对应的 AssetBundle 构建未成功，拒绝生成预热集合。");
            if (!report.reportIsComplete)
                throw new InvalidOperationException("最近 Shader 审计详细候选已截断，请提高详细项上限并重新构建。");
            if (!string.Equals(report.buildTarget, buildTarget.ToString(), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Shader 审计目标为 {report.buildTarget}，当前目标为 {buildTarget}。请切换目标并重新审计。");
            if (!string.IsNullOrWhiteSpace(report.activeProfileName) &&
                !string.Equals(report.activeProfileName, normalizedProfile, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Shader 审计配置档为 {report.activeProfileName}，当前配置档为 {normalizedProfile}。请重新审计。");

            string[] modules = (report.moduleNames ?? Array.Empty<string>())
                .Where(module => !string.IsNullOrWhiteSpace(module))
                .Select(module => module.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(module => module, StringComparer.Ordinal)
                .ToArray();
            if (modules.Length == 0)
                throw new InvalidOperationException("Shader 审计报告没有模块信息，无法生成模块级预热集合。");

            ShaderVariantCollectionGenerationResult result =
                new ShaderVariantCollectionGenerationResult();
            foreach (string module in modules)
                GenerateModule(report, modules.Length, module, normalizedProfile, result);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return result;
        }

        private static void GenerateModule(
            ShaderVariantAuditReport report,
            int reportModuleCount,
            string moduleName,
            string profileName,
            ShaderVariantCollectionGenerationResult result)
        {
            ShaderVariantAuditEntry[] materialEntries = FilterSourceEntries(
                report.materialVariants,
                moduleName,
                profileName,
                reportModuleCount);
            ShaderVariantAuditEntry[] explicitEntries = FilterSourceEntries(
                report.explicitVariants,
                moduleName,
                profileName,
                reportModuleCount);
            ShaderVariantAuditEntry[] candidateEntries = FilterCandidateEntries(
                report.candidatesAfterProcessing,
                moduleName,
                profileName,
                reportModuleCount);
            ShaderVariantAuditEntry[] collectionEntries = candidateEntries
                .Concat(explicitEntries)
                .ToArray();

            string sourceManifestHash = ShaderVariantCanonicalManifestBuilder.Build(
                    moduleName,
                    report.buildTarget,
                    materialEntries.Concat(explicitEntries))
                .contentHash;
            ShaderVariantCanonicalManifest collectionManifest = ShaderVariantCanonicalManifestBuilder.Build(
                moduleName + "/" + profileName,
                report.buildTarget,
                collectionEntries);

            List<ShaderVariantCollection.ShaderVariant> validatedVariants =
                BuildValidatedVariants(
                    collectionManifest.entries,
                    result.Warnings,
                    out ShaderVariantAuditEntry[] validatedEntries);
            string validatedCollectionContentHash = ShaderVariantCanonicalManifestBuilder.Build(
                    moduleName + "/" + profileName,
                    report.buildTarget,
                    validatedEntries)
                .contentHash;
            string directory = ShaderVariantPrewarmPaths.GetProfileDirectory(moduleName, profileName);
            EnsureAssetFolder(directory);
            string collectionPath = ShaderVariantPrewarmPaths.GetCollectionAssetPath(moduleName, profileName);
            string manifestPath = ShaderVariantPrewarmPaths.GetManifestAssetPath(moduleName, profileName);

            ShaderVariantCollection collection =
                AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(collectionPath);
            if (collection == null)
            {
                collection = new ShaderVariantCollection();
                AssetDatabase.CreateAsset(collection, collectionPath);
            }
            collection.Clear();
            foreach (ShaderVariantCollection.ShaderVariant variant in validatedVariants)
                collection.Add(variant);
            EditorUtility.SetDirty(collection);
            AssetDatabase.SaveAssetIfDirty(collection);
            string collectionAssetHash = AssetDatabase.GetAssetDependencyHash(collectionPath).ToString();

            ShaderVariantPrewarmManifest manifest =
                AssetDatabase.LoadAssetAtPath<ShaderVariantPrewarmManifest>(manifestPath);
            if (manifest == null)
            {
                manifest = ScriptableObject.CreateInstance<ShaderVariantPrewarmManifest>();
                AssetDatabase.CreateAsset(manifest, manifestPath);
            }
            manifest.Configure(
                moduleName,
                profileName,
                report.buildTarget,
                sourceManifestHash,
                validatedCollectionContentHash,
                collection);
            EditorUtility.SetDirty(manifest);

            string stripManifestPath = ShaderVariantStripManifestStore.Write(
                moduleName,
                profileName,
                report.buildTarget,
                string.IsNullOrWhiteSpace(report.unityVersion)
                    ? Application.unityVersion
                    : report.unityVersion,
                sourceManifestHash,
                validatedCollectionContentHash,
                collectionAssetHash,
                validatedEntries);

            result.ManifestAssetPaths.Add(manifestPath);
            result.StripManifestPaths.Add(stripManifestPath);
            result.ModuleCount++;
            result.ShaderCount += collection.shaderCount;
            result.VariantCount += collection.variantCount;
        }

        private static List<ShaderVariantCollection.ShaderVariant> BuildValidatedVariants(
            IEnumerable<ShaderVariantAuditEntry> entries,
            ICollection<string> warnings,
            out ShaderVariantAuditEntry[] validatedEntries)
        {
            ShaderVariantCollection validationCollection = new ShaderVariantCollection();
            List<ShaderVariantCollection.ShaderVariant> variants =
                new List<ShaderVariantCollection.ShaderVariant>();
            List<ShaderVariantAuditEntry> acceptedEntries = new List<ShaderVariantAuditEntry>();
            try
            {
                foreach (ShaderVariantAuditEntry entry in entries ?? Array.Empty<ShaderVariantAuditEntry>())
                {
                    if (entry == null) continue;
                    Shader shader = ResolveShader(entry);
                    if (shader == null)
                    {
                        warnings?.Add($"无法解析 Shader，已跳过预热变体：{entry.shaderName} ({entry.shaderGuid})");
                        continue;
                    }
                    if (!Enum.TryParse(entry.passType, true, out PassType passType))
                    {
                        warnings?.Add($"Shader {shader.name} 的 PassType 无效，已跳过：{entry.passType}");
                        continue;
                    }

                    try
                    {
                        ShaderVariantCollection.ShaderVariant variant =
                            new ShaderVariantCollection.ShaderVariant(
                                shader,
                                passType,
                                ShaderVariantKeywordUtility.Normalize(entry.keywords));
                        if (validationCollection.Add(variant))
                        {
                            variants.Add(variant);
                            acceptedEntries.Add(entry);
                        }
                    }
                    catch (Exception exception)
                    {
                        warnings?.Add(
                            $"Shader {shader.name} 变体无效，已跳过：{passType} " +
                            $"[{string.Join(", ", entry.keywords ?? Array.Empty<string>())}]；{exception.Message}");
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(validationCollection);
            }
            validatedEntries = acceptedEntries.ToArray();
            return variants;
        }

        private static Shader ResolveShader(ShaderVariantAuditEntry entry)
        {
            string assetPath = !string.IsNullOrWhiteSpace(entry.shaderGuid)
                ? AssetDatabase.GUIDToAssetPath(entry.shaderGuid)
                : entry.shaderAssetPath;
            Shader shader = string.IsNullOrWhiteSpace(assetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
            return shader != null || string.IsNullOrWhiteSpace(entry.shaderName)
                ? shader
                : Shader.Find(entry.shaderName);
        }

        private static ShaderVariantAuditEntry[] FilterSourceEntries(
            IEnumerable<ShaderVariantAuditEntry> entries,
            string moduleName,
            string profileName,
            int reportModuleCount)
        {
            return (entries ?? Array.Empty<ShaderVariantAuditEntry>())
                .Where(entry => EntryApplies(entry, moduleName, profileName, reportModuleCount, true))
                .ToArray();
        }

        private static ShaderVariantAuditEntry[] FilterCandidateEntries(
            IEnumerable<ShaderVariantAuditEntry> entries,
            string moduleName,
            string profileName,
            int reportModuleCount)
        {
            return (entries ?? Array.Empty<ShaderVariantAuditEntry>())
                .Where(entry => EntryApplies(entry, moduleName, profileName, reportModuleCount, false))
                .ToArray();
        }

        private static bool EntryApplies(
            ShaderVariantAuditEntry entry,
            string moduleName,
            string profileName,
            int reportModuleCount,
            bool allowGlobalModule)
        {
            if (entry == null) return false;
            bool moduleMatches = string.Equals(entry.moduleName, moduleName, StringComparison.Ordinal) ||
                                 (string.IsNullOrWhiteSpace(entry.moduleName) &&
                                  (allowGlobalModule || reportModuleCount == 1));
            bool profileMatches = string.IsNullOrWhiteSpace(entry.profileName) ||
                                  string.Equals(entry.profileName, profileName, StringComparison.Ordinal);
            return moduleMatches && profileMatches;
        }

        private static void EnsureAssetFolder(string assetFolder)
        {
            string normalized = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException($"生成目录必须位于 Assets：{assetFolder}");
            string[] segments = normalized.Split('/');
            string current = segments[0];
            for (int index = 1; index < segments.Length; index++)
            {
                string next = current + "/" + segments[index];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segments[index]);
                current = next;
            }
        }
    }
}
