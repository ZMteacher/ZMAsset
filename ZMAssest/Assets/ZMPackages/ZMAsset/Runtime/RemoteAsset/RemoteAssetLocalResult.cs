using System;
using System.Collections.Generic;

namespace ZM.Asset
{
    /// <summary>
    /// 远端资源本地可用性查询结果。该结果是查询时刻的快照，实际加载仍会重新校验资源状态。
    /// </summary>
    public sealed class RemoteAssetLocalResult
    {
        /// <summary>被查询的项目相对资源路径。</summary>
        public string AssetPath { get; }

        /// <summary>资源物理归属模块。</summary>
        public string ModuleName { get; }

        /// <summary>本地可用状态。</summary>
        public RemoteAssetLocalStatus Status { get; }

        /// <summary>本次判断覆盖的主 Bundle 与依赖 Bundle 总数。</summary>
        public int RequiredBundleCount { get; }

        /// <summary>缺失、无效或无法可靠确认的 Bundle 数量。</summary>
        public int UnavailableBundleCount => UnavailableBundleNames.Count;

        /// <summary>未就绪的 Bundle 名称快照，用于业务诊断和下载提示。</summary>
        public IReadOnlyList<string> UnavailableBundleNames { get; }

        /// <summary>资源当前是否可以在不触发远端下载的情况下加载。</summary>
        public bool IsAvailable => Status == RemoteAssetLocalStatus.Ready;

        internal RemoteAssetLocalResult(
            string assetPath,
            string moduleName,
            RemoteAssetLocalStatus status,
            int requiredBundleCount,
            List<string> unavailableBundleNames)
        {
            AssetPath = assetPath ?? string.Empty;
            ModuleName = moduleName ?? string.Empty;
            Status = status;
            RequiredBundleCount = Math.Max(0, requiredBundleCount);
            UnavailableBundleNames = unavailableBundleNames == null || unavailableBundleNames.Count == 0
                ? Array.Empty<string>()
                : new List<string>(unavailableBundleNames).AsReadOnly();
        }
    }
}
