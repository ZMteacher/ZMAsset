namespace ZM.Asset
{
    /// <summary>
    /// 远端资源在当前运行环境中的本地可用状态。
    /// </summary>
    public enum RemoteAssetLocalStatus
    {
        /// <summary>资源主 Bundle 与全部依赖均可在不触发远端下载的情况下使用。</summary>
        Ready = 0,

        /// <summary>资源主 Bundle 尚未在本地就绪。</summary>
        Missing = 1,

        /// <summary>资源主 Bundle 已就绪，但至少一个依赖 Bundle 尚未就绪。</summary>
        Incomplete = 2,

        /// <summary>本地 Manifest 或 Bundle 声明与资源配置不一致。</summary>
        Invalid = 3,

        /// <summary>没有可供离线判断的已提交 Manifest。</summary>
        ManifestUnavailable = 4,

        /// <summary>资源路径未登记在指定模块的资源配置中。</summary>
        AssetNotConfigured = 5,

        /// <summary>指定资源模块尚未初始化，无法把资源路径解析为 Bundle。</summary>
        ModuleNotInitialized = 6,

        /// <summary>
        /// 当前平台无法在不发起请求的情况下可靠确认缓存状态。
        /// WebGL 冷启动后的 Unity 浏览器 Bundle 缓存属于这种情况。
        /// </summary>
        Unknown = 7
    }
}
