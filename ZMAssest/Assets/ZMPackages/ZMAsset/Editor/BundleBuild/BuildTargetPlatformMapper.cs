using System;
using UnityEditor;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 统一维护 Unity 编辑器构建目标与运行时热更协议平台之间的映射。
    /// WebGL 不能依赖枚举数值裸转，否则旧运行时枚举会把数值 20 写成“20”。
    /// </summary>
    internal static class BuildTargetPlatformMapper
    {
        // Unity 2022 的 StandaloneLinux64 数值为 24；历史代码将该未命名值写入清单后缀“_24.json”。
        // 第 0 期只补 WebGL 映射，必须保持 Linux64 已发布产物的历史协议。
        private const int LegacyStandaloneLinux64ManifestTargetValue = 24;

        /// <summary>
        /// 将 Unity 编辑器目标转换为运行时 BundleSettings 使用的平台枚举。
        /// 未明确支持的目标会在构建前失败，避免继续发布名称错误的资源与热更清单。
        /// </summary>
        internal static global::BuildTarget ToRuntimeBuildTarget(UnityEditor.BuildTarget unityBuildTarget)
        {
            switch (unityBuildTarget)
            {
                case UnityEditor.BuildTarget.iOS:
                    return global::BuildTarget.iOS;
                case UnityEditor.BuildTarget.Android:
                    return global::BuildTarget.Android;
                case UnityEditor.BuildTarget.StandaloneWindows64:
                    return global::BuildTarget.StandaloneWindows64;
                case UnityEditor.BuildTarget.StandaloneOSX:
                    return global::BuildTarget.StandaloneOSX;
                case UnityEditor.BuildTarget.StandaloneLinux64:
                    return (global::BuildTarget)LegacyStandaloneLinux64ManifestTargetValue;
                case UnityEditor.BuildTarget.WebGL:
                    return global::BuildTarget.WebGL;
                default:
                    throw new NotSupportedException(
                        $"ZMAsset 尚未定义 Unity 构建目标 {unityBuildTarget} 的运行时热更平台映射。" +
                        "请在 BuildTargetPlatformMapper 中显式补充映射后再构建。");
            }
        }

        /// <summary>
        /// 使用与运行时完全一致的命名规则生成热更 Manifest 文件名。
        /// 构建器必须通过该入口生成名称，避免任何调用点重新引入枚举数值转换。
        /// </summary>
        internal static string GetHotManifestName(string moduleName, UnityEditor.BuildTarget unityBuildTarget)
        {
            BundleSettings settings = BundleSettings.Instance;
            if (settings == null)
                throw new InvalidOperationException("找不到 AssetsBundleSettings，无法生成热更 Manifest 文件名。");

            return settings.HotManifestName(moduleName, ToRuntimeBuildTarget(unityBuildTarget));
        }

        /// <summary>
        /// 在资源扫描和 staging 创建前确认当前 Unity 安装具备目标平台的构建模块。
        /// 映射校验与 Build Support 校验必须同时通过，才能避免构建到一半才因平台环境缺失失败。
        /// </summary>
        internal static void EnsureEditorBuildTargetSupported(UnityEditor.BuildTarget unityBuildTarget)
        {
            ToRuntimeBuildTarget(unityBuildTarget);
            BuildTargetGroup buildTargetGroup = BuildPipeline.GetBuildTargetGroup(unityBuildTarget);
            bool isBuildTargetSupported = buildTargetGroup != BuildTargetGroup.Unknown &&
                                          BuildPipeline.IsBuildTargetSupported(buildTargetGroup, unityBuildTarget);
            EnsureBuildSupportAvailable(
                unityBuildTarget,
                isBuildTargetSupported,
                UnityEngine.Application.unityVersion);
        }

        /// <summary>
        /// 校验当前资源设置是否满足 WebGL 首发运行时约束。
        /// Native 平台继续保留整包 AES；WebGL 必须在创建 staging 和写配置前明确拒绝。
        /// </summary>
        internal static void EnsureWebGLRuntimeCompatibility(
            UnityEditor.BuildTarget unityBuildTarget,
            bool isBundleEncryptionEnabled)
        {
            if (unityBuildTarget != UnityEditor.BuildTarget.WebGL || !isBundleEncryptionEnabled)
                return;

            throw new NotSupportedException(
                "WebGL 首发版本不支持当前整包 AES 资源加密。" +
                "请在 ZMAsset 构建设置中关闭资源加密，再使用 HTTPS、Bundle Hash 和 CRC 发布 WebGL 资源；" +
                "该限制不会影响 Windows、Android、iOS 或 macOS 的 AES 构建。");
        }

        /// <summary>
        /// 在创建 staging 前校验 WebGL 发布协议。CORS、MIME 和内容编码由部署探针
        /// 针对实际服务器响应验证，不能用本地配置推断。
        /// </summary>
        internal static void EnsureWebGLBuildConfiguration(
            UnityEditor.BuildTarget unityBuildTarget,
            BuildAssetBundleOptions compression,
            bool isBundleEncryptionEnabled,
            string assetBaseUrl)
        {
            if (unityBuildTarget != UnityEditor.BuildTarget.WebGL)
                return;

            EnsureWebGLRuntimeCompatibility(unityBuildTarget, isBundleEncryptionEnabled);
            if ((compression & BuildAssetBundleOptions.ChunkBasedCompression) == 0)
            {
                throw new NotSupportedException(
                    "WebGL AssetBundle 必须使用 LZ4（ChunkBasedCompression）。" +
                    "请在 ZMAsset 构建设置中把压缩格式改为 LZ4 后重试。");
            }

            if (string.IsNullOrWhiteSpace(assetBaseUrl) ||
                !Uri.TryCreate(assetBaseUrl.Trim(), UriKind.Absolute, out Uri parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "WebGL 资源下载地址必须是绝对 HTTP/HTTPS URL。" +
                    "请在 ZMAsset 构建设置中填写实际 CDN 或资源服务器地址。");
            }

            if (parsedUri.Scheme != Uri.UriSchemeHttps && !parsedUri.IsLoopback)
            {
                throw new InvalidOperationException(
                    "WebGL 正式资源地址必须使用 HTTPS；仅 localhost/127.0.0.1 本地验收允许 HTTP。" +
                    "请修正资源下载地址，避免浏览器混合内容拦截。");
            }
        }

        /// <summary>
        /// 根据已取得的 Build Support 状态生成稳定且可操作的异常。
        /// 该方法不再执行平台映射，既避免生产入口重复转换，也让 EditMode 测试不依赖本机安装状态。
        /// </summary>
        internal static void EnsureBuildSupportAvailable(
            UnityEditor.BuildTarget unityBuildTarget,
            bool isBuildTargetSupported,
            string unityVersion)
        {
            if (isBuildTargetSupported) return;

            throw new NotSupportedException(
                $"当前 Unity {unityVersion} 未安装或不支持 {unityBuildTarget} 的 Build Support。" +
                "请通过 Unity Hub 为当前 Unity 版本安装对应平台模块后再构建。");
        }
    }
}
