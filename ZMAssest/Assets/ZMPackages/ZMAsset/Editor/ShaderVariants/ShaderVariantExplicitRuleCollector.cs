using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// Resolves explicit rules into the same normalized entries used by build auditing and SVC freshness checks.
    /// </summary>
    internal static class ShaderVariantExplicitRuleCollector
    {
        internal static List<ShaderVariantAuditEntry> Collect(
            IReadOnlyList<ShaderVariantExplicitRuleSnapshot> rules,
            UnityEditor.BuildTarget buildTarget,
            ICollection<string> warnings)
        {
            List<ShaderVariantAuditEntry> entries = new List<ShaderVariantAuditEntry>();
            IReadOnlyList<ShaderVariantExplicitRuleSnapshot> safeRules =
                rules ?? Array.Empty<ShaderVariantExplicitRuleSnapshot>();
            for (int index = 0; index < safeRules.Count; index++)
            {
                ShaderVariantExplicitRuleSnapshot rule = safeRules[index];
                if (rule == null || !rule.IsEnabled) continue;

                string shaderPath = string.IsNullOrWhiteSpace(rule.ShaderGuid)
                    ? string.Empty
                    : NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(rule.ShaderGuid));
                Shader shader = !string.IsNullOrWhiteSpace(shaderPath)
                    ? AssetDatabase.LoadAssetAtPath<Shader>(shaderPath)
                    : null;
                if (shader == null && !string.IsNullOrWhiteSpace(rule.BuiltInShaderName))
                    shader = Shader.Find(rule.BuiltInShaderName);

                if (shader == null)
                {
                    warnings?.Add(
                        $"显式 Shader 规则 {index + 1} 无法解析 GUID“{rule.ShaderGuid}”" +
                        $"或内置名称“{rule.BuiltInShaderName}”。");
                    continue;
                }

                string resolvedPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(shader));
                string resolvedGuid = string.IsNullOrWhiteSpace(resolvedPath)
                    ? rule.ShaderGuid
                    : AssetDatabase.AssetPathToGUID(resolvedPath);
                ShaderVariantAuditEntry entry = new ShaderVariantAuditEntry
                {
                    sourceKind = ShaderVariantAuditSourceKind.ExplicitRule,
                    shaderName = shader.name ?? rule.BuiltInShaderName,
                    shaderGuid = resolvedGuid ?? string.Empty,
                    shaderAssetPath = resolvedPath,
                    moduleName = rule.ModuleName,
                    profileName = rule.ProfileName,
                    passName = rule.PassName,
                    passType = rule.PassType.ToString(),
                    shaderStage = rule.ShaderStage.ToString(),
                    buildTarget = buildTarget.ToString(),
                    compilerPlatform = ShaderVariantAuditSchema.UnknownValue,
                    graphicsTier = ShaderVariantAuditSchema.UnknownValue,
                    keywords = ShaderVariantKeywordUtility.Normalize(rule.Keywords),
                    sourceAssets = new[] { $"ExplicitRule[{index}]" },
                    reason = rule.Reason,
                    occurrences = 1
                };
                entry.stableKey = ShaderVariantKeywordUtility.BuildStableKey(entry);
                entries.Add(entry);
            }

            return ShaderVariantCanonicalManifestBuilder
                .Build("ExplicitRules", buildTarget.ToString(), entries)
                .entries
                .ToList();
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/');
        }
    }
}
