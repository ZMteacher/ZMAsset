namespace ZM.ZMAsset
{
    /// <summary>
    /// 资源版本检查返回的不可变结果。
    /// </summary>
    public readonly struct HotUpdateVersionCheckResult
    {
        /// <summary>
        /// 创建一次版本检查结果。
        /// </summary>
        public HotUpdateVersionCheckResult(bool requiresUpdate, float downloadSizeMb)
        {
            RequiresUpdate = requiresUpdate;
            DownloadSizeMb = downloadSizeMb;
        }

        /// <summary>
        /// 是否存在需要下载的远端资源。
        /// </summary>
        public bool RequiresUpdate { get; }

        /// <summary>
        /// 本次预计下载大小，单位为 MB。
        /// </summary>
        public float DownloadSizeMb { get; }
    }
}
