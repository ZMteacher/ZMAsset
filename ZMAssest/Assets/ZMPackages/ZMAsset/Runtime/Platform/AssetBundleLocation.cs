namespace ZM.Asset
{
    /// <summary>
    /// 标识 AssetBundle 当前来自内嵌目录、热更目录还是远端地址。
    /// 该值只描述来源，不参与 Bundle 的引用计数或缓存身份计算。
    /// </summary>
    internal enum AssetBundleSourceKind
    {
        Builtin,
        HotUpdate,
        Remote
    }

    /// <summary>
    /// 描述一个待加载 Bundle 的平台无关位置。
    /// Native 后端把 <see cref="UriOrPath"/> 解释为文件路径，WebGL 后端将在后续阶段把它解释为 URL。
    /// </summary>
    internal readonly struct AssetBundleLocation
    {
        internal AssetBundleLocation(
            string moduleName,
            string bundleName,
            AssetBundleSourceKind sourceKind,
            string uriOrPath,
            string contentHash,
            uint crc,
            bool isEncrypted)
        {
            ModuleName = moduleName;
            BundleName = bundleName;
            SourceKind = sourceKind;
            UriOrPath = uriOrPath;
            ContentHash = contentHash;
            Crc = crc;
            IsEncrypted = isEncrypted;
        }

        internal string ModuleName { get; }

        internal string BundleName { get; }

        internal AssetBundleSourceKind SourceKind { get; }

        internal string UriOrPath { get; }

        internal string ContentHash { get; }

        internal uint Crc { get; }

        internal bool IsEncrypted { get; }
    }
}
