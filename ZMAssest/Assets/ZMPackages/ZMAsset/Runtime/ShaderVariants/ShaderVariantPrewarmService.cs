using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ZM.Asset
{
    internal interface IShaderVariantWarmupWork
    {
        bool IsValid { get; }
        bool IsWarmedUp { get; }
        int VariantCount { get; }
        int WarmedVariantCount { get; }
        bool WarmUpProgressively(int variantsPerFrame);
    }

    internal sealed class UnityShaderVariantWarmupWork : IShaderVariantWarmupWork
    {
        private readonly ShaderVariantCollection m_Collection;

        internal UnityShaderVariantWarmupWork(ShaderVariantCollection collection)
        {
            m_Collection = collection;
        }

        public bool IsValid => m_Collection != null;
        public bool IsWarmedUp => IsValid && m_Collection.isWarmedUp;
        public int VariantCount => IsValid ? m_Collection.variantCount : 0;
        public int WarmedVariantCount => IsValid ? m_Collection.warmedUpVariantCount : 0;

        public bool WarmUpProgressively(int variantsPerFrame)
        {
            return IsValid && m_Collection.WarmUpProgressively(variantsPerFrame);
        }
    }

    /// <summary>
    /// Owns the complete load/warm/release transaction and rejects duplicate warmups for the same module profile.
    /// All operations must start on Unity's main thread because ShaderVariantCollection is a Unity object.
    /// </summary>
    internal static class ShaderVariantPrewarmService
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<string> ActiveOperations =
            new HashSet<string>(StringComparer.Ordinal);

        internal static async UniTask<ShaderVariantPrewarmResult> WarmUpModuleAsync(
            IResourceInterface resources,
            string moduleName,
            string profileName,
            ShaderVariantPrewarmOptions options,
            IProgress<ShaderVariantPrewarmProgress> progress,
            CancellationToken cancellationToken)
        {
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            string normalizedModule = RequireName(moduleName, nameof(moduleName));
            string normalizedProfile = string.IsNullOrWhiteSpace(profileName)
                ? ShaderVariantPrewarmPaths.DefaultProfileName
                : RequireName(profileName, nameof(profileName));
            ShaderVariantPrewarmOptions normalizedOptions = options.Normalize();
            string operationKey = normalizedModule + "\u001f" + normalizedProfile;

            lock (Gate)
            {
                if (!ActiveOperations.Add(operationKey))
                {
                    return CreateResult(
                        ShaderVariantPrewarmStatus.Busy,
                        normalizedModule,
                        normalizedProfile,
                        0,
                        0,
                        0d,
                        "相同模块和配置档的 Shader 预热已经在运行。");
                }
            }

            AssetHandle<ShaderVariantPrewarmManifest> handle = null;
            try
            {
                // Resource loading and ShaderVariantCollection APIs are Unity-object operations. Accept calls from
                // worker threads, but move the whole transaction onto the PlayerLoop before touching either one.
                await UniTask.SwitchToMainThread(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                    return Cancelled(normalizedModule, normalizedProfile, 0, 0, 0d);

                bool initialized = await resources.InitAssetModule(normalizedModule);
                if (!initialized)
                {
                    return CreateResult(
                        ShaderVariantPrewarmStatus.ModuleInitializationFailed,
                        normalizedModule,
                        normalizedProfile,
                        0,
                        0,
                        0d,
                        $"资源模块初始化失败：{normalizedModule}");
                }

                string manifestPath = ShaderVariantPrewarmPaths.GetManifestAssetPath(
                    normalizedModule,
                    normalizedProfile);
                handle = await resources.LoadResourceAsync<ShaderVariantPrewarmManifest>(
                    manifestPath,
                    cancellationToken);
                if (handle == null || !handle.TryGetAsset(out ShaderVariantPrewarmManifest manifest))
                {
                    return CreateResult(
                        ShaderVariantPrewarmStatus.MissingManifest,
                        normalizedModule,
                        normalizedProfile,
                        0,
                        0,
                        0d,
                        $"找不到 Shader 预热清单：{manifestPath}");
                }

                if (!IsManifestValid(manifest, normalizedModule, normalizedProfile, out string validationError))
                {
                    return CreateResult(
                        ShaderVariantPrewarmStatus.InvalidManifest,
                        normalizedModule,
                        normalizedProfile,
                        0,
                        manifest.VariantCount,
                        0d,
                        validationError);
                }

                return await WarmUpCollectionAsync(
                    normalizedModule,
                    normalizedProfile,
                    manifest.Collection,
                    normalizedOptions,
                    progress,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return Cancelled(normalizedModule, normalizedProfile, 0, 0, 0d);
            }
            catch (Exception exception)
            {
                return CreateResult(
                    ShaderVariantPrewarmStatus.Failed,
                    normalizedModule,
                    normalizedProfile,
                    0,
                    0,
                    0d,
                    $"Shader 预热失败：{exception.GetType().Name}：{exception.Message}");
            }
            finally
            {
                handle?.Dispose();
                lock (Gate) ActiveOperations.Remove(operationKey);
            }
        }

        internal static async UniTask<ShaderVariantPrewarmResult> WarmUpCollectionAsync(
            string moduleName,
            string profileName,
            ShaderVariantCollection collection,
            ShaderVariantPrewarmOptions options,
            IProgress<ShaderVariantPrewarmProgress> progress,
            CancellationToken cancellationToken)
        {
            if (collection == null)
            {
                return CreateResult(
                    ShaderVariantPrewarmStatus.InvalidManifest,
                    moduleName,
                    profileName,
                    0,
                    0,
                    0d,
                    "Shader 预热集合为空。");
            }

            return await WarmUpWorkAsync(
                moduleName,
                profileName,
                new UnityShaderVariantWarmupWork(collection),
                options,
                progress,
                cancellationToken);
        }

        internal static async UniTask<ShaderVariantPrewarmResult> WarmUpWorkAsync(
            string moduleName,
            string profileName,
            IShaderVariantWarmupWork work,
            ShaderVariantPrewarmOptions options,
            IProgress<ShaderVariantPrewarmProgress> progress,
            CancellationToken cancellationToken)
        {
            ShaderVariantPrewarmOptions normalizedOptions = options.Normalize();
            if (work == null || !work.IsValid)
            {
                return CreateResult(
                    ShaderVariantPrewarmStatus.InvalidManifest,
                    moduleName,
                    profileName,
                    0,
                    0,
                    0d,
                    "Shader 预热工作项无效。");
            }

            if (cancellationToken.IsCancellationRequested)
                return Cancelled(moduleName, profileName, 0, work.VariantCount, 0d);

            int total = work.VariantCount;
            if (total <= 0)
            {
                progress?.Report(new ShaderVariantPrewarmProgress(moduleName, profileName, 0, 0, 0d));
                return CreateResult(
                    ShaderVariantPrewarmStatus.EmptyCollection,
                    moduleName,
                    profileName,
                    0,
                    0,
                    0d,
                    "Shader 预热集合不包含变体，无需执行预热。",
                    true);
            }

            if (work.IsWarmedUp)
            {
                progress?.Report(new ShaderVariantPrewarmProgress(moduleName, profileName, total, total, 0d));
                return CreateResult(
                    ShaderVariantPrewarmStatus.AlreadyWarmed,
                    moduleName,
                    profileName,
                    total,
                    total,
                    0d,
                    "Shader 变体已经完成预热。",
                    true);
            }

            double started = Time.realtimeSinceStartupAsDouble;
            try
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Cancelled(
                            moduleName,
                            profileName,
                            work.WarmedVariantCount,
                            total,
                            Time.realtimeSinceStartupAsDouble - started);
                    }

                    bool completed = work.WarmUpProgressively(normalizedOptions.VariantsPerFrame);
                    double elapsed = Time.realtimeSinceStartupAsDouble - started;
                    int warmed = work.WarmedVariantCount;
                    progress?.Report(new ShaderVariantPrewarmProgress(
                        moduleName,
                        profileName,
                        warmed,
                        total,
                        elapsed));

                    if (completed || work.IsWarmedUp)
                    {
                        return CreateResult(
                            ShaderVariantPrewarmStatus.Succeeded,
                            moduleName,
                            profileName,
                            Math.Max(warmed, total),
                            total,
                            elapsed,
                            "Shader 变体预热完成。",
                            true);
                    }

                    if (elapsed >= normalizedOptions.TimeoutSeconds)
                    {
                        return CreateResult(
                            ShaderVariantPrewarmStatus.TimedOut,
                            moduleName,
                            profileName,
                            warmed,
                            total,
                            elapsed,
                            $"Shader 预热超过 {normalizedOptions.TimeoutSeconds:0.###} 秒。",
                            true);
                    }

                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                    if (!work.IsValid)
                    {
                        return CreateResult(
                            ShaderVariantPrewarmStatus.InvalidManifest,
                            moduleName,
                            profileName,
                            warmed,
                            total,
                            elapsed,
                            "预热期间 ShaderVariantCollection 已被卸载。",
                            true);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return Cancelled(
                    moduleName,
                    profileName,
                    work == null || !work.IsValid ? 0 : work.WarmedVariantCount,
                    total,
                    Time.realtimeSinceStartupAsDouble - started);
            }
            catch (Exception exception)
            {
                return CreateResult(
                    ShaderVariantPrewarmStatus.Failed,
                    moduleName,
                    profileName,
                    work == null || !work.IsValid ? 0 : work.WarmedVariantCount,
                    total,
                    Time.realtimeSinceStartupAsDouble - started,
                    $"Shader 预热执行失败：{exception.GetType().Name}：{exception.Message}",
                    true);
            }
        }

        private static bool IsManifestValid(
            ShaderVariantPrewarmManifest manifest,
            string moduleName,
            string profileName,
            out string error)
        {
            error = string.Empty;
            if (manifest.SchemaVersion != ShaderVariantPrewarmManifest.CurrentSchemaVersion)
                error = $"Shader 预热清单版本不受支持：{manifest.SchemaVersion}";
            else if (!string.Equals(manifest.ModuleName, moduleName, StringComparison.Ordinal))
                error = $"Shader 预热清单模块不匹配：期望 {moduleName}，实际 {manifest.ModuleName}";
            else if (!string.Equals(manifest.ProfileName, profileName, StringComparison.Ordinal))
                error = $"Shader 预热配置档不匹配：期望 {profileName}，实际 {manifest.ProfileName}";
            else if (manifest.Collection == null)
                error = "Shader 预热清单没有关联 ShaderVariantCollection。";
            return string.IsNullOrEmpty(error);
        }

        private static string RequireName(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("名称不能为空。", parameterName);
            string normalized = value.Trim();
            if (normalized.Length > 128)
                throw new ArgumentException("名称不能超过 128 个字符。", parameterName);
            return normalized;
        }

        private static ShaderVariantPrewarmResult Cancelled(
            string moduleName,
            string profileName,
            int warmed,
            int total,
            double elapsed)
        {
            return CreateResult(
                ShaderVariantPrewarmStatus.Cancelled,
                moduleName,
                profileName,
                warmed,
                total,
                elapsed,
                "Shader 预热已取消。",
                true);
        }

        private static ShaderVariantPrewarmResult CreateResult(
            ShaderVariantPrewarmStatus status,
            string moduleName,
            string profileName,
            int warmed,
            int total,
            double elapsed,
            string message,
            bool preserveCounts = false)
        {
            return new ShaderVariantPrewarmResult(
                status,
                moduleName,
                profileName,
                preserveCounts ? warmed : Math.Max(0, warmed),
                preserveCounts ? total : Math.Max(0, total),
                elapsed,
                message);
        }
    }
}
