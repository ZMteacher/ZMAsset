using System;
using System.IO;

namespace ZM.Asset
{
    /// <summary>
    /// 统一约束构建配置、运行时配置和热更 Manifest 中的 Bundle 物理文件名。
    /// ZMAsset 的发布协议只支持单层文件名，不允许目录或路径回退片段。
    /// </summary>
    internal static class AssetBundleNameValidator
    {
        private const string PortableInvalidCharacters = "<>:\"/\\|?*";

        internal static bool TryValidateFileName(string bundleName, out string failureReason)
        {
            if (string.IsNullOrWhiteSpace(bundleName))
            {
                failureReason = "名称不能为空";
                return false;
            }

            if (!string.Equals(bundleName, bundleName.Trim(), StringComparison.Ordinal))
            {
                failureReason = "名称不能包含首尾空白";
                return false;
            }

            if (string.Equals(bundleName, ".", StringComparison.Ordinal) ||
                bundleName.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                failureReason = "名称不能包含路径回退片段 '..'";
                return false;
            }

            if (bundleName.EndsWith(".", StringComparison.Ordinal))
            {
                failureReason = "名称不能以句点结尾";
                return false;
            }

            foreach (char character in bundleName)
            {
                if (char.IsControl(character) || PortableInvalidCharacters.IndexOf(character) >= 0)
                {
                    failureReason = $"名称包含非法字符：U+{(int)character:X4}";
                    return false;
                }
            }

            failureReason = null;
            return true;
        }

        internal static string EnsureValidFileName(string bundleName, string context)
        {
            if (!TryValidateFileName(bundleName, out string failureReason))
                throw new ArgumentException($"{context}无效：{failureReason}。", nameof(bundleName));
            return bundleName;
        }

        /// <summary>
        /// 在验证单层文件名后解析目标路径，并再次确认结果仍位于指定根目录内。
        /// </summary>
        internal static string ResolveChildPath(string rootDirectory, string fileName, string context)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException($"{context}根目录不能为空。", nameof(rootDirectory));

            EnsureValidFileName(fileName, context);
            string normalizedRoot = Path.GetFullPath(rootDirectory);
            string rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                                normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            string resolvedPath = Path.GetFullPath(Path.Combine(normalizedRoot, fileName));
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!resolvedPath.StartsWith(rootPrefix, comparison))
                throw new InvalidDataException($"{context}越过目标根目录：{fileName}");
            return resolvedPath;
        }
    }
}
