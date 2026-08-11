using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZM.Asset
{
    internal sealed class ShaderVariantPrewarmBuildInput
    {
        internal readonly string BundleNameSuffix;
        internal readonly string[] AssetPaths;

        internal ShaderVariantPrewarmBuildInput(string bundleNameSuffix, string[] assetPaths)
        {
            BundleNameSuffix = bundleNameSuffix ?? throw new ArgumentNullException(nameof(bundleNameSuffix));
            AssetPaths = assetPaths ?? throw new ArgumentNullException(nameof(assetPaths));
        }
    }

    /// <summary>
    /// Validates generated collection freshness before it can enter a production AssetBundle input list.
    /// Non-strict mode skips missing or stale generated content; strict mode fails before BuildPipeline is invoked.
    /// </summary>
    internal static class ShaderVariantPrewarmBuildInjector
    {
        internal static ShaderVariantPrewarmBuildInput CreateInput(
            string moduleName,
            UnityEditor.BuildTarget buildTarget,
            IReadOnlyList<AssetBundleBuild> currentBuilds,
            ShaderVariantAuditConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (!configuration.IncludeGeneratedCollections) return null;

            UnityEditor.BuildTarget effectiveBuildTarget =
                buildTarget == UnityEditor.BuildTarget.NoTarget
                    ? EditorUserBuildSettings.activeBuildTarget
                    : buildTarget;

            string profileName = configuration.ActiveProfileName;
            string manifestPath = ShaderVariantPrewarmPaths.GetManifestAssetPath(moduleName, profileName);
            string collectionPath = ShaderVariantPrewarmPaths.GetCollectionAssetPath(moduleName, profileName);
            ShaderVariantPrewarmManifest manifest =
                AssetDatabase.LoadAssetAtPath<ShaderVariantPrewarmManifest>(manifestPath);
            ShaderVariantCollection collection =
                AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(collectionPath);

            if (manifest == null || collection == null)
                return HandleUnavailable(
                    configuration,
                    $"模块 {moduleName} 配置档 {profileName} 尚未生成 Shader 预热集合。" +
                    "请先完成一次审计构建，再在 Shader 变体页面生成 SVC。");
            if (manifest.SchemaVersion != ShaderVariantPrewarmManifest.CurrentSchemaVersion)
                return HandleUnavailable(
                    configuration,
                    $"模块 {moduleName} 的 Shader 预热清单版本为 {manifest.SchemaVersion}，" +
                    $"当前要求 {ShaderVariantPrewarmManifest.CurrentSchemaVersion}。请重新生成 SVC。");
            if (!string.Equals(manifest.ModuleName, moduleName, StringComparison.Ordinal) ||
                !string.Equals(manifest.ProfileName, profileName, StringComparison.Ordinal))
                return HandleUnavailable(
                    configuration,
                    $"Shader 预热清单身份不匹配：期望 {moduleName}/{profileName}，" +
                    $"实际 {manifest.ModuleName}/{manifest.ProfileName}。");
            if (!string.Equals(manifest.BuildTarget, effectiveBuildTarget.ToString(), StringComparison.Ordinal))
                return HandleUnavailable(
                    configuration,
                    $"模块 {moduleName} 的 Shader 预热集合目标为 {manifest.BuildTarget}，" +
                    $"当前构建目标为 {effectiveBuildTarget}。请重新审计并生成 SVC。");
            if (!ReferenceEquals(manifest.Collection, collection))
                return HandleUnavailable(configuration, $"Shader 预热清单未引用预期集合：{collectionPath}");
            if (manifest.ShaderCount != collection.shaderCount ||
                manifest.VariantCount != collection.variantCount)
                return HandleUnavailable(
                    configuration,
                    $"模块 {moduleName} 的 Shader 预热集合计数与生成清单不一致。请重新生成 SVC。");

            List<string> warnings = new List<string>();
            string currentSourceHash = ShaderVariantSourceManifestUtility.ComputeModuleHash(
                moduleName,
                profileName,
                effectiveBuildTarget,
                currentBuilds,
                configuration,
                warnings);
            foreach (string warning in warnings)
                Debug.LogWarning($"Shader 预热来源检查：{warning}");
            if (!string.Equals(
                    currentSourceHash,
                    manifest.SourceManifestHash,
                    StringComparison.Ordinal))
                return HandleUnavailable(
                    configuration,
                    $"模块 {moduleName} 配置档 {profileName} 的 Shader 来源已经变化，生成集合已过期。" +
                    "请重新完成审计构建并生成 SVC。");

            return new ShaderVariantPrewarmBuildInput(
                ShaderVariantPrewarmPaths.GetBundleNameSuffix(profileName),
                new[] { manifestPath, collectionPath });
        }

        private static ShaderVariantPrewarmBuildInput HandleUnavailable(
            ShaderVariantAuditConfiguration configuration,
            string message)
        {
            if (configuration.RequireUpToDateCollection)
                throw new InvalidOperationException(message);
            Debug.LogWarning(message + " 本次构建将跳过 Shader 预热集合注入。");
            return null;
        }
    }
}
