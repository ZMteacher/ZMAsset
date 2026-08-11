using System;

namespace ZM.Asset
{
    /// <summary>
    ///  唯一标识一个物理 AssetBundle；模块名和 Bundle 名共同参与相等性与哈希计算。
    ///  即使未来不同模块使用相同 Bundle 文件名，缓存、异步任务和释放也不会互相串用。
    /// </summary>
    public readonly struct ModuleBundleKey : IEquatable<ModuleBundleKey>
    {
        //00 模块名由配置初始化边界完成 Trim，此结构不在热路径重复分配字符串。
        public readonly string ModuleName;
        //00 BundleName 保存实际文件名（含框架后缀），用于直接拼接加载路径。
        public readonly string BundleName;

        /// <summary>
        ///  创建模块级 Bundle 身份；空值保留为空字符串，正式加载边界会给出明确错误。
        /// </summary>
        public ModuleBundleKey(string moduleName, string bundleName)
        {
            ModuleName = moduleName?.Trim() ?? string.Empty;
            BundleName = bundleName?.Trim() ?? string.Empty;
        }

        /// <summary>
        ///  模块名称和 Bundle 名称都使用 Ordinal，保持跨平台可预测且与构建端稳定名称一致。
        /// </summary>
        public bool Equals(ModuleBundleKey other)
        {
            return string.Equals(ModuleName, other.ModuleName, StringComparison.Ordinal) &&
                   string.Equals(BundleName, other.BundleName, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is ModuleBundleKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                //00 使用稳定的 397 组合两个字符串哈希，Equals 与 GetHashCode 使用完全相同的 Ordinal 维度。
                int moduleHash = StringComparer.Ordinal.GetHashCode(ModuleName ?? string.Empty);
                int bundleHash = StringComparer.Ordinal.GetHashCode(BundleName ?? string.Empty);
                return (moduleHash * 397) ^ bundleHash;
            }
        }

        public static bool operator ==(ModuleBundleKey left, ModuleBundleKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ModuleBundleKey left, ModuleBundleKey right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"{ModuleName}/{BundleName}";
        }
    }
}
