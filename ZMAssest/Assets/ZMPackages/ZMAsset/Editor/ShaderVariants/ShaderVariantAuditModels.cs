using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ZM.Asset
{
    internal static class ShaderVariantAuditSchema
    {
        internal const int Version = 4;
        internal const string MaterialPass = "<material-state>";
        internal const string UnknownValue = "Unknown";
    }

    internal enum ShaderVariantAuditSourceKind
    {
        Material = 0,
        ExplicitRule = 1,
        BuildCandidateBeforeProcessing = 2,
        BuildCandidateAfterProcessing = 3
    }

    /// <summary>
    /// Serializable normalized representation shared by material observations, explicit rules and compiler candidates.
    /// Public fields are intentional because Unity JsonUtility serializes fields rather than properties.
    /// </summary>
    [Serializable]
    internal sealed class ShaderVariantAuditEntry
    {
        public string stableKey;
        public ShaderVariantAuditSourceKind sourceKind;
        public string shaderName;
        public string shaderGuid;
        public string shaderAssetPath;
        public string moduleName;
        public string profileName;
        public string passName;
        public string passType;
        public string shaderStage;
        public string buildTarget;
        public string compilerPlatform;
        public string graphicsTier;
        public string[] keywords = Array.Empty<string>();
        public string[] sourceAssets = Array.Empty<string>();
        public string reason;
        public int occurrences = 1;

        internal ShaderVariantAuditEntry Clone()
        {
            return new ShaderVariantAuditEntry
            {
                stableKey = stableKey,
                sourceKind = sourceKind,
                shaderName = shaderName,
                shaderGuid = shaderGuid,
                shaderAssetPath = shaderAssetPath,
                moduleName = moduleName,
                profileName = profileName,
                passName = passName,
                passType = passType,
                shaderStage = shaderStage,
                buildTarget = buildTarget,
                compilerPlatform = compilerPlatform,
                graphicsTier = graphicsTier,
                keywords = keywords?.ToArray() ?? Array.Empty<string>(),
                sourceAssets = sourceAssets?.ToArray() ?? Array.Empty<string>(),
                reason = reason,
                occurrences = occurrences
            };
        }
    }

    [Serializable]
    internal sealed class ShaderVariantAuditShaderSummary
    {
        public string shaderName;
        public string shaderGuid;
        public string shaderAssetPath;
        public int materialStates;
        public int explicitRules;
        public int candidatesBeforeProcessing;
        public int candidatesAfterProcessing;
        public int candidatesAfterStripping;
    }

    [Serializable]
    internal sealed class ShaderVariantAuditPrewarmCollectionSummary
    {
        public string moduleName;
        public string profileName;
        public string buildTarget;
        public string bundleName;
        public string manifestAssetPath;
        public string collectionAssetPath;
        public string sourceManifestHash;
        public string collectionContentHash;
        public int shaderCount;
        public int variantCount;
    }

    [Serializable]
    internal sealed class ShaderVariantCanonicalManifest
    {
        public int schemaVersion = ShaderVariantAuditSchema.Version;
        public string contextName;
        public string buildTarget;
        public string contentHash;
        public ShaderVariantAuditEntry[] entries = Array.Empty<ShaderVariantAuditEntry>();
    }

    [Serializable]
    internal sealed class ShaderVariantAuditReport
    {
        public int schemaVersion = ShaderVariantAuditSchema.Version;
        public string contextName;
        public string[] moduleNames = Array.Empty<string>();
        public string activeProfileName;
        public string buildTarget;
        public string unityVersion;
        public string startedUtc;
        public string completedUtc;
        public bool buildSucceeded;
        public bool reportIsComplete = true;
        public int detailedVariantLimit;
        public int bundleCount;
        public int rootAssetCount;
        public int materialAssetCount;
        public int explicitRuleCount;
        public int prewarmCollectionCount;
        public int prewarmShaderCount;
        public int prewarmVariantCount;
        public string strippingMode;
        public int strippingAllowlistVariantCount;
        public int strippingManagedShaderCount;
        public long candidateCountBeforeProcessing;
        public long candidateCountAfterProcessing;
        public long candidateCountAfterStripping;
        public long strippedCandidateCount;
        public long strippingManagedSnippetCount;
        public long strippingUnmanagedSnippetCount;
        public long strippingPassFallbackSnippetCount;
        public long strippingEmergencyFallbackSnippetCount;
        public int detailedUniqueBeforeCount;
        public int detailedUniqueAfterCount;
        public bool beforeDetailsTruncated;
        public bool afterDetailsTruncated;
        public string canonicalManifestHash;
        public string canonicalManifestPath;
        public string reportJsonPath;
        public string reportMarkdownPath;
        public ShaderVariantAuditEntry[] materialVariants = Array.Empty<ShaderVariantAuditEntry>();
        public ShaderVariantAuditEntry[] explicitVariants = Array.Empty<ShaderVariantAuditEntry>();
        public ShaderVariantAuditEntry[] candidatesBeforeProcessing = Array.Empty<ShaderVariantAuditEntry>();
        public ShaderVariantAuditEntry[] candidatesAfterProcessing = Array.Empty<ShaderVariantAuditEntry>();
        public ShaderVariantAuditShaderSummary[] shaderSummaries = Array.Empty<ShaderVariantAuditShaderSummary>();
        public ShaderVariantAuditPrewarmCollectionSummary[] prewarmCollections =
            Array.Empty<ShaderVariantAuditPrewarmCollectionSummary>();
        public string[] warnings = Array.Empty<string>();
    }

    internal static class ShaderVariantKeywordUtility
    {
        private static readonly char[] KeywordSeparators =
        {
            ' ', '\t', '\r', '\n', ',', ';'
        };

        internal static string[] Parse(string keywordText)
        {
            return string.IsNullOrWhiteSpace(keywordText)
                ? Array.Empty<string>()
                : Normalize(keywordText.Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries));
        }

        internal static string[] Normalize(IEnumerable<string> keywords)
        {
            if (keywords == null) return Array.Empty<string>();

            return keywords
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
                .Select(keyword => keyword.Trim())
                .Where(keyword => !string.Equals(keyword, "_", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(keyword => keyword, StringComparer.Ordinal)
                .ToArray();
        }

        internal static string BuildStableKey(ShaderVariantAuditEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            string shaderIdentity = !string.IsNullOrWhiteSpace(entry.shaderGuid)
                ? entry.shaderGuid.Trim()
                : entry.shaderName?.Trim() ?? string.Empty;
            string[] normalizedKeywords = Normalize(entry.keywords);

            return string.Join(
                "\u001f",
                ((int)entry.sourceKind).ToString(),
                shaderIdentity,
                entry.moduleName?.Trim() ?? string.Empty,
                entry.profileName?.Trim() ?? string.Empty,
                entry.passName?.Trim() ?? string.Empty,
                entry.passType?.Trim() ?? string.Empty,
                entry.shaderStage?.Trim() ?? string.Empty,
                entry.buildTarget?.Trim() ?? string.Empty,
                entry.compilerPlatform?.Trim() ?? string.Empty,
                entry.graphicsTier?.Trim() ?? string.Empty,
                string.Join("\u001e", normalizedKeywords));
        }
    }

    internal static class ShaderVariantCanonicalManifestBuilder
    {
        internal static ShaderVariantCanonicalManifest Build(
            string contextName,
            string buildTarget,
            IEnumerable<ShaderVariantAuditEntry> entries)
        {
            ShaderVariantAuditEntry[] normalizedEntries = (entries ?? Array.Empty<ShaderVariantAuditEntry>())
                .Where(entry => entry != null)
                .Select(entry =>
                {
                    ShaderVariantAuditEntry copy = entry.Clone();
                    copy.keywords = ShaderVariantKeywordUtility.Normalize(copy.keywords);
                    copy.sourceAssets = (copy.sourceAssets ?? Array.Empty<string>())
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => path.Trim().Replace('\\', '/'))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray();
                    copy.stableKey = ShaderVariantKeywordUtility.BuildStableKey(copy);
                    return copy;
                })
                .GroupBy(entry => entry.stableKey, StringComparer.Ordinal)
                .Select(group => MergeEquivalentEntries(group))
                .OrderBy(entry => entry.stableKey, StringComparer.Ordinal)
                .ToArray();

            return new ShaderVariantCanonicalManifest
            {
                contextName = contextName ?? string.Empty,
                buildTarget = buildTarget ?? string.Empty,
                contentHash = ComputeContentHash(normalizedEntries),
                entries = normalizedEntries
            };
        }

        internal static string ComputeContentHash(IEnumerable<ShaderVariantAuditEntry> entries)
        {
            string canonicalText = string.Join(
                "\n",
                (entries ?? Array.Empty<ShaderVariantAuditEntry>())
                    .Where(entry => entry != null)
                    .Select(entry => entry.stableKey ?? ShaderVariantKeywordUtility.BuildStableKey(entry))
                    .OrderBy(key => key, StringComparer.Ordinal));

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalText));
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        private static ShaderVariantAuditEntry MergeEquivalentEntries(
            IEnumerable<ShaderVariantAuditEntry> equivalentEntries)
        {
            ShaderVariantAuditEntry[] entries = equivalentEntries.ToArray();
            ShaderVariantAuditEntry merged = entries[0].Clone();
            merged.occurrences = entries.Sum(entry => Math.Max(1, entry.occurrences));
            merged.sourceAssets = entries
                .SelectMany(entry => entry.sourceAssets ?? Array.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            return merged;
        }
    }
}
