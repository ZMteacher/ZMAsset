using System;
using System.Collections.Generic;
using System.Linq;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 00 Shared 补丁配置变化等级；这里只分析资源身份和依赖结构，不推测线上业务兼容性。
    /// </summary>
    internal enum SharedPatchChangeSeverity
    {
        None,
        Additive,
        Severe
    }

    /// <summary>
    /// 00 Shared 结构变化报告仅用于构建提示，绝不自动修改开发者选择的补丁模块集合。
    /// </summary>
    internal sealed class SharedPatchChangeReport
    {
        internal SharedPatchChangeSeverity Severity { get; }
        internal IReadOnlyList<string> Details { get; }

        internal SharedPatchChangeReport(SharedPatchChangeSeverity severity, IReadOnlyList<string> details)
        {
            Severity = severity;
            Details = details ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// 00 对比 Shared 新旧 BundleConfig，识别新增资源与会破坏旧资源身份的严重结构变化。
    /// </summary>
    internal static class SharedPatchChangeAnalyzer
    {
        internal static SharedPatchChangeReport Analyze(BundleConfig previous, BundleConfig current)
        {
            //00 没有历史基线表示首次发布，无法定义“变化”，因此不弹出结构变更警告。
            if (previous?.bundleInfoList == null || current?.bundleInfoList == null)
                return new SharedPatchChangeReport(SharedPatchChangeSeverity.None, Array.Empty<string>());

            Dictionary<string, BundleInfo> previousByPath = CreatePathIndex(previous.bundleInfoList, "旧");
            Dictionary<string, BundleInfo> currentByPath = CreatePathIndex(current.bundleInfoList, "新");
            List<string> severeDetails = new List<string>();
            List<string> additiveDetails = new List<string>();

            foreach (KeyValuePair<string, BundleInfo> previousEntry in previousByPath.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!currentByPath.TryGetValue(previousEntry.Key, out BundleInfo currentInfo))
                {
                    severeDetails.Add($"删除资源：{previousEntry.Key}");
                    continue;
                }

                BundleInfo previousInfo = previousEntry.Value;
                if (!string.Equals(previousInfo.bundleName, currentInfo.bundleName, StringComparison.OrdinalIgnoreCase))
                    severeDetails.Add(
                        $"Bundle 归属变化：{previousEntry.Key}，{previousInfo.bundleName} → {currentInfo.bundleName}");
                if (previousInfo.isLoadableEntry != currentInfo.isLoadableEntry)
                    severeDetails.Add(
                        $"Entry 开放状态变化：{previousEntry.Key}，{previousInfo.isLoadableEntry} → {currentInfo.isLoadableEntry}");
                if (!SetEquals(GetDependencyIdentities(previousInfo), GetDependencyIdentities(currentInfo)))
                    severeDetails.Add($"资源依赖结构变化：{previousEntry.Key}");
            }

            foreach (string currentPath in currentByPath.Keys.OrderBy(path => path, StringComparer.Ordinal))
                if (!previousByPath.ContainsKey(currentPath)) additiveDetails.Add($"新增资源：{currentPath}");

            if (!SetEquals(previous.moduleDependencies, current.moduleDependencies))
                severeDetails.Add("Shared 的模块依赖声明发生变化。");

            HashSet<string> previousBundles = new HashSet<string>(
                previousByPath.Values.Select(info => info.bundleName).Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> currentBundles = new HashSet<string>(
                currentByPath.Values.Select(info => info.bundleName).Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);
            foreach (string removedBundle in previousBundles.Except(currentBundles, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(name => name, StringComparer.Ordinal))
                severeDetails.Add($"删除 Bundle：{removedBundle}");

            if (severeDetails.Count > 0)
                return new SharedPatchChangeReport(SharedPatchChangeSeverity.Severe, severeDetails);
            if (additiveDetails.Count > 0)
                return new SharedPatchChangeReport(SharedPatchChangeSeverity.Additive, additiveDetails);
            return new SharedPatchChangeReport(SharedPatchChangeSeverity.None, Array.Empty<string>());
        }

        private static Dictionary<string, BundleInfo> CreatePathIndex(IEnumerable<BundleInfo> bundleInfos, string label)
        {
            Dictionary<string, BundleInfo> result =
                new Dictionary<string, BundleInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleInfo info in bundleInfos)
            {
                if (info == null || string.IsNullOrWhiteSpace(info.path)) continue;
                string normalizedPath = info.path.Replace('\\', '/').Trim();
                if (!result.TryAdd(normalizedPath, info))
                    throw new InvalidOperationException($"{label} Shared 配置包含重复资源路径：{normalizedPath}");
            }
            return result;
        }

        private static IEnumerable<string> GetDependencyIdentities(BundleInfo info)
        {
            if (info?.bundleDependencies != null && info.bundleDependencies.Count > 0)
                return info.bundleDependencies
                    .Where(dependency => dependency != null)
                    .Select(dependency => $"{dependency.bundleModule?.Trim()}/{dependency.bundleName?.Trim()}");
            return info?.bundleDependce ?? Enumerable.Empty<string>();
        }

        private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right)
        {
            HashSet<string> leftSet = new HashSet<string>(
                (left ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return leftSet.SetEquals(
                (right ?? Enumerable.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }
}
