using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// Writes deterministic machine-readable output plus a concise human audit report.
    /// Reports are development artifacts and are kept outside Assets and published AssetBundle directories.
    /// </summary>
    internal static class ShaderVariantAuditReportWriter
    {
        internal static void Write(
            ShaderVariantAuditReport report,
            ShaderVariantCanonicalManifest manifest,
            string configuredReportDirectory)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string reportDirectory = ResolveReportDirectory(projectRoot, configuredReportDirectory);
            Directory.CreateDirectory(reportDirectory);

            string fileStem = SanitizeFileStem($"{report.contextName}_{report.buildTarget}_latest");
            string manifestPath = Path.Combine(reportDirectory, fileStem + ".manifest.json");
            string reportJsonPath = Path.Combine(reportDirectory, fileStem + ".audit.json");
            string reportMarkdownPath = Path.Combine(reportDirectory, fileStem + ".audit.md");

            report.canonicalManifestPath = ToProjectRelativePath(projectRoot, manifestPath);
            report.reportJsonPath = ToProjectRelativePath(projectRoot, reportJsonPath);
            report.reportMarkdownPath = ToProjectRelativePath(projectRoot, reportMarkdownPath);

            WriteTextAtomically(manifestPath, JsonUtility.ToJson(manifest, true));
            WriteTextAtomically(reportJsonPath, JsonUtility.ToJson(report, true));
            WriteTextAtomically(reportMarkdownPath, BuildMarkdown(report));
        }

        internal static string ResolveReportDirectory(string projectRoot, string configuredReportDirectory)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("Unity 工程根目录不能为空。", nameof(projectRoot));

            string normalizedProjectRoot = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string relativeDirectory = string.IsNullOrWhiteSpace(configuredReportDirectory)
                ? ShaderVariantAuditSettings.DefaultReportDirectory
                : configuredReportDirectory.Trim();
            if (Path.IsPathRooted(relativeDirectory))
                throw new InvalidOperationException(
                    $"Shader 审计报告目录必须是工程内相对路径：{relativeDirectory}");

            string fullDirectory = Path.GetFullPath(Path.Combine(normalizedProjectRoot, relativeDirectory));
            string requiredPrefix = normalizedProjectRoot + Path.DirectorySeparatorChar;
            if (!fullDirectory.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Shader 审计报告目录越出了 Unity 工程：{relativeDirectory}");

            return fullDirectory;
        }

        private static string BuildMarkdown(ShaderVariantAuditReport report)
        {
            StringBuilder builder = new StringBuilder(4096);
            builder.AppendLine("# ZMAsset Shader 变体审计");
            builder.AppendLine();
            builder.AppendLine($"- 构建上下文：`{EscapeInline(report.contextName)}`");
            builder.AppendLine($"- 模块：`{EscapeInline(string.Join(", ", report.moduleNames ?? Array.Empty<string>()))}`");
            builder.AppendLine($"- 配置档：`{EscapeInline(report.activeProfileName)}`");
            builder.AppendLine($"- 剔除模式：`{EscapeInline(report.strippingMode)}`");
            builder.AppendLine($"- Unity：`{EscapeInline(report.unityVersion)}`");
            builder.AppendLine($"- 构建目标：`{EscapeInline(report.buildTarget)}`");
            builder.AppendLine($"- 构建成功：`{report.buildSucceeded}`");
            builder.AppendLine($"- 报告完整：`{report.reportIsComplete}`");
            builder.AppendLine($"- 规范清单：`{EscapeInline(report.canonicalManifestHash)}`");
            builder.AppendLine();
            builder.AppendLine("## 汇总");
            builder.AppendLine();
            builder.AppendLine("| 指标 | 数量 |");
            builder.AppendLine("|---|---:|");
            builder.AppendLine($"| Bundle | {report.bundleCount} |");
            builder.AppendLine($"| 根资源 | {report.rootAssetCount} |");
            builder.AppendLine($"| 材质资源 | {report.materialAssetCount} |");
            builder.AppendLine($"| 显式规则 | {report.explicitRuleCount} |");
            builder.AppendLine($"| 已注入预热集合 | {report.prewarmCollectionCount} |");
            builder.AppendLine($"| 已注入预热 Shader | {report.prewarmShaderCount} |");
            builder.AppendLine($"| 已注入预热变体 | {report.prewarmVariantCount} |");
            builder.AppendLine($"| 预处理前候选 | {report.candidateCountBeforeProcessing} |");
            builder.AppendLine($"| 预处理后候选 | {report.candidateCountAfterProcessing} |");
            builder.AppendLine($"| ZM 剔除后候选 | {report.candidateCountAfterStripping} |");
            builder.AppendLine($"| 本次剔除候选 | {report.strippedCandidateCount} |");
            builder.AppendLine($"| 处理前详细唯一项 | {report.detailedUniqueBeforeCount} |");
            builder.AppendLine($"| 处理后详细唯一项 | {report.detailedUniqueAfterCount} |");
            builder.AppendLine();
            builder.AppendLine("## 变体数量最高的 Shader");
            builder.AppendLine();
            builder.AppendLine("| Shader | 材质状态 | 显式规则 | 处理前 | 项目处理后 | ZM 剔除后 |");
            builder.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (ShaderVariantAuditShaderSummary summary in
                     (report.shaderSummaries ?? Array.Empty<ShaderVariantAuditShaderSummary>()).Take(50))
            {
                builder.AppendLine(
                    $"| {EscapeTable(summary.shaderName)} | {summary.materialStates} | " +
                    $"{summary.explicitRules} | {summary.candidatesBeforeProcessing} | " +
                    $"{summary.candidatesAfterProcessing} | {summary.candidatesAfterStripping} |");
            }

            builder.AppendLine();
            builder.AppendLine("## 剔除门禁");
            builder.AppendLine();
            builder.AppendLine("| 指标 | 数量 |");
            builder.AppendLine("|---|---:|");
            builder.AppendLine($"| Allowlist 变体 | {report.strippingAllowlistVariantCount} |");
            builder.AppendLine($"| Allowlist Shader | {report.strippingManagedShaderCount} |");
            builder.AppendLine($"| 受管 Shader/Pass 回调 | {report.strippingManagedSnippetCount} |");
            builder.AppendLine($"| 未受管回调（保留全部） | {report.strippingUnmanagedSnippetCount} |");
            builder.AppendLine($"| 未知 Pass 保底回调 | {report.strippingPassFallbackSnippetCount} |");
            builder.AppendLine($"| 零命中应急保底回调 | {report.strippingEmergencyFallbackSnippetCount} |");

            if (report.prewarmCollections != null && report.prewarmCollections.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## 本次构建注入的预热集合");
                builder.AppendLine();
                builder.AppendLine("| 模块 | 配置档 | Bundle | Shader | 变体 | 来源哈希 | 集合哈希 |");
                builder.AppendLine("|---|---|---|---:|---:|---|---|");
                foreach (ShaderVariantAuditPrewarmCollectionSummary summary in report.prewarmCollections)
                {
                    builder.AppendLine(
                        $"| {EscapeTable(summary.moduleName)} | {EscapeTable(summary.profileName)} | " +
                        $"{EscapeTable(summary.bundleName)} | {summary.shaderCount} | {summary.variantCount} | " +
                        $"{EscapeTable(summary.sourceManifestHash)} | " +
                        $"{EscapeTable(summary.collectionContentHash)} |");
                }
            }

            if (report.warnings != null && report.warnings.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("## 警告");
                builder.AppendLine();
                foreach (string warning in report.warnings)
                    builder.AppendLine($"- {warning}");
            }

            return builder.ToString();
        }

        private static void WriteTextAtomically(string destinationPath, string content)
        {
            string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, content ?? string.Empty, new UTF8Encoding(false));
                if (!File.Exists(destinationPath))
                {
                    File.Move(temporaryPath, destinationPath);
                    return;
                }

                string backupPath = destinationPath + ".bak";
                try
                {
                    File.Replace(temporaryPath, destinationPath, backupPath, true);
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(temporaryPath, destinationPath, true);
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static string SanitizeFileStem(string value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "ShaderVariantAudit" : value.Trim();
            char[] invalidCharacters = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(Math.Min(source.Length, 96));
            foreach (char character in source)
            {
                if (builder.Length >= 96) break;
                builder.Append(invalidCharacters.Contains(character) || char.IsWhiteSpace(character)
                    ? '_'
                    : character);
            }
            return builder.ToString();
        }

        private static string ToProjectRelativePath(string projectRoot, string fullPath)
        {
            string prefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(prefix.Length).Replace('\\', '/')
                : fullPath.Replace('\\', '/');
        }

        private static string EscapeInline(string value)
        {
            return (value ?? string.Empty).Replace("`", "'");
        }

        private static string EscapeTable(string value)
        {
            return (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
