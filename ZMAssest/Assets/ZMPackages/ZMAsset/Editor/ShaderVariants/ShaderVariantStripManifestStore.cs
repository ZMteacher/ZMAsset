using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ZM.Asset
{
    [Serializable]
    internal sealed class ShaderVariantStripEntry
    {
        public string shaderName;
        public string shaderGuid;
        public string passType;
        public string[] keywords = Array.Empty<string>();

        internal string ShaderIdentity => !string.IsNullOrWhiteSpace(shaderGuid)
            ? shaderGuid.Trim()
            : shaderName?.Trim() ?? string.Empty;

        internal string StableKey => string.Join(
            "\u001f",
            ShaderIdentity,
            passType?.Trim() ?? string.Empty,
            string.Join("\u001e", ShaderVariantKeywordUtility.Normalize(keywords)));

        internal static ShaderVariantStripEntry FromAuditEntry(ShaderVariantAuditEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return new ShaderVariantStripEntry
            {
                shaderName = entry.shaderName?.Trim() ?? string.Empty,
                shaderGuid = entry.shaderGuid?.Trim() ?? string.Empty,
                passType = entry.passType?.Trim() ?? string.Empty,
                keywords = ShaderVariantKeywordUtility.Normalize(entry.keywords)
            };
        }
    }

    [Serializable]
    internal sealed class ShaderVariantStripManifest
    {
        internal const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;
        public string moduleName;
        public string profileName;
        public string buildTarget;
        public string unityVersion;
        public string sourceManifestHash;
        public string collectionContentHash;
        public string collectionAssetHash;
        public string shaderDependencyHash;
        public string allowlistContentHash;
        public ShaderVariantStripEntry[] variants = Array.Empty<ShaderVariantStripEntry>();
    }

    /// <summary>
    /// Persists editor-only stripping evidence under ProjectSettings. The allowlist is source-controlled build
    /// input, but never becomes an AssetBundle asset and therefore does not inflate runtime downloads.
    /// </summary>
    internal static class ShaderVariantStripManifestStore
    {
        internal const string RelativeRootDirectory = "ProjectSettings/ZMAssetShaderVariants";
        internal const string FileName = "GeneratedAllowlist.json";

        internal static string Write(
            string moduleName,
            string profileName,
            string buildTarget,
            string unityVersion,
            string sourceManifestHash,
            string collectionContentHash,
            string collectionAssetHash,
            IEnumerable<ShaderVariantAuditEntry> validatedEntries)
        {
            ShaderVariantStripEntry[] variants = NormalizeEntries(validatedEntries);
            ShaderVariantStripManifest manifest = new ShaderVariantStripManifest
            {
                moduleName = RequireName(moduleName, nameof(moduleName)),
                profileName = RequireName(profileName, nameof(profileName)),
                buildTarget = RequireName(buildTarget, nameof(buildTarget)),
                unityVersion = RequireName(unityVersion, nameof(unityVersion)),
                sourceManifestHash = RequireHash(sourceManifestHash, nameof(sourceManifestHash)),
                collectionContentHash = RequireHash(
                    collectionContentHash,
                    nameof(collectionContentHash)),
                collectionAssetHash = RequireName(collectionAssetHash, nameof(collectionAssetHash)),
                variants = variants
            };
            manifest.shaderDependencyHash = ComputeShaderDependencyHash(variants);
            manifest.allowlistContentHash = ComputeAllowlistContentHash(variants);

            string path = GetFullPath(moduleName, profileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException(
                $"无法解析 Shader 剔除清单目录：{path}"));
            WriteTextAtomically(path, JsonUtility.ToJson(manifest, true));
            return path;
        }

        internal static ShaderVariantStripManifest LoadRequired(string moduleName, string profileName)
        {
            string path = GetFullPath(moduleName, profileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"找不到模块 {moduleName} 配置档 {profileName} 的 Shader 剔除 allowlist。" +
                    "请从最新完整审计报告重新生成 SVC。",
                    path);

            ShaderVariantStripManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<ShaderVariantStripManifest>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                throw new InvalidDataException($"Shader 剔除 allowlist 无法解析：{path}", exception);
            }

            if (manifest == null)
                throw new InvalidDataException($"Shader 剔除 allowlist 内容为空：{path}");
            manifest.variants = manifest.variants ?? Array.Empty<ShaderVariantStripEntry>();
            string currentContentHash = ComputeAllowlistContentHash(manifest.variants);
            if (!string.Equals(
                    currentContentHash,
                    manifest.allowlistContentHash,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Shader 剔除 allowlist 完整性校验失败：{path}。请重新生成 SVC。");
            return manifest;
        }

        internal static string GetFullPath(string moduleName, string profileName)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string moduleSegment = ShaderVariantPrewarmPaths.BuildStableSegment(
                RequireName(moduleName, nameof(moduleName)),
                nameof(moduleName));
            string profileSegment = ShaderVariantPrewarmPaths.BuildStableSegment(
                RequireName(profileName, nameof(profileName)),
                nameof(profileName));
            string root = Path.GetFullPath(Path.Combine(projectRoot, RelativeRootDirectory));
            string path = Path.GetFullPath(Path.Combine(root, moduleSegment, profileSegment, FileName));
            string requiredPrefix = root.TrimEnd(
                                        Path.DirectorySeparatorChar,
                                        Path.AltDirectorySeparatorChar) +
                                    Path.DirectorySeparatorChar;
            if (!path.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Shader 剔除清单路径越出 ProjectSettings：{path}");
            return path;
        }

        internal static string ComputeShaderDependencyHash(IEnumerable<ShaderVariantStripEntry> entries)
        {
            List<string> dependencyRecords = new List<string>();
            foreach (IGrouping<string, ShaderVariantStripEntry> shaderGroup in
                     (entries ?? Array.Empty<ShaderVariantStripEntry>())
                         .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.ShaderIdentity))
                         .GroupBy(entry => entry.ShaderIdentity, StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                ShaderVariantStripEntry representative = shaderGroup.First();
                if (!string.IsNullOrWhiteSpace(representative.shaderGuid))
                {
                    string shaderPath = AssetDatabase.GUIDToAssetPath(representative.shaderGuid.Trim());
                    if (string.IsNullOrWhiteSpace(shaderPath))
                        throw new InvalidDataException(
                            $"Shader 剔除 allowlist 引用了缺失的 Shader GUID：{representative.shaderGuid}");
                    dependencyRecords.Add(
                        representative.shaderGuid.Trim() + "\u001f" +
                        AssetDatabase.GetAssetDependencyHash(shaderPath));
                }
                else
                {
                    dependencyRecords.Add(
                        "builtin\u001f" + representative.shaderName.Trim() + "\u001f" +
                        Application.unityVersion);
                }
            }
            return ComputeSha256(dependencyRecords);
        }

        internal static string ComputeAllowlistContentHash(IEnumerable<ShaderVariantStripEntry> entries)
        {
            return ComputeSha256(
                (entries ?? Array.Empty<ShaderVariantStripEntry>())
                    .Where(entry => entry != null)
                    .Select(entry => entry.StableKey)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal));
        }

        internal static void DeleteForTests(string moduleName, string profileName)
        {
            string path = GetFullPath(moduleName, profileName);
            if (File.Exists(path)) File.Delete(path);
            string profileDirectory = Path.GetDirectoryName(path);
            string moduleDirectory = string.IsNullOrEmpty(profileDirectory)
                ? null
                : Path.GetDirectoryName(profileDirectory);
            string rootDirectory = string.IsNullOrEmpty(moduleDirectory)
                ? null
                : Path.GetDirectoryName(moduleDirectory);
            DeleteDirectoryIfEmpty(profileDirectory);
            DeleteDirectoryIfEmpty(moduleDirectory);
            DeleteDirectoryIfEmpty(rootDirectory);
        }

        private static ShaderVariantStripEntry[] NormalizeEntries(
            IEnumerable<ShaderVariantAuditEntry> entries)
        {
            return (entries ?? Array.Empty<ShaderVariantAuditEntry>())
                .Where(entry => entry != null)
                .Select(ShaderVariantStripEntry.FromAuditEntry)
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ShaderIdentity))
                .GroupBy(entry => entry.StableKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(entry => entry.StableKey, StringComparer.Ordinal)
                .ToArray();
        }

        private static string ComputeSha256(IEnumerable<string> records)
        {
            string content = string.Join(
                "\n",
                (records ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal));
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) builder.Append(value.ToString("x2"));
                return builder.ToString();
            }
        }

        private static string RequireName(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Shader 剔除清单标识不能为空。", parameterName);
            return value.Trim();
        }

        private static string RequireHash(string value, string parameterName)
        {
            string hash = RequireName(value, parameterName);
            if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
                throw new ArgumentException("Shader 剔除清单哈希必须是 64 位 SHA-256。", parameterName);
            return hash.ToLowerInvariant();
        }

        private static void WriteTextAtomically(string destinationPath, string content)
        {
            string temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = destinationPath + ".bak";
            bool writeCompleted = false;
            try
            {
                File.WriteAllText(temporaryPath, content ?? string.Empty, new UTF8Encoding(false));
                if (!File.Exists(destinationPath))
                {
                    File.Move(temporaryPath, destinationPath);
                    writeCompleted = true;
                    return;
                }

                try
                {
                    File.Replace(temporaryPath, destinationPath, backupPath, true);
                    writeCompleted = true;
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(temporaryPath, destinationPath, true);
                    File.Delete(temporaryPath);
                    writeCompleted = true;
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                if (writeCompleted && File.Exists(backupPath)) File.Delete(backupPath);
            }
        }

        private static void DeleteDirectoryIfEmpty(string directory)
        {
            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, false);
        }
    }
}
