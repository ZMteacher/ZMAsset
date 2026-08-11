using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// Runtime descriptor generated from a completed shader audit. The manifest and its collection are packed as
    /// normal module entries, so download, encryption, hot update, reference counting, and module unload all keep
    /// using the existing ZMAsset lifecycle.
    /// </summary>
    public sealed class ShaderVariantPrewarmManifest : ScriptableObject
    {
        internal const int CurrentSchemaVersion = 1;

        [SerializeField] private int m_SchemaVersion = CurrentSchemaVersion;
        [SerializeField] private string m_ModuleName = string.Empty;
        [SerializeField] private string m_ProfileName = ShaderVariantPrewarmPaths.DefaultProfileName;
        [SerializeField] private string m_BuildTarget = string.Empty;
        [SerializeField] private string m_SourceManifestHash = string.Empty;
        [SerializeField] private string m_CollectionContentHash = string.Empty;
        [SerializeField] private int m_ShaderCount;
        [SerializeField] private int m_VariantCount;
        [SerializeField] private ShaderVariantCollection m_Collection;

        public int SchemaVersion => m_SchemaVersion;
        public string ModuleName => m_ModuleName ?? string.Empty;
        public string ProfileName => m_ProfileName ?? string.Empty;
        public string BuildTarget => m_BuildTarget ?? string.Empty;
        public string SourceManifestHash => m_SourceManifestHash ?? string.Empty;
        public string CollectionContentHash => m_CollectionContentHash ?? string.Empty;
        public int ShaderCount => m_ShaderCount;
        public int VariantCount => m_VariantCount;
        internal ShaderVariantCollection Collection => m_Collection;

        internal void Configure(
            string moduleName,
            string profileName,
            string buildTarget,
            string sourceManifestHash,
            string collectionContentHash,
            ShaderVariantCollection collection)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                throw new ArgumentException("Shader 预热清单模块名不能为空。", nameof(moduleName));
            if (string.IsNullOrWhiteSpace(profileName))
                throw new ArgumentException("Shader 预热配置档不能为空。", nameof(profileName));
            if (string.IsNullOrWhiteSpace(buildTarget))
                throw new ArgumentException("Shader 预热清单构建目标不能为空。", nameof(buildTarget));
            if (collection == null)
                throw new ArgumentNullException(nameof(collection));

            m_SchemaVersion = CurrentSchemaVersion;
            m_ModuleName = moduleName.Trim();
            m_ProfileName = profileName.Trim();
            m_BuildTarget = buildTarget.Trim();
            m_SourceManifestHash = sourceManifestHash?.Trim() ?? string.Empty;
            m_CollectionContentHash = collectionContentHash?.Trim() ?? string.Empty;
            m_ShaderCount = collection.shaderCount;
            m_VariantCount = collection.variantCount;
            m_Collection = collection;
        }
    }

    /// <summary>
    /// Shared editor/runtime naming contract for generated shader prewarm assets.
    /// Stable hashed suffixes prevent case-insensitive path collisions without exposing arbitrary input as a path.
    /// </summary>
    internal static class ShaderVariantPrewarmPaths
    {
        internal const string DefaultProfileName = "Default";
        internal const string GeneratedRootAssetPath =
            "Assets/ZMPackages/ZMAsset/Generated/ShaderVariants";
        internal const string ManifestFileName = "ZMAssetShaderVariantManifest.asset";
        internal const string CollectionFileName = "ZMAssetShaderVariants.shadervariants";

        internal static string GetProfileDirectory(string moduleName, string profileName)
        {
            return $"{GeneratedRootAssetPath}/{BuildStableSegment(moduleName, nameof(moduleName))}/" +
                   BuildStableSegment(profileName, nameof(profileName));
        }

        internal static string GetManifestAssetPath(string moduleName, string profileName)
        {
            return GetProfileDirectory(moduleName, profileName) + "/" + ManifestFileName;
        }

        internal static string GetCollectionAssetPath(string moduleName, string profileName)
        {
            return GetProfileDirectory(moduleName, profileName) + "/" + CollectionFileName;
        }

        internal static string GetBundleNameSuffix(string profileName)
        {
            return "zmasset_shadervariants_" + BuildStableSegment(profileName, nameof(profileName))
                .ToLowerInvariant();
        }

        internal static string BuildStableSegment(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Shader 预热路径标识不能为空。", parameterName);

            string normalized = value.Trim();
            if (normalized.Length > 128)
                throw new ArgumentException("Shader 预热路径标识不能超过 128 个字符。", parameterName);

            StringBuilder readable = new StringBuilder(Math.Min(32, normalized.Length));
            foreach (char character in normalized)
            {
                if (readable.Length >= 32) break;
                readable.Append(char.IsLetterOrDigit(character) || character == '_' || character == '-'
                    ? character
                    : '_');
            }
            if (readable.Length == 0) readable.Append("value");

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                StringBuilder suffix = new StringBuilder(12);
                for (int index = 0; index < 6; index++) suffix.Append(hash[index].ToString("x2"));
                return readable + "_" + suffix;
            }
        }
    }
}
