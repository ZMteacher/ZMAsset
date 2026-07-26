#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ZM.Editor
{
    /// <summary>
    /// 单个业务模块的 AssetBundle 目录映射。
    /// 字段名保持不变，以兼容 Library/ABViewerConfig.json 中的既有数据。
    /// </summary>
    [Serializable]
    internal sealed class GameModuleConfig
    {
        public string moduleName = string.Empty;
        public string abSubFolder = string.Empty;
    }

    /// <summary>
    /// 分析器的项目级本地配置。配置保存在 Library，不进入版本控制，
    /// 避免不同开发环境的构建目录互相污染。
    /// </summary>
    [Serializable]
    internal sealed class BundleViewerConfig
    {
        public string abRootPath = "StreamingAssets/AssetBundle";
        public List<GameModuleConfig> modules = new List<GameModuleConfig>();
    }

    /// <summary>
    /// 配置持久化边界。窗口不直接处理 JSON 和文件异常，
    /// 便于未来替换为 ProjectSettings 或 ScriptableObject。
    /// </summary>
    internal sealed class BundleViewerConfigurationStore
    {
        private const string RelativePath = "/../Library/ABViewerConfig.json";

        private string FullPath => Application.dataPath + RelativePath;

        internal BundleViewerConfig Load()
        {
            if (!File.Exists(FullPath))
                return new BundleViewerConfig();

            try
            {
                BundleViewerConfig config = JsonUtility.FromJson<BundleViewerConfig>(File.ReadAllText(FullPath));
                if (config == null) return new BundleViewerConfig();
                config.modules ??= new List<GameModuleConfig>();
                return config;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BundleDependencyViewer] 配置加载失败，已使用默认配置: {exception.Message}");
                return new BundleViewerConfig();
            }
        }

        internal void Save(BundleViewerConfig config)
        {
            if (config == null) return;
            try
            {
                File.WriteAllText(FullPath, JsonUtility.ToJson(config, true));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BundleDependencyViewer] 配置保存失败: {exception.Message}");
            }
        }
    }

    /// <summary>
    /// 深度资源扫描缓存。以 Bundle 修改时间作为失效依据，
    /// 只保存可重建结果，因此文件固定放在 Library。
    /// </summary>
    internal sealed class BundleEmbeddedScanCache
    {
        private const string RelativePath = "/../Library/ABDependencyViewerCache.json";

        [Serializable]
        private sealed class Entry
        {
            public string key;
            public bool hasEmbed;
            public long fileTicks;
        }

        [Serializable]
        private sealed class Data
        {
            public List<Entry> entries = new List<Entry>();
        }

        internal readonly struct CacheValue
        {
            internal readonly bool HasEmbed;
            internal readonly long FileTicks;

            internal CacheValue(bool hasEmbed, long fileTicks)
            {
                HasEmbed = hasEmbed;
                FileTicks = fileTicks;
            }
        }

        private readonly Dictionary<string, CacheValue> values = new Dictionary<string, CacheValue>();
        internal int Count => values.Count;
        internal bool IsDirty { get; private set; }
        private string FullPath => Application.dataPath + RelativePath;

        internal void Load()
        {
            values.Clear();
            IsDirty = false;
            if (!File.Exists(FullPath)) return;
            try
            {
                Data data = JsonUtility.FromJson<Data>(File.ReadAllText(FullPath));
                if (data?.entries == null) return;
                foreach (Entry entry in data.entries)
                    if (!string.IsNullOrEmpty(entry.key))
                        values[entry.key] = new CacheValue(entry.hasEmbed, entry.fileTicks);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BundleDependencyViewer] 扫描缓存加载失败: {exception.Message}");
            }
        }

        internal bool TryGet(string key, out CacheValue value) => values.TryGetValue(key, out value);

        internal void Set(string key, bool hasEmbed, long fileTicks)
        {
            values[key] = new CacheValue(hasEmbed, fileTicks);
            IsDirty = true;
        }

        internal void Save()
        {
            try
            {
                var data = new Data();
                foreach (KeyValuePair<string, CacheValue> pair in values)
                {
                    data.entries.Add(new Entry
                    {
                        key = pair.Key,
                        hasEmbed = pair.Value.HasEmbed,
                        fileTicks = pair.Value.FileTicks
                    });
                }
                File.WriteAllText(FullPath, JsonUtility.ToJson(data, true));
                IsDirty = false;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[BundleDependencyViewer] 扫描缓存保存失败: {exception.Message}");
            }
        }

        internal void Clear(bool deleteFile)
        {
            values.Clear();
            IsDirty = false;
            if (deleteFile && File.Exists(FullPath))
                File.Delete(FullPath);
        }
    }
}
#endif
