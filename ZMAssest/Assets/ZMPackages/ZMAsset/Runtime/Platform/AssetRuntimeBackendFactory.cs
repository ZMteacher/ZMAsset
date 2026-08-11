using System;

namespace ZM.Asset
{
    /// <summary>
    /// 标识当前运行时应使用的资源平台后端。
    /// </summary>
    internal enum AssetRuntimePlatformKind
    {
        Native,
        WebGL
    }

    /// <summary>
    /// 聚合一次平台选择产生的全部资源服务，保证加载、下载、元数据和事务不会混用不同平台实现。
    /// </summary>
    internal sealed class AssetRuntimeBackend
    {
        internal AssetRuntimeBackend(
            AssetRuntimePlatformKind platformKind,
            IAssetBundleLoader bundleLoader,
            IAssetDownloadService downloadService,
            IAssetMetadataStore metadataStore,
            IHotUpdateCommitStrategy commitStrategy,
            IAssetRuntimeScheduler scheduler)
        {
            PlatformKind = platformKind;
            BundleLoader = bundleLoader ?? throw new ArgumentNullException(nameof(bundleLoader));
            DownloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
            MetadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            CommitStrategy = commitStrategy ?? throw new ArgumentNullException(nameof(commitStrategy));
            Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        internal AssetRuntimePlatformKind PlatformKind { get; }

        internal IAssetBundleLoader BundleLoader { get; }

        internal IAssetDownloadService DownloadService { get; }

        internal IAssetMetadataStore MetadataStore { get; }

        internal IHotUpdateCommitStrategy CommitStrategy { get; }

        internal IAssetRuntimeScheduler Scheduler { get; }
    }

    /// <summary>
    /// 统一选择资源平台后端。
    /// Phase 1 尚未提供 WebGL 业务后端，因此 WebGL Player 必须明确失败，禁止静默落入 Native 文件实现。
    /// </summary>
    internal static class AssetRuntimeBackendFactory
    {
        // Lazy 的默认 ExecutionAndPublication 模式保证并发首次访问只创建并发布一套平台服务。
        // 这对后续带有 IndexedDB 状态的 WebGL 后端尤其重要，不能让不同调用方持有不同存储实例。
        private static readonly Lazy<AssetRuntimeBackend> sCurrent =
            new Lazy<AssetRuntimeBackend>(() => Create(DetectCurrentPlatform()));

        internal static AssetRuntimeBackend Current => sCurrent.Value;

        internal static AssetRuntimePlatformKind DetectCurrentPlatform()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return AssetRuntimePlatformKind.WebGL;
#else
            return AssetRuntimePlatformKind.Native;
#endif
        }

        internal static AssetRuntimeBackend Create(AssetRuntimePlatformKind platformKind)
        {
            if (platformKind == AssetRuntimePlatformKind.WebGL)
            {
                WebGLIndexedDbMetadataStore webMetadataStore = new WebGLIndexedDbMetadataStore();
                return new AssetRuntimeBackend(
                    AssetRuntimePlatformKind.WebGL,
                    new WebGLAssetBundleLoader(),
                    new WebGLAssetDownloadService(),
                    webMetadataStore,
                    new WebGLVersionPointerCommitStrategy(webMetadataStore),
                    new WebGLAssetRuntimeScheduler());
            }

            NativeFileMetadataStore metadataStore = new NativeFileMetadataStore();
            NativeDirectoryCommitStrategy commitStrategy = new NativeDirectoryCommitStrategy(metadataStore);
            return new AssetRuntimeBackend(
                AssetRuntimePlatformKind.Native,
                new NativeAssetBundleLoader(),
                new NativeAssetDownloadService(),
                metadataStore,
                commitStrategy,
                new NativeAssetRuntimeScheduler());
        }
    }
}
