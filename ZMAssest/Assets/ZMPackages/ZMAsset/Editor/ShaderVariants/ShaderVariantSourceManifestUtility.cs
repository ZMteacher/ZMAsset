using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace ZM.Asset
{
    /// <summary>
    /// Computes the deterministic material-and-rule source hash used to reject stale generated collections.
    /// Compiler candidates are produced by the audit build; current source ownership can be checked before the
    /// next production build without invoking Unity's compiler a second time.
    /// </summary>
    internal static class ShaderVariantSourceManifestUtility
    {
        internal static string ComputeModuleHash(
            string moduleName,
            string profileName,
            UnityEditor.BuildTarget buildTarget,
            IReadOnlyList<AssetBundleBuild> bundleBuilds,
            ShaderVariantAuditConfiguration configuration,
            ICollection<string> warnings = null)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                throw new ArgumentException("计算 Shader 来源清单时模块名不能为空。", nameof(moduleName));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            string normalizedModule = moduleName.Trim();
            string normalizedProfile = string.IsNullOrWhiteSpace(profileName)
                ? ShaderVariantPrewarmPaths.DefaultProfileName
                : profileName.Trim();
            Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetBundleBuild build in bundleBuilds ?? Array.Empty<AssetBundleBuild>())
            {
                if (!string.IsNullOrWhiteSpace(build.assetBundleName))
                    owners[build.assetBundleName] = normalizedModule;
            }

            ShaderMaterialCollectionResult materials = ShaderMaterialVariantCollector.Collect(
                bundleBuilds ?? Array.Empty<AssetBundleBuild>(),
                buildTarget,
                owners);
            foreach (string warning in materials.Warnings) warnings?.Add(warning);

            List<string> explicitWarnings = new List<string>();
            List<ShaderVariantAuditEntry> explicitEntries = ShaderVariantExplicitRuleCollector.Collect(
                configuration.ExplicitRules,
                buildTarget,
                explicitWarnings);
            foreach (string warning in explicitWarnings) warnings?.Add(warning);

            IEnumerable<ShaderVariantAuditEntry> relevantEntries = materials.Entries
                .Concat(explicitEntries)
                .Where(entry => AppliesToModuleAndProfile(
                    entry,
                    normalizedModule,
                    normalizedProfile));
            return ShaderVariantCanonicalManifestBuilder.Build(
                    normalizedModule,
                    buildTarget.ToString(),
                    relevantEntries)
                .contentHash;
        }

        internal static bool AppliesToModuleAndProfile(
            ShaderVariantAuditEntry entry,
            string moduleName,
            string profileName)
        {
            if (entry == null) return false;
            bool moduleMatches = string.IsNullOrWhiteSpace(entry.moduleName) ||
                                 string.Equals(entry.moduleName.Trim(), moduleName, StringComparison.Ordinal);
            bool profileMatches = string.IsNullOrWhiteSpace(entry.profileName) ||
                                  string.Equals(entry.profileName.Trim(), profileName, StringComparison.Ordinal);
            return moduleMatches && profileMatches;
        }
    }
}
