using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZM.Asset
{
    internal enum ShaderVariantStrippingMode
    {
        AuditOnly = 0,
        GeneratedAllowlist = 1
    }

    /// <summary>
    /// Describes the shader stage covered by an explicit audit rule.
    /// Any is intentionally the default because material state alone cannot identify a compiler stage.
    /// </summary>
    internal enum ShaderVariantAuditStage
    {
        Any = 0,
        Vertex = 1,
        Fragment = 2,
        Geometry = 3,
        Hull = 4,
        Domain = 5,
        RayTracing = 6
    }

    /// <summary>
    /// Mutable editor-only copy of one explicit rule. The UI edits drafts and commits all settings atomically.
    /// </summary>
    internal sealed class ShaderVariantExplicitRuleDraft
    {
        internal bool IsEnabled { get; set; } = true;
        internal string ShaderGuid { get; set; } = string.Empty;
        internal string BuiltInShaderName { get; set; } = string.Empty;
        internal string ModuleName { get; set; } = string.Empty;
        internal string ProfileName { get; set; } = string.Empty;
        internal PassType PassType { get; set; } = PassType.Normal;
        internal string PassName { get; set; } = string.Empty;
        internal ShaderVariantAuditStage ShaderStage { get; set; } = ShaderVariantAuditStage.Any;
        internal string KeywordsText { get; set; } = string.Empty;
        internal string Reason { get; set; } = string.Empty;

        internal ShaderVariantExplicitRuleDraft()
        {
        }

        internal ShaderVariantExplicitRuleDraft(ShaderVariantExplicitRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));

            IsEnabled = rule.IsEnabled;
            ShaderGuid = rule.ShaderGuid;
            BuiltInShaderName = rule.BuiltInShaderName;
            ModuleName = rule.ModuleName;
            ProfileName = rule.ProfileName;
            PassType = rule.PassType;
            PassName = rule.PassName;
            ShaderStage = rule.ShaderStage;
            KeywordsText = string.Join(" ", rule.Keywords);
            Reason = rule.Reason;
        }
    }

    /// <summary>
    /// A reviewed variant declaration for a runtime keyword combination that static material scanning cannot discover.
    /// Shader assets are referenced by GUID so the project settings file survives asset moves and renames.
    /// </summary>
    [Serializable]
    internal sealed class ShaderVariantExplicitRule
    {
        [SerializeField] private bool m_IsEnabled = true;
        [SerializeField] private string m_ShaderGuid = string.Empty;
        [SerializeField] private string m_BuiltInShaderName = string.Empty;
        [SerializeField] private string m_ModuleName = string.Empty;
        [SerializeField] private string m_ProfileName = string.Empty;
        [SerializeField] private PassType m_PassType = PassType.Normal;
        [SerializeField] private string m_PassName = string.Empty;
        [SerializeField] private ShaderVariantAuditStage m_ShaderStage = ShaderVariantAuditStage.Any;
        [SerializeField] private string[] m_Keywords = Array.Empty<string>();
        [SerializeField] private string m_Reason = string.Empty;

        internal bool IsEnabled => m_IsEnabled;
        internal string ShaderGuid => m_ShaderGuid ?? string.Empty;
        internal string BuiltInShaderName => m_BuiltInShaderName ?? string.Empty;
        internal string ModuleName => m_ModuleName ?? string.Empty;
        internal string ProfileName => m_ProfileName ?? string.Empty;
        internal PassType PassType => m_PassType;
        internal string PassName => m_PassName ?? string.Empty;
        internal ShaderVariantAuditStage ShaderStage => m_ShaderStage;
        internal IReadOnlyList<string> Keywords => m_Keywords ?? Array.Empty<string>();
        internal string Reason => m_Reason ?? string.Empty;

        internal ShaderVariantExplicitRule(ShaderVariantExplicitRuleDraft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));

            m_IsEnabled = draft.IsEnabled;
            m_ShaderGuid = NormalizeText(draft.ShaderGuid);
            m_BuiltInShaderName = NormalizeText(draft.BuiltInShaderName);
            m_ModuleName = NormalizeText(draft.ModuleName);
            m_ProfileName = NormalizeText(draft.ProfileName);
            m_PassType = draft.PassType;
            m_PassName = NormalizeText(draft.PassName);
            m_ShaderStage = draft.ShaderStage;
            m_Keywords = ShaderVariantKeywordUtility.Parse(draft.KeywordsText);
            m_Reason = NormalizeText(draft.Reason);
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }
    }

    /// <summary>
    /// Complete editable settings snapshot used by the Build Hub page.
    /// </summary>
    internal sealed class ShaderVariantAuditEditorConfiguration
    {
        internal bool IsEnabled { get; }
        internal string ReportDirectory { get; }
        internal int MaxDetailedVariants { get; }
        internal string ActiveProfileName { get; }
        internal bool IncludeGeneratedCollections { get; }
        internal bool RequireUpToDateCollection { get; }
        internal ShaderVariantStrippingMode StrippingMode { get; }
        internal List<ShaderVariantExplicitRuleDraft> ExplicitRules { get; }

        internal ShaderVariantAuditEditorConfiguration(
            bool isEnabled,
            string reportDirectory,
            int maxDetailedVariants,
            string activeProfileName,
            bool includeGeneratedCollections,
            bool requireUpToDateCollection,
            ShaderVariantStrippingMode strippingMode,
            List<ShaderVariantExplicitRuleDraft> explicitRules)
        {
            IsEnabled = isEnabled;
            ReportDirectory = reportDirectory ?? ShaderVariantAuditSettings.DefaultReportDirectory;
            MaxDetailedVariants = maxDetailedVariants;
            ActiveProfileName = activeProfileName ?? ShaderVariantPrewarmPaths.DefaultProfileName;
            IncludeGeneratedCollections = includeGeneratedCollections;
            RequireUpToDateCollection = requireUpToDateCollection;
            StrippingMode = strippingMode;
            ExplicitRules = explicitRules ?? new List<ShaderVariantExplicitRuleDraft>();
        }
    }

    /// <summary>
    /// Project-wide settings for shader variant auditing.
    /// The settings live under ProjectSettings and are not runtime assets or AssetBundle inputs.
    /// </summary>
    [FilePath("ProjectSettings/ZMAssetShaderVariantAuditSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class ShaderVariantAuditSettings : ScriptableSingleton<ShaderVariantAuditSettings>
    {
        internal const string DefaultReportDirectory = "BuildArtifacts/ShaderVariantAudit";
        internal const int DefaultDetailedVariantLimit = 100000;
        internal const int MinimumDetailedVariantLimit = 1000;
        internal const int MaximumExplicitRuleCount = 10000;

        [SerializeField] private bool m_IsEnabled = true;
        [SerializeField] private string m_ReportDirectory = DefaultReportDirectory;
        [SerializeField] private int m_MaxDetailedVariants = DefaultDetailedVariantLimit;
        [SerializeField] private string m_ActiveProfileName = ShaderVariantPrewarmPaths.DefaultProfileName;
        [SerializeField] private bool m_IncludeGeneratedCollections = true;
        [SerializeField] private bool m_RequireUpToDateCollection;
        [SerializeField] private ShaderVariantStrippingMode m_StrippingMode =
            ShaderVariantStrippingMode.AuditOnly;
        [SerializeField] private List<ShaderVariantExplicitRule> m_ExplicitRules =
            new List<ShaderVariantExplicitRule>();

        internal ShaderVariantAuditConfiguration CreateSnapshot()
        {
            ValidateStrippingDependencies(
                m_IsEnabled,
                m_IncludeGeneratedCollections,
                m_RequireUpToDateCollection,
                m_StrippingMode);
            int detailedVariantLimit = Math.Max(MinimumDetailedVariantLimit, m_MaxDetailedVariants);
            List<ShaderVariantExplicitRuleSnapshot> explicitRules =
                new List<ShaderVariantExplicitRuleSnapshot>();

            if (m_ExplicitRules != null)
            {
                foreach (ShaderVariantExplicitRule rule in m_ExplicitRules)
                {
                    if (rule == null) continue;
                    explicitRules.Add(new ShaderVariantExplicitRuleSnapshot(rule));
                }
            }

            return new ShaderVariantAuditConfiguration(
                m_IsEnabled,
                string.IsNullOrWhiteSpace(m_ReportDirectory)
                    ? DefaultReportDirectory
                    : m_ReportDirectory.Trim(),
                detailedVariantLimit,
                NormalizeProfileName(m_ActiveProfileName),
                m_IncludeGeneratedCollections,
                m_RequireUpToDateCollection,
                explicitRules,
                m_StrippingMode);
        }

        internal ShaderVariantAuditEditorConfiguration CreateEditorConfiguration()
        {
            List<ShaderVariantExplicitRuleDraft> drafts = new List<ShaderVariantExplicitRuleDraft>();
            if (m_ExplicitRules != null)
            {
                foreach (ShaderVariantExplicitRule rule in m_ExplicitRules)
                {
                    if (rule != null) drafts.Add(new ShaderVariantExplicitRuleDraft(rule));
                }
            }

            return new ShaderVariantAuditEditorConfiguration(
                m_IsEnabled,
                string.IsNullOrWhiteSpace(m_ReportDirectory)
                    ? DefaultReportDirectory
                    : m_ReportDirectory.Trim(),
                Math.Max(MinimumDetailedVariantLimit, m_MaxDetailedVariants),
                NormalizeProfileName(m_ActiveProfileName),
                m_IncludeGeneratedCollections,
                m_RequireUpToDateCollection,
                m_StrippingMode,
                drafts);
        }

        /// <summary>
        /// Validates and persists the complete editor draft as one transaction. Invalid input never partially
        /// changes the project-wide build configuration.
        /// </summary>
        internal bool TryApplyEditorConfiguration(
            bool isEnabled,
            string reportDirectory,
            int maxDetailedVariants,
            string activeProfileName,
            bool includeGeneratedCollections,
            bool requireUpToDateCollection,
            ShaderVariantStrippingMode strippingMode,
            IReadOnlyList<ShaderVariantExplicitRuleDraft> explicitRules,
            out string error)
        {
            try
            {
                ValidateStrippingDependencies(
                    isEnabled,
                    includeGeneratedCollections,
                    requireUpToDateCollection,
                    strippingMode);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            if (!TryNormalizeEditorConfiguration(
                    reportDirectory,
                    maxDetailedVariants,
                    activeProfileName,
                    includeGeneratedCollections,
                    requireUpToDateCollection,
                    strippingMode,
                    explicitRules,
                    out string normalizedReportDirectory,
                    out string normalizedProfileName,
                    out List<ShaderVariantExplicitRule> normalizedRules,
                    out error))
                return false;

            m_IsEnabled = isEnabled;
            m_ReportDirectory = normalizedReportDirectory;
            m_MaxDetailedVariants = maxDetailedVariants;
            m_ActiveProfileName = normalizedProfileName;
            m_IncludeGeneratedCollections = includeGeneratedCollections;
            m_RequireUpToDateCollection = requireUpToDateCollection;
            m_StrippingMode = strippingMode;
            m_ExplicitRules = normalizedRules;
            Save(true);
            return true;
        }

        internal static bool TryNormalizeEditorConfiguration(
            string reportDirectory,
            int maxDetailedVariants,
            string activeProfileName,
            bool includeGeneratedCollections,
            bool requireUpToDateCollection,
            ShaderVariantStrippingMode strippingMode,
            IReadOnlyList<ShaderVariantExplicitRuleDraft> explicitRules,
            out string normalizedReportDirectory,
            out string normalizedProfileName,
            out List<ShaderVariantExplicitRule> normalizedRules,
            out string error)
        {
            normalizedReportDirectory = string.IsNullOrWhiteSpace(reportDirectory)
                ? DefaultReportDirectory
                : reportDirectory.Trim().Replace('\\', '/');
            normalizedProfileName = NormalizeProfileName(activeProfileName);
            normalizedRules = new List<ShaderVariantExplicitRule>();
            error = string.Empty;

            if (maxDetailedVariants < MinimumDetailedVariantLimit)
            {
                error = $"详细变体上限不能小于 {MinimumDetailedVariantLimit:N0}。";
                return false;
            }

            if (requireUpToDateCollection && !includeGeneratedCollections)
            {
                error = "要求最新预热集合时必须同时启用生成集合注入。";
                return false;
            }

            if (!Enum.IsDefined(typeof(ShaderVariantStrippingMode), strippingMode))
            {
                error = $"Shader 变体剔除模式无效：{strippingMode}。";
                return false;
            }

            if (strippingMode == ShaderVariantStrippingMode.GeneratedAllowlist &&
                (!includeGeneratedCollections || !requireUpToDateCollection))
            {
                error = "GeneratedAllowlist 剔除必须同时启用生成 SVC 注入和严格新鲜度门禁。";
                return false;
            }

            try
            {
                ShaderVariantPrewarmPaths.GetProfileDirectory("ValidationModule", normalizedProfileName);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            try
            {
                string projectRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, ".."));
                ShaderVariantAuditReportWriter.ResolveReportDirectory(
                    projectRoot,
                    normalizedReportDirectory);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            int ruleCount = explicitRules?.Count ?? 0;
            if (ruleCount > MaximumExplicitRuleCount)
            {
                error = $"显式规则数量不能超过 {MaximumExplicitRuleCount:N0} 条。";
                return false;
            }

            for (int index = 0; index < ruleCount; index++)
            {
                ShaderVariantExplicitRuleDraft draft = explicitRules[index];
                if (draft == null)
                {
                    error = $"显式规则 {index + 1} 为空。";
                    return false;
                }

                if (!Enum.IsDefined(typeof(PassType), draft.PassType))
                {
                    error = $"显式规则 {index + 1} 的 PassType 无效。";
                    return false;
                }

                if (!Enum.IsDefined(typeof(ShaderVariantAuditStage), draft.ShaderStage))
                {
                    error = $"显式规则 {index + 1} 的 Shader Stage 无效。";
                    return false;
                }

                if (draft.IsEnabled &&
                    string.IsNullOrWhiteSpace(draft.ShaderGuid) &&
                    string.IsNullOrWhiteSpace(draft.BuiltInShaderName))
                {
                    error = $"显式规则 {index + 1} 已启用，但没有指定 Shader 资源或内置 Shader 名称。";
                    return false;
                }

                normalizedRules.Add(new ShaderVariantExplicitRule(draft));
            }

            return true;
        }

        internal static bool TryNormalizeEditorConfiguration(
            string reportDirectory,
            int maxDetailedVariants,
            string activeProfileName,
            bool includeGeneratedCollections,
            bool requireUpToDateCollection,
            IReadOnlyList<ShaderVariantExplicitRuleDraft> explicitRules,
            out string normalizedReportDirectory,
            out string normalizedProfileName,
            out List<ShaderVariantExplicitRule> normalizedRules,
            out string error)
        {
            return TryNormalizeEditorConfiguration(
                reportDirectory,
                maxDetailedVariants,
                activeProfileName,
                includeGeneratedCollections,
                requireUpToDateCollection,
                ShaderVariantStrippingMode.AuditOnly,
                explicitRules,
                out normalizedReportDirectory,
                out normalizedProfileName,
                out normalizedRules,
                out error);
        }

        internal static bool TryNormalizeEditorConfiguration(
            string reportDirectory,
            int maxDetailedVariants,
            IReadOnlyList<ShaderVariantExplicitRuleDraft> explicitRules,
            out string normalizedReportDirectory,
            out List<ShaderVariantExplicitRule> normalizedRules,
            out string error)
        {
            return TryNormalizeEditorConfiguration(
                reportDirectory,
                maxDetailedVariants,
                ShaderVariantPrewarmPaths.DefaultProfileName,
                true,
                false,
                ShaderVariantStrippingMode.AuditOnly,
                explicitRules,
                out normalizedReportDirectory,
                out _,
                out normalizedRules,
                out error);
        }

        private static string NormalizeProfileName(string profileName)
        {
            return string.IsNullOrWhiteSpace(profileName)
                ? ShaderVariantPrewarmPaths.DefaultProfileName
                : profileName.Trim();
        }

        private static void ValidateStrippingDependencies(
            bool isEnabled,
            bool includeGeneratedCollections,
            bool requireUpToDateCollection,
            ShaderVariantStrippingMode strippingMode)
        {
            if (!Enum.IsDefined(typeof(ShaderVariantStrippingMode), strippingMode))
                throw new InvalidOperationException($"Shader 变体剔除模式无效：{strippingMode}。");
            if (strippingMode == ShaderVariantStrippingMode.AuditOnly) return;
            if (!isEnabled)
                throw new InvalidOperationException("启用 GeneratedAllowlist 剔除时必须同时启用 Shader 构建审计。");
            if (!includeGeneratedCollections || !requireUpToDateCollection)
                throw new InvalidOperationException(
                    "GeneratedAllowlist 剔除必须同时启用生成 SVC 注入和严格新鲜度门禁。");
        }

        internal void SaveSettings()
        {
            Save(true);
        }
    }

    /// <summary>
    /// Immutable build-time copy of an explicit rule. A build never reads mutable project settings twice.
    /// </summary>
    internal sealed class ShaderVariantExplicitRuleSnapshot
    {
        internal readonly bool IsEnabled;
        internal readonly string ShaderGuid;
        internal readonly string BuiltInShaderName;
        internal readonly string ModuleName;
        internal readonly string ProfileName;
        internal readonly PassType PassType;
        internal readonly string PassName;
        internal readonly ShaderVariantAuditStage ShaderStage;
        internal readonly string[] Keywords;
        internal readonly string Reason;

        internal ShaderVariantExplicitRuleSnapshot(ShaderVariantExplicitRule rule)
        {
            if (rule == null) throw new ArgumentNullException(nameof(rule));

            IsEnabled = rule.IsEnabled;
            ShaderGuid = rule.ShaderGuid;
            BuiltInShaderName = rule.BuiltInShaderName;
            ModuleName = rule.ModuleName;
            ProfileName = rule.ProfileName;
            PassType = rule.PassType;
            PassName = rule.PassName;
            ShaderStage = rule.ShaderStage;
            Keywords = ShaderVariantKeywordUtility.Normalize(rule.Keywords);
            Reason = rule.Reason;
        }
    }

    /// <summary>
    /// Immutable settings snapshot owned by one AssetBundle build transaction.
    /// </summary>
    internal sealed class ShaderVariantAuditConfiguration
    {
        internal readonly bool IsEnabled;
        internal readonly string ReportDirectory;
        internal readonly int MaxDetailedVariants;
        internal readonly string ActiveProfileName;
        internal readonly bool IncludeGeneratedCollections;
        internal readonly bool RequireUpToDateCollection;
        internal readonly ShaderVariantStrippingMode StrippingMode;
        internal readonly IReadOnlyList<ShaderVariantExplicitRuleSnapshot> ExplicitRules;

        internal ShaderVariantAuditConfiguration(
            bool isEnabled,
            string reportDirectory,
            int maxDetailedVariants,
            string activeProfileName,
            bool includeGeneratedCollections,
            bool requireUpToDateCollection,
            IReadOnlyList<ShaderVariantExplicitRuleSnapshot> explicitRules,
            ShaderVariantStrippingMode strippingMode = ShaderVariantStrippingMode.AuditOnly)
        {
            IsEnabled = isEnabled;
            ReportDirectory = reportDirectory ?? ShaderVariantAuditSettings.DefaultReportDirectory;
            MaxDetailedVariants = Math.Max(
                ShaderVariantAuditSettings.MinimumDetailedVariantLimit,
                maxDetailedVariants);
            ActiveProfileName = string.IsNullOrWhiteSpace(activeProfileName)
                ? ShaderVariantPrewarmPaths.DefaultProfileName
                : activeProfileName.Trim();
            IncludeGeneratedCollections = includeGeneratedCollections;
            RequireUpToDateCollection = requireUpToDateCollection;
            StrippingMode = strippingMode;
            ExplicitRules = explicitRules ?? Array.Empty<ShaderVariantExplicitRuleSnapshot>();
        }
    }
}
