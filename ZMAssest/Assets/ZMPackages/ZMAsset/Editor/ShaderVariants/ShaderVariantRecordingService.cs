using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// Isolates Unity's editor-only current-variant recording API behind a guarded compatibility boundary.
    /// Unity 2022.3 exposes these methods as internal, so an API mismatch disables recording operations without
    /// affecting AssetBundle building, audit collection, or report generation.
    /// </summary>
    internal static class ShaderVariantRecordingService
    {
        private const BindingFlags StaticInternal = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly MethodInfo ClearMethod = FindMethod(
            "ClearCurrentShaderVariantCollection",
            Type.EmptyTypes,
            typeof(void));
        private static readonly MethodInfo GetShaderCountMethod = FindMethod(
            "GetCurrentShaderVariantCollectionShaderCount",
            Type.EmptyTypes,
            typeof(int));
        private static readonly MethodInfo GetVariantCountMethod = FindMethod(
            "GetCurrentShaderVariantCollectionVariantCount",
            Type.EmptyTypes,
            typeof(int));
        private static readonly MethodInfo SaveMethod = FindMethod(
            "SaveCurrentShaderVariantCollection",
            new[] { typeof(string) },
            typeof(void));

        internal static bool IsAvailable =>
            ClearMethod != null &&
            GetShaderCountMethod != null &&
            GetVariantCountMethod != null &&
            SaveMethod != null;

        internal static string CompatibilityMessage => IsAvailable
            ? $"已适配 Unity {Application.unityVersion} 当前变体录制接口。"
            : $"Unity {Application.unityVersion} 未提供兼容的当前变体录制接口；审计与构建仍可正常工作。";

        internal static bool TryGetCounts(out int shaderCount, out int variantCount, out string error)
        {
            shaderCount = 0;
            variantCount = 0;
            if (!TryEnsureAvailable(out error)) return false;

            try
            {
                shaderCount = (int)GetShaderCountMethod.Invoke(null, null);
                variantCount = (int)GetVariantCountMethod.Invoke(null, null);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = BuildInvocationError("读取当前 Shader 变体统计", exception);
                return false;
            }
        }

        internal static bool TryClear(out string error)
        {
            if (!TryEnsureAvailable(out error)) return false;

            try
            {
                ClearMethod.Invoke(null, null);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = BuildInvocationError("清空当前 Shader 变体记录", exception);
                return false;
            }
        }

        internal static bool TrySave(string assetPath, out string error)
        {
            if (!TryValidateAssetPath(assetPath, out string normalizedAssetPath, out error))
                return false;
            if (!TryEnsureAvailable(out error)) return false;

            try
            {
                SaveMethod.Invoke(null, new object[] { normalizedAssetPath });
                AssetDatabase.ImportAsset(
                    normalizedAssetPath,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                ShaderVariantCollection collection =
                    AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(normalizedAssetPath);
                if (collection == null)
                {
                    error = $"Unity 未能在“{normalizedAssetPath}”生成 ShaderVariantCollection。";
                    return false;
                }

                EditorGUIUtility.PingObject(collection);
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = BuildInvocationError($"保存当前 Shader 变体记录到“{normalizedAssetPath}”", exception);
                return false;
            }
        }

        internal static bool TryValidateAssetPath(
            string assetPath,
            out string normalizedAssetPath,
            out string error)
        {
            normalizedAssetPath = string.IsNullOrWhiteSpace(assetPath)
                ? string.Empty
                : assetPath.Trim().Replace('\\', '/');
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(normalizedAssetPath))
            {
                error = "ShaderVariantCollection 保存路径不能为空。";
                return false;
            }

            if (!normalizedAssetPath.StartsWith("Assets/", StringComparison.Ordinal) ||
                !string.Equals(
                    Path.GetExtension(normalizedAssetPath),
                    ".shadervariants",
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "ShaderVariantCollection 必须保存为 Assets 下的 .shadervariants 资源。";
                return false;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string assetsRoot = Path.GetFullPath(Application.dataPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(projectRoot, normalizedAssetPath));
            string requiredPrefix = assetsRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"ShaderVariantCollection 保存路径越出了 Assets：{normalizedAssetPath}";
                return false;
            }

            return true;
        }

        private static MethodInfo FindMethod(string name, Type[] parameterTypes, Type returnType)
        {
            MethodInfo method = typeof(ShaderUtil).GetMethod(
                name,
                StaticInternal,
                null,
                parameterTypes,
                null);
            return method != null && method.ReturnType == returnType ? method : null;
        }

        private static bool TryEnsureAvailable(out string error)
        {
            error = IsAvailable ? string.Empty : CompatibilityMessage;
            return IsAvailable;
        }

        private static string BuildInvocationError(string operation, Exception exception)
        {
            Exception cause = exception is TargetInvocationException invocationException &&
                              invocationException.InnerException != null
                ? invocationException.InnerException
                : exception;
            return $"{operation}失败：{cause.GetType().Name}：{cause.Message}";
        }
    }
}
