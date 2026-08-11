using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace ZM.Asset
{
    public partial class ZMAsset
    {
        /// <summary>
        /// Module-scoped shader variant warmup API.
        /// </summary>
        public static class ShaderVariants
        {
            /// <summary>
            /// Initializes the module, loads its generated prewarm manifest, and progressively warms the selected
            /// collection. The operation returns a diagnostic result for success, cancellation, timeout, and
            /// recoverable configuration failures.
            /// </summary>
            public static UniTask<ShaderVariantPrewarmResult> WarmUpModuleAsync(
                string moduleName,
                string profileName = ShaderVariantPrewarmPaths.DefaultProfileName,
                ShaderVariantPrewarmOptions options = default,
                IProgress<ShaderVariantPrewarmProgress> progress = null,
                CancellationToken cancellationToken = default)
            {
                ValidateModuleName(moduleName);
                return ShaderVariantPrewarmService.WarmUpModuleAsync(
                    InitializedInstance.mResource,
                    moduleName,
                    profileName,
                    options,
                    progress,
                    cancellationToken);
            }
        }
    }
}
