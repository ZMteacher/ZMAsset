using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// Resolves report artifacts for editor presentation without coupling the Build Hub to report naming details.
    /// </summary>
    internal static class ShaderVariantAuditArtifactLocator
    {
        internal sealed class Result
        {
            internal string DirectoryPath { get; }
            internal string MarkdownPath { get; }
            internal string ReportJsonPath { get; }
            internal string ManifestJsonPath { get; }

            internal bool HasAnyArtifact =>
                !string.IsNullOrEmpty(MarkdownPath) ||
                !string.IsNullOrEmpty(ReportJsonPath) ||
                !string.IsNullOrEmpty(ManifestJsonPath);

            internal Result(
                string directoryPath,
                string markdownPath,
                string reportJsonPath,
                string manifestJsonPath)
            {
                DirectoryPath = directoryPath ?? string.Empty;
                MarkdownPath = markdownPath ?? string.Empty;
                ReportJsonPath = reportJsonPath ?? string.Empty;
                ManifestJsonPath = manifestJsonPath ?? string.Empty;
            }
        }

        internal static bool TryFindLatest(
            string configuredReportDirectory,
            out Result result,
            out string error)
        {
            result = null;
            error = string.Empty;
            try
            {
                string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string directory = ShaderVariantAuditReportWriter.ResolveReportDirectory(
                    projectRoot,
                    configuredReportDirectory);
                result = new Result(
                    directory,
                    FindLatest(directory, "*.audit.md"),
                    FindLatest(directory, "*.audit.json"),
                    FindLatest(directory, "*.manifest.json"));
                return true;
            }
            catch (Exception exception)
            {
                error = $"读取 Shader 审计报告目录失败：{exception.Message}";
                return false;
            }
        }

        private static string FindLatest(string directory, string pattern)
        {
            if (!Directory.Exists(directory)) return string.Empty;
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? string.Empty;
        }
    }
}
