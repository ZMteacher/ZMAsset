#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZM.Editor
{
    /// <summary>
    /// AssetBundle 依赖查看器
    /// 功能：
    ///   Tab1 - 单包依赖查看：选择具体Bundle，展示其完整依赖链，跨游戏依赖高亮标红
    ///   Tab2 - 跨游戏依赖检测：检测某游戏的所有Bundle是否引用了其他游戏的资源（资源冗余检查）
    ///   Tab3 - 全局依赖总览：扫描所有游戏，汇总跨游戏依赖情况
    /// 
    /// 原理：加载每个游戏的 AssetBundleManifest（StreamingAssets/AssetBundle/{GameName}/{Platform}），
    ///        通过 AssetBundleManifest.GetAllDependencies() 获取完整依赖链，
    ///        再按 Bundle 名前缀匹配是否属于其他游戏来判断跨游戏冗余。
    /// </summary>
    public partial class BundleDependencyViewer : EditorWindow
    {
        // ─── 自有配置（独立于ZMAsset框架）────────────────────────────────────
        private BundleViewerConfig _viewerConfig = new BundleViewerConfig();
        private readonly BundleViewerConfigurationStore _configurationStore = new BundleViewerConfigurationStore();

        // ─── 配置页面状态 ─────────────────────────────────────────────────────
        private bool   _showSettings;
        private string _cfgEditRootPath;
        private readonly List<GameModuleConfig> _cfgEditModules = new List<GameModuleConfig>();
        private Vector2 _cfgScroll;
        private int    _welcomeTheme; // reserved

        // ─── 平台 ─────────────────────────────────────────────────────────────
        private static readonly string[] Platforms = { "Android", "iOS" };
        private int _platformIdx;

        // ─── Tab ─────────────────────────────────────────────────────────────
        private int _tab;
        private static readonly string[] TabNames = { "AB版本对比", "跨游戏依赖检测", "全局依赖总览", "Bundle大小统计", "资源浏览器" };

        // ─── 游戏列表（以 GameData 子目录为准，AB文件夹名 = GameDataName + "Game"）──
        /// <summary>AB 文件夹名，如 "JackarooGame"，用于 Manifest 加载和前缀匹配</summary>
        private string[] _gameNames = Array.Empty<string>();
        /// <summary>UI 显示名，如 "Jackaroo" 或 "Jackaroo（未构建）"</summary>
        private string[] _gameDisplayNames = Array.Empty<string>();
        /// <summary>该游戏是否已有构建好的 AB 目录</summary>
        private bool[] _gameHasAb = Array.Empty<bool>();

        // 仅包含已构建 AB 的游戏名（供依赖分析使用）
        private string[] _builtGameNames = Array.Empty<string>();

        // ─── 缓存：key = "{GameName}_{Platform}"，value = 每个bundle的直接+全部依赖 ──
        private readonly Dictionary<string, GameManifestData> _cache = new();

        // ─── 深度扫描嵌入缓存（持久化到 Library）──────────────────────────────
        private readonly BundleEmbeddedScanCache _embedCache = new BundleEmbeddedScanCache();

        // ─── 导出格式 ─────────────────────────────────────────────────────────
        private static readonly string[] ExportFormats = { "TXT", "CSV" };
        private int _exportFmtIdx; // 0=TXT, 1=CSV
        private readonly Dictionary<string, int> _popupSelections = new();

        // ─── Tab1 状态：AB 版本对比 ────────────────────────────────────────────
        private string  _t1OldPath = string.Empty;
        private string  _t1NewPath = string.Empty;
        private Vector2 _t1Scroll;
        private int     _t1SortMode;   // 0=按变化量, 1=按名称, 2=按状态
        private int     _t1FilterMode; // 0=全部, 1=仅变化, 2=仅新增, 3=仅删除
        private bool    _t1ShowChart;  // false = collapsed by default
        private readonly List<BundleCompareEntry> _t1CompareResults = new();

        // ─── Tab2 状态 ────────────────────────────────────────────────────────
        private int  _t2SrcIdx;
        private int  _t2DstIdx;              // 0 = 全部其他游戏
        private bool _t2DeepScan = true;     // true = 同时加载bundle内资源路径做深度检测
        private bool _t2Dirty = true;
        private readonly List<CrossResult> _t2Results = new();
        private Vector2 _t2Scroll;
        private Vector2 _t2EmbedScroll;   // separate scroll for deep scan results
        private Vector2 _t2OuterScroll;   // outer scroll wrapping the full results area

        /// <summary>深度扫描结果：bundle内资源路径含有其他游戏名</summary>
        private struct EmbeddedCrossResult
        {
            public string SourceBundle;
            public List<(string assetPath, string matchedGame)> CrossAssets;
        }
        private readonly List<EmbeddedCrossResult> _t2EmbeddedResults = new();

        // ─── Tab3 状态 ────────────────────────────────────────────────────────
        private bool _t3Dirty = true;
        private bool _t3DeepScan = true;
        private readonly List<GameSummary> _t3Summary = new();
        private Vector2 _t3Scroll;

        // ─── Tab4 状态 ────────────────────────────────────────────────────────
        private int     _t4GameIdx;
        private bool    _t4ShowAll;         // true = 显示全部游戏合并, false = 只看单个游戏
        private int     _t4SortMode;        // 0=按大小降序, 1=按大小升序, 2=按名称, 3=按游戏+名称
        private bool    _t4OnlyCross;        // 只显示有跨游戏依赖的 bundle
        private Vector2 _t4Scroll;
        private bool    _t4NeedScan = true;  // true = 需要重新扫描, false = 仅重新筛选
        private readonly List<BundleSizeEntry> _t4Entries = new();

        // ─── Tab5 状态：资源浏览器 ─────────────────────────────────────────────
        private int     _t5GameIdx;
        private int     _t5BundleIdx;
        private bool    _t5NeedReload     = true;
        private string[] _t5BundleNames   = Array.Empty<string>();
        private bool    _t5ShowFoldout    = true;
        private bool    _t5ViewMode;            // false = 单包查看, true = 全部已加载
        private Vector2 _t5AssetScroll;
        private Vector2 _t5SearchScroll;
        private Vector2 _t5AllBundlesScroll;
        private Vector2 _t5ModuleScroll;
        private Vector2 _t5BundleScroll;
        private Vector2 _t5DetailScroll;
        private string  _t5SearchKeyword  = string.Empty;
        private string  _t5BundleSearch   = string.Empty;
        private string  _t5LastSearchKey  = string.Empty;
        // 在「全部已加载」模式下的折叠状态：key=cacheKey
        private readonly Dictionary<string, bool> _t5Foldouts = new();
        private readonly List<AssetEntry>    _t5SearchResults = new();
        // 批量加载状态
        private bool    _t5BatchLoading;
        private int     _t5BatchProgress;
        private int     _t5BatchTotal;
        private string  _t5BatchStatus    = string.Empty;
        private string  _t5PendingBundle  = string.Empty; // set by Tab2 "jump to Tab5"
        // 资源列表缓存：key = "GameName/bundleName"
        private readonly Dictionary<string, string[]> _assetListCache = new();

        // ─── 欢迎页专用样式 ───────────────────────────────────────────────────
        private GUIStyle _welcomeTitle;
        private GUIStyle _welcomeSubtitle;
        private GUIStyle _featureCardTitle;
        private GUIStyle _featureCardDesc;
        private GUIStyle _checkItem;
        private GUIStyle _ctaButton;
        private GUIStyle _styleRed;
        private GUIStyle _styleBoldRed;
        private GUIStyle _styleGreen;
        private GUIStyle _styleOrange;
        private GUIStyle _miniRightStyle;
        private GUIStyle _miniCenterStyle;
        private Vector2 _welcomeScroll;

        // ─────────────────────────────────────────────────────────────────────
        [MenuItem("ZM/AssetBundle Analyzer Hub",false,2)]
        public static void ShowWindow()
        {
            var win = GetWindow<BundleDependencyViewer>("AssetBundle Analyzer");
            win.titleContent = new GUIContent("AB 分析器", EditorGUIUtility.IconContent("UnityLogo").image);
            win.minSize = new Vector2(1200, 680);
        }

        private void OnEnable()
        {
            LoadViewerConfig();
            RefreshGameList();
            LoadEmbedCache();
        }

        private void OnDisable()
        {
            if (_embedCache.IsDirty) SaveEmbedCache();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  数据层
        // ═════════════════════════════════════════════════════════════════════

        private string AbRootFullPath =>
            Path.Combine(Application.dataPath, _viewerConfig.abRootPath).Replace('/', Path.DirectorySeparatorChar);

        private void LoadEmbedCache()
        {
            _embedCache.Load();
        }

        private void SaveEmbedCache()
        {
            _embedCache.Save();
        }

        private void LoadViewerConfig()
        {
            _viewerConfig = _configurationStore.Load();
        }

        private void SaveViewerConfig()
        {
            _configurationStore.Save(_viewerConfig);
        }

        // ─── 从自有配置构建游戏列表（不依赖 ZMAsset）────────────────────────
        /// Check embed cache; if miss/stale, load the bundle, scan asset paths, update cache.
        /// Returns true if the bundle contains embedded assets from other games.
        /// NOTE: Does NOT return which specific asset paths matched (use full scan for detail).
        /// </summary>
        private bool GetOrComputeEmbedded(string gameName, string bundleName, List<(string keyword, string gameName2)> keywords)
        {
            string bundlePath = Path.Combine(AbRootFullPath, gameName, bundleName);
            if (!File.Exists(bundlePath)) return false;

            string cacheKey     = $"{Platforms[_platformIdx]}/{gameName}/{bundleName}";
            long   currentTicks = File.GetLastWriteTimeUtc(bundlePath).Ticks;

            if (_embedCache.TryGet(cacheKey, out BundleEmbeddedScanCache.CacheValue cached) &&
                cached.FileTicks == currentTicks)
                return cached.HasEmbed;

            // Cache miss or stale — scan the bundle
            if (!IsValidAssetBundle(bundlePath))
            {
                _embedCache.Set(cacheKey, false, currentTicks);
                return false;
            }

            bool hasEmbed = false;
            AssetBundle ab = null;
            try
            {
                ab = AssetBundle.LoadFromFile(bundlePath);
                if (ab != null)
                    hasEmbed = ab.GetAllAssetNames()
                        .Any(ap => keywords.Any(k => ap.ToLower().Contains(k.keyword)));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BundleDependencyViewer] 嵌入缓存扫描失败 {gameName}/{bundleName}: {e.Message}");
            }
            finally
            {
                ab?.Unload(true);
            }

            _embedCache.Set(cacheKey, hasEmbed, currentTicks);
            return hasEmbed;
        }

        private void RefreshGameList()
        {
            string abRoot = AbRootFullPath;
            var names    = new List<string>();
            var displays = new List<string>();
            var hasAb    = new List<bool>();

            if (_viewerConfig.modules != null && _viewerConfig.modules.Count > 0)
            {
                // 使用自有配置中的模块列表
                foreach (var mod in _viewerConfig.modules)
                {
                    if (string.IsNullOrEmpty(mod.moduleName)) continue;
                    string folder   = string.IsNullOrEmpty(mod.abSubFolder) ? mod.moduleName : mod.abSubFolder;
                    bool   abExists = Directory.Exists(Path.Combine(abRoot, folder));
                    names.Add(folder);
                    displays.Add(abExists ? mod.moduleName : $"{mod.moduleName}（未构建）");
                    hasAb.Add(abExists);
                }
            }
            else
            {
                // 无配置时：扫描 AB 目录作为回退（方便用户初次了解目录结构）
                if (Directory.Exists(abRoot))
                {
                    foreach (var dir in Directory.GetDirectories(abRoot).OrderBy(d => d))
                    {
                        string name = Path.GetFileName(dir);
                        names.Add(name);
                        displays.Add(name);
                        hasAb.Add(true);
                    }
                }
            }

            _gameNames        = names.ToArray();
            _gameDisplayNames = displays.ToArray();
            _gameHasAb        = hasAb.ToArray();
            _builtGameNames   = names.Where((n, i) => hasAb[i]).ToArray();

            InvalidateAllResults();
        }

        private GameManifestData LoadOrGetManifest(string gameName)
        {
            string cacheKey = $"{gameName}_{Platforms[_platformIdx]}";
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

            string platform = Platforms[_platformIdx];
            string manifestPath = Path.Combine(AbRootFullPath, gameName, platform);
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[BundleDependencyViewer] Manifest 不存在: {manifestPath}");
                return null;
            }

            if (!IsValidAssetBundle(manifestPath))
            {
                Debug.LogWarning($"[BundleDependencyViewer] Manifest 文件头无效（可能被加密）: {manifestPath}");
                return null;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(manifestPath);
                if (bundle == null)
                {
                    Debug.LogWarning($"[BundleDependencyViewer] 加载 AssetBundle 失败: {manifestPath}");
                    return null;
                }

                var manifest = bundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                if (manifest == null)
                {
                    Debug.LogWarning($"[BundleDependencyViewer] 找不到 AssetBundleManifest 资产: {manifestPath}");
                    return null;
                }

                var data = new GameManifestData();
                foreach (var bundleName in manifest.GetAllAssetBundles())
                {
                    data.DirectDeps[bundleName]  = manifest.GetDirectDependencies(bundleName);
                    data.AllDeps[bundleName]      = manifest.GetAllDependencies(bundleName);
                }
                _cache[cacheKey] = data;
                return data;
            }
            catch (Exception e)
            {
                Debug.LogError($"[BundleDependencyViewer] 加载 Manifest 异常: {e.Message}");
                return null;
            }
            finally
            {
                bundle?.Unload(true);
            }
        }

        private void InvalidateAllResults()
        {
            _t1CompareResults.Clear();
            _t2Dirty = true;
            _t3Dirty = true;
            _t4NeedScan = true;
            _t5NeedReload = true;
            _t2Results.Clear();
            _t2EmbeddedResults.Clear();
            _t3Summary.Clear();
            _t4Entries.Clear();
            _assetListCache.Clear();
            _t5SearchResults.Clear();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  OnGUI 入口
        // ═════════════════════════════════════════════════════════════════════

        private void OnGUI()
        {
            EnsureStyles();
            BundleAnalyzerStyles.Ensure();
            GUIStyle previousButton = GUI.skin.button;
            GUI.skin.button = ZMBuildStyles.CompactSecondaryButton;
            try
            {
                EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), ZMBuildStyles.Window);
                DrawTopToolbar();

                // 设置面板覆盖整个内容区域
                if (_showSettings)
                {
                    DrawSettingsPanel();
                    return;
                }

                // 未配置任何模块时显示引导页
                if (_viewerConfig.modules == null || _viewerConfig.modules.Count == 0)
                {
                    DrawWelcomePage();
                    return;
                }

                EditorGUI.DrawRect(new Rect(0, 78, 220, position.height - 78), ZMBuildStyles.Sidebar);
                EditorGUI.DrawRect(new Rect(219, 78, 1, position.height - 78), ZMBuildStyles.Border);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawPageNavigation();
                    // 内容区与侧栏分割线保持稳定留白，卡片阴影不会压在线条上。
                    GUILayout.Space(18);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        GUILayout.Space(18);
                        switch (_tab)
                        {
                            case 0: DrawTab1_VersionCompare(); break;
                            case 1: DrawTab2_CrossGame(); break;
                            case 2: DrawTab3_GlobalOverview(); break;
                            case 3: DrawTab4_BundleSize(); break;
                            case 4: DrawTab5_AssetBrowser(); break;
                        }
                    }
                    GUILayout.Space(22);
                }
            }
            finally
            {
                GUI.skin.button = previousButton;
            }
        }

        private void DrawPageNavigation()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(220), GUILayout.ExpandHeight(true)))
            {
                GUILayout.Space(18);
                for (int i = 0; i < TabNames.Length; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(12);
                        Rect row = GUILayoutUtility.GetRect(196, 48, GUILayout.Width(196), GUILayout.Height(48));
                        if (GUI.Button(row, TabNames[i],
                                i == _tab ? BundleAnalyzerStyles.SidebarItemSelected : BundleAnalyzerStyles.SidebarItem))
                            _tab = i;
                        DrawNavigationGlyph(new Rect(row.x + 17, row.y + 15, 18, 18), i,
                            i == _tab ? Color.white : new Color32(160, 169, 180, 255));
                        GUILayout.Space(12);
                    }
                    if (i < TabNames.Length - 1) GUILayout.Space(7);
                }
                GUILayout.FlexibleSpace();
                GUILayout.Label($"  已配置 {_gameNames.Length} 个模块", BundleAnalyzerStyles.PageSubtitle,
                    GUILayout.Height(36));
                GUILayout.Space(14);
            }
        }

        private static void DrawNavigationGlyph(Rect rect, int index, Color color)
        {
            if (Event.current.type != EventType.Repaint) return;
            Handles.BeginGUI();
            Color previous = Handles.color;
            Handles.color = color;
            Vector3[] points;
            if (index == 0)
            {
                Handles.DrawAAPolyLine(1.6f, new Vector3(rect.x + 3, rect.center.y),
                    new Vector3(rect.xMax - 3, rect.center.y));
                Handles.DrawWireDisc(new Vector3(rect.x + 5, rect.center.y), Vector3.forward, 3);
                Handles.DrawWireDisc(new Vector3(rect.xMax - 5, rect.center.y), Vector3.forward, 3);
            }
            else if (index == 1)
            {
                Handles.DrawWireDisc(new Vector3(rect.x + 4, rect.y + 4), Vector3.forward, 3);
                Handles.DrawWireDisc(new Vector3(rect.xMax - 4, rect.center.y), Vector3.forward, 3);
                Handles.DrawWireDisc(new Vector3(rect.x + 4, rect.yMax - 4), Vector3.forward, 3);
                Handles.DrawAAPolyLine(1.6f, new Vector3(rect.x + 7, rect.y + 5),
                    new Vector3(rect.xMax - 7, rect.center.y), new Vector3(rect.x + 7, rect.yMax - 5));
            }
            else if (index == 2)
            {
                Handles.DrawWireDisc(rect.center, Vector3.forward, 7);
                Handles.DrawWireDisc(rect.center, Vector3.forward, 2);
            }
            else if (index == 3)
            {
                points = new[] { new Vector3(rect.x + 2, rect.yMax - 2), new Vector3(rect.x + 2, rect.y + 10),
                    new Vector3(rect.x + 7, rect.y + 10), new Vector3(rect.x + 7, rect.yMax - 2),
                    new Vector3(rect.x + 11, rect.yMax - 2), new Vector3(rect.x + 11, rect.y + 5),
                    new Vector3(rect.x + 16, rect.y + 5), new Vector3(rect.x + 16, rect.yMax - 2) };
                Handles.DrawAAPolyLine(1.6f, points);
            }
            else
            {
                Handles.DrawAAPolyLine(1.6f, new Vector3(rect.x + 2, rect.y + 6),
                    new Vector3(rect.x + 7, rect.y + 6), new Vector3(rect.x + 9, rect.y + 3),
                    new Vector3(rect.xMax - 2, rect.y + 3), new Vector3(rect.xMax - 2, rect.yMax - 3),
                    new Vector3(rect.x + 2, rect.yMax - 3), new Vector3(rect.x + 2, rect.y + 6));
            }
            Handles.color = previous;
            Handles.EndGUI();
        }

        private void EnsureStyles()
        {
            if (_styleRed != null) return;
            _styleRed     = new GUIStyle(EditorStyles.label)     { normal = { textColor = new Color(0.9f, 0.25f, 0.25f) } };
            _styleBoldRed = new GUIStyle(EditorStyles.boldLabel)  { normal = { textColor = new Color(0.9f, 0.25f, 0.25f) } };
            _styleGreen   = new GUIStyle(EditorStyles.label)      { normal = { textColor = new Color(0.22f, 0.72f, 0.30f) } };
            _styleOrange  = new GUIStyle(EditorStyles.label)      { normal = { textColor = new Color(0.95f, 0.55f, 0.10f) } };
            _miniRightStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            _miniCenterStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.UpperCenter,
                wordWrap  = false,
                clipping  = TextClipping.Clip
            };

            // 欢迎页专用
            _welcomeTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize  = 22,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = new Color(0.95f, 0.95f, 0.95f) }
            };
            _welcomeSubtitle = new GUIStyle(EditorStyles.label)
            {
                fontSize  = 11,
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = true,
                normal    = { textColor = new Color(0.70f, 0.75f, 0.82f) }
            };
            _featureCardTitle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
                normal   = { textColor = new Color(0.92f, 0.92f, 0.92f) }
            };
            _featureCardDesc = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = true,
                normal   = { textColor = new Color(0.68f, 0.72f, 0.78f) }
            };
            _checkItem = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                wordWrap = false,
                normal   = { textColor = new Color(0.75f, 0.85f, 0.70f) }
            };
            _ctaButton = new GUIStyle(GUI.skin.button)
            {
                fontSize  = 13,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = Color.white, background = MakeTex(2, 2, new Color(0.18f, 0.46f, 0.88f)) },
                hover     = { textColor = Color.white, background = MakeTex(2, 2, new Color(0.25f, 0.56f, 0.98f)) },
                active    = { textColor = Color.white, background = MakeTex(2, 2, new Color(0.12f, 0.38f, 0.76f)) },
                padding   = new RectOffset(12, 12, 8, 8)
            };
        }

        private static Texture2D MakeTex(int w, int h, Color col)
        {
            var pix = new Color[w * h];
            for (int i = 0; i < pix.Length; i++) pix[i] = col;
            var t = new Texture2D(w, h);
            t.SetPixels(pix);
            t.Apply();
            return t;
        }

        // ─── 顶部工具栏 ───────────────────────────────────────────────────────
        private void DrawTopToolbar()
        {
            Rect header = GUILayoutUtility.GetRect(0, 78, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(header, ZMBuildStyles.Header);
            EditorGUI.DrawRect(new Rect(header.x, header.yMax - 1, header.width, 1), ZMBuildStyles.Border);

            Rect logoBox = new Rect(header.x + 22, header.y + 18, 42, 42);
            GUI.Box(logoBox, GUIContent.none, ZMBuildStyles.BadgeBox);
            BundleAnalyzerIcons.Draw(new Rect(logoBox.x + 9, logoBox.y + 9, 24, 24),
                BundleAnalyzerIcons.Icon.Analyzer, ZMBuildStyles.Accent, 1.8f);
            GUI.Label(new Rect(header.x + 76, header.y + 13, 260, 29),
                "AssetBundle 分析中心", BundleAnalyzerStyles.HeaderTitle);
            GUI.Label(new Rect(header.x + 76, header.y + 40, 300, 20),
                "依赖检测 · 版本对比 · 资源审计", BundleAnalyzerStyles.HeaderSubtitle);

            float x = header.xMax - 22;
            Rect settingsRect = AllocateHeaderRect(ref x, 86);
            Rect cacheRect = AllocateHeaderRect(ref x, 116);
            Rect clearRect = AllocateHeaderRect(ref x, 38);
            Rect refreshRect = AllocateHeaderRect(ref x, 38);
            Rect exportRect = AllocateHeaderRect(ref x, 104);
            Rect platformRect = AllocateHeaderRect(ref x, 124);

            int newPlatform = DrawHeaderPopup(platformRect, "构建平台", _platformIdx, Platforms);
            if (newPlatform != _platformIdx)
            {
                _platformIdx = newPlatform;
                _cache.Clear();
                InvalidateAllResults();
            }

            _exportFmtIdx = DrawHeaderPopup(exportRect, "导出", _exportFmtIdx, ExportFormats);
            if (!_showSettings)
            {
                if (DrawHeaderIconButton(refreshRect, BundleAnalyzerIcons.Icon.Refresh, "刷新模块列表"))
                {
                    _cache.Clear();
                    RefreshGameList();
                }
                if (DrawHeaderIconButton(clearRect, BundleAnalyzerIcons.Icon.Clear, "清空 Manifest 与扫描缓存",
                        new Color32(235, 111, 116, 255)))
                {
                    _cache.Clear();
                    _embedCache.Clear(true);
                    InvalidateAllResults();
                }
                if (DrawHeaderTextButton(cacheRect, $"缓存 {_embedCache.Count} 条", BundleAnalyzerIcons.Icon.Cache))
                    SaveEmbedCache();
            }

            if (DrawHeaderTextButton(settingsRect, _showSettings ? "返回" : "设置",
                    _showSettings ? BundleAnalyzerIcons.Icon.Back : BundleAnalyzerIcons.Icon.Settings,
                    _showSettings))
            {
                _showSettings = !_showSettings;
                if (_showSettings) OpenSettingsEditor();
            }
        }

        private static Rect AllocateHeaderRect(ref float right, float width)
        {
            Rect rect = new Rect(right - width, 18, width, 42);
            right = rect.x - 8;
            return rect;
        }

        private int DrawHeaderPopup(Rect rect, string label, int value, string[] options)
        {
            value = ConsumePopupSelection($"header.{label}", value);
            GUI.Box(rect, GUIContent.none, ZMBuildStyles.BadgeBox);
            GUI.Label(new Rect(rect.x + 11, rect.y + 4, rect.width - 22, 12), label, BundleAnalyzerStyles.ToolbarLabel);
            Rect popup = new Rect(rect.x + 10, rect.y + 19, rect.width - 20, 19);
            DrawDarkPopupButton(popup, $"header.{label}", value, options);
            return value;
        }

        /// <summary>绘制与 ZMAsset 设置中心一致的圆角下拉框及深色弹出列表。</summary>
        private int DrawAnalyzerPopup(string key, int value, string[] options, params GUILayoutOption[] layout)
        {
            value = ConsumePopupSelection(key, value);
            Rect rect = GUILayoutUtility.GetRect(100, 34, layout);
            GUI.Box(rect, GUIContent.none, ZMBuildStyles.FieldBox);
            DrawDarkPopupButton(rect, key, value, options);
            return value;
        }

        private int ConsumePopupSelection(string key, int current)
        {
            if (!_popupSelections.TryGetValue(key, out int pending)) return current;
            _popupSelections.Remove(key);
            return pending;
        }

        private void DrawDarkPopupButton(Rect rect, string key, int value, string[] options)
        {
            if (options == null || options.Length == 0) return;
            int selected = Mathf.Clamp(value, 0, options.Length - 1);
            GUIStyle popupStyle = ZMBuildStyles.SettingsPopup;
            if (rect.height < 30f)
            {
                popupStyle = new GUIStyle(ZMBuildStyles.SettingsPopup)
                {
                    fixedHeight = 0,
                    fontSize = 12,
                    padding = new RectOffset(1, 24, 0, 0),
                    alignment = TextAnchor.MiddleLeft
                };
            }
            if (GUI.Button(rect, options[selected], popupStyle))
            {
                DarkDropdownWindow.Show(GUIUtility.GUIToScreenRect(rect), options, selected, index =>
                {
                    _popupSelections[key] = index;
                    Repaint();
                });
            }
            DrawAnalyzerPopupArrow(rect);
        }

        private static void DrawAnalyzerPopupArrow(Rect rect)
        {
            Handles.BeginGUI();
            Color previous = Handles.color;
            Handles.color = new Color32(158, 164, 174, 255);
            float x = rect.xMax - 17;
            float y = rect.center.y - 2;
            Handles.DrawAAPolyLine(1.7f, new Vector3(x - 4, y), new Vector3(x, y + 4), new Vector3(x + 4, y));
            Handles.color = previous;
            Handles.EndGUI();
        }

        private static bool DrawHeaderIconButton(Rect rect, BundleAnalyzerIcons.Icon icon, string tooltip,
            Color? color = null)
        {
            bool clicked = GUI.Button(rect, new GUIContent(string.Empty, tooltip), ZMBuildStyles.PathIconButton);
            const float iconSize = 18f;
            Rect iconRect = new Rect(
                Mathf.Round(rect.center.x - iconSize * 0.5f),
                Mathf.Round(rect.center.y - iconSize * 0.5f),
                iconSize,
                iconSize);
            BundleAnalyzerIcons.Draw(iconRect, icon,
                color ?? new Color32(187, 196, 207, 255), 1.6f);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return clicked;
        }

        private static bool DrawHeaderTextButton(Rect rect, string text, BundleAnalyzerIcons.Icon icon, bool selected = false)
        {
            GUIStyle style = selected ? ZMBuildStyles.SegmentSelected : ZMBuildStyles.BadgeBox;
            bool clicked = GUI.Button(rect, GUIContent.none, style);
            Color color = selected ? Color.white : new Color32(192, 201, 212, 255);
            GUIStyle textStyle = new GUIStyle(BundleAnalyzerStyles.ToolbarValue)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(),
                normal = { textColor = color }
            };
            float textWidth = textStyle.CalcSize(new GUIContent(text)).x;
            const float iconSize = 17f;
            const float gap = 7f;
            float groupWidth = iconSize + gap + textWidth;
            float groupX = Mathf.Round(rect.center.x - groupWidth * 0.5f);
            Rect iconRect = new Rect(groupX, Mathf.Round(rect.center.y - iconSize * 0.5f), iconSize, iconSize);
            BundleAnalyzerIcons.Draw(iconRect, icon, color, 1.5f);
            GUI.Label(new Rect(iconRect.xMax + gap, rect.y, textWidth, rect.height), text, textStyle);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return clicked;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  引导页 & 设置面板
        // ═════════════════════════════════════════════════════════════════════

        private void DrawWelcomePage() => DrawWelcome_DarkPro();

        private void DrawWelcome_DarkPro()
        {
            _welcomeScroll = EditorGUILayout.BeginScrollView(_welcomeScroll);
            GUILayout.Space(28);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(56);
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    DrawWelcomeHero();
                    GUILayout.Space(16);
                    DrawWelcomeCapabilities();
                    GUILayout.Space(16);
                    DrawWelcomeSteps();
                    GUILayout.Space(18);
                    WelcomeCTA("立即配置游戏模块", ZMBuildStyles.Accent);
                    GUILayout.Space(10);
                    GUILayout.Label("配置仅保存在本地 Library，不会修改运行时代码或资源。",
                        new GUIStyle(BundleAnalyzerStyles.PageSubtitle)
                        {
                            alignment = TextAnchor.MiddleCenter
                        }, GUILayout.Height(20));
                }
                GUILayout.Space(56);
            }
            GUILayout.Space(24);
            EditorGUILayout.EndScrollView();
        }

        private static void DrawWelcomeHero()
        {
            Rect hero = GUILayoutUtility.GetRect(0, 126, GUILayout.ExpandWidth(true));
            GUI.Box(hero, GUIContent.none, ZMBuildStyles.SettingsCard);

            Rect iconBox = new Rect(hero.x + 28, hero.y + 27, 72, 72);
            GUI.Box(iconBox, GUIContent.none, ZMBuildStyles.BadgeBox);
            BundleAnalyzerIcons.Draw(new Rect(iconBox.x + 17, iconBox.y + 17, 38, 38),
                BundleAnalyzerIcons.Icon.Analyzer, ZMBuildStyles.Accent, 2.2f);

            GUI.Label(new Rect(hero.x + 124, hero.y + 24, hero.width - 152, 34),
                "开始使用 AssetBundle 分析中心",
                new GUIStyle(BundleAnalyzerStyles.PageTitle) { fontSize = 26 });
            GUI.Label(new Rect(hero.x + 124, hero.y + 60, hero.width - 152, 22),
                "先配置游戏模块与 Bundle 根目录，即可解锁完整的资源分析工作流。",
                BundleAnalyzerStyles.PageSubtitle);

            Rect badge = new Rect(hero.x + 124, hero.y + 88, 228, 25);
            GUI.Box(badge, GUIContent.none, ZMBuildStyles.BadgeBox);
            GUI.Label(badge, "零运行时依赖  ·  仅在 Editor 中工作",
                new GUIStyle(ZMBuildStyles.SettingsHint)
                {
                    alignment = TextAnchor.MiddleCenter
                });
        }

        private static void DrawWelcomeCapabilities()
        {
            string[] titles =
            {
                "版本对比", "跨模块依赖", "全局依赖总览", "Bundle 大小统计", "资源浏览器"
            };
            string[] descriptions =
            {
                "对比两个构建版本，审计 Bundle 体积与内容变化。",
                "识别跨模块引用关系，快速定位重复打包资源。",
                "汇总全部游戏模块，集中查看依赖风险与异常项。",
                "按大小排序构建产物，快速定位异常体积 Bundle。",
                "查看 Bundle 包含内容，全局搜索并定位源资源。"
            };
            Color[] accents =
            {
                new Color32(83, 169, 246, 255),
                new Color32(239, 139, 74, 255),
                new Color32(71, 201, 181, 255),
                new Color32(235, 179, 72, 255),
                new Color32(181, 127, 244, 255)
            };
            BundleAnalyzerIcons.Icon[] icons =
            {
                BundleAnalyzerIcons.Icon.Compare,
                BundleAnalyzerIcons.Icon.Dependency,
                BundleAnalyzerIcons.Icon.Overview,
                BundleAnalyzerIcons.Icon.Size,
                BundleAnalyzerIcons.Icon.Browser
            };

            const float cardHeight = 104f;
            const float horizontalGap = 12f;
            const float verticalGap = 10f;
            Rect area = GUILayoutUtility.GetRect(0, cardHeight * 3 + verticalGap * 2,
                GUILayout.ExpandWidth(true));
            float cardWidth = (area.width - horizontalGap) * 0.5f;

            for (int i = 0; i < titles.Length; i++)
            {
                int row = i / 2;
                int column = i % 2;
                Rect card = new Rect(
                    area.x + column * (cardWidth + horizontalGap),
                    area.y + row * (cardHeight + verticalGap),
                    cardWidth,
                    cardHeight);
                DrawWelcomeCapabilityCard(card, titles[i], descriptions[i], accents[i], icons[i]);
            }
        }

        private static void DrawWelcomeCapabilityCard(Rect rect, string title, string description, Color accent,
            BundleAnalyzerIcons.Icon icon)
        {
            GUI.Box(rect, GUIContent.none, BundleAnalyzerStyles.MetricCard);
            EditorGUI.DrawRect(new Rect(rect.x + 16, rect.y + 13, 34, 3), accent);

            Rect iconBox = new Rect(rect.x + 16, rect.y + 31, 42, 42);
            GUI.Box(iconBox, GUIContent.none, ZMBuildStyles.BadgeBox);
            BundleAnalyzerIcons.Draw(new Rect(iconBox.x + 9, iconBox.y + 9, 24, 24), icon, accent, 1.8f);

            float textX = iconBox.xMax + 12;
            GUI.Label(new Rect(textX, rect.y + 25, rect.xMax - textX - 16, 25), title,
                new GUIStyle(ZMBuildStyles.SettingsSectionTitle)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleLeft
                });
            GUI.Label(new Rect(textX, rect.y + 54, rect.xMax - textX - 16, 38), description,
                new GUIStyle(BundleAnalyzerStyles.PageSubtitle)
                {
                    fontSize = 12,
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft
                });
        }

        private static void DrawWelcomeCapabilityRow(
            (string title, string description, Color accent) left,
            (string title, string description, Color accent) right)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawWelcomeCapability(left.title, left.description, left.accent);
                GUILayout.Space(12);
                DrawWelcomeCapability(right.title, right.description, right.accent);
            }
        }

        private static void DrawWelcomeCapability(string title, string description, Color accent,
            params GUILayoutOption[] options)
        {
            using (new EditorGUILayout.VerticalScope(BundleAnalyzerStyles.MetricCard,
                       CombineWelcomeLayout(options)))
            {
                Rect marker = GUILayoutUtility.GetRect(0, 3, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(new Rect(marker.x, marker.y, 34, 3), accent);
                GUILayout.Space(10);
                GUILayout.Label(title, new GUIStyle(ZMBuildStyles.SettingsSectionTitle)
                {
                    fontSize = 16,
                    alignment = TextAnchor.MiddleLeft
                });
                GUILayout.Space(5);
                GUILayout.Label(description, new GUIStyle(BundleAnalyzerStyles.PageSubtitle)
                {
                    fontSize = 12,
                    wordWrap = true
                });
            }
        }

        private static GUILayoutOption[] CombineWelcomeLayout(GUILayoutOption[] options)
        {
            var result = new List<GUILayoutOption>
            {
                GUILayout.Height(104),
                GUILayout.ExpandWidth(true)
            };
            if (options != null) result.AddRange(options);
            return result.ToArray();
        }

        private static void DrawWelcomeSteps()
        {
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                GUILayout.Label("三步完成配置", ZMBuildStyles.SettingsSectionTitle);
                GUILayout.Label("分析器读取现有构建产物，不会改变你的 Bundle 构建结果。",
                    BundleAnalyzerStyles.PageSubtitle);
                GUILayout.Space(10);
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawWelcomeStep("1", "设置根目录", "选择 AssetBundle 输出根目录");
                    DrawWelcomeStepDivider();
                    DrawWelcomeStep("2", "添加游戏模块", "填写模块名与对应子目录");
                    DrawWelcomeStepDivider();
                    DrawWelcomeStep("3", "保存并分析", "自动加载 Manifest 数据");
                }
            }
        }

        private static void DrawWelcomeStep(string number, string title, string description)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandWidth(true)))
            {
                Rect numberBox = GUILayoutUtility.GetRect(30, 30, GUILayout.Width(30), GUILayout.Height(30));
                GUI.Box(numberBox, GUIContent.none, ZMBuildStyles.BadgeBox);
                GUI.Label(numberBox, number, new GUIStyle(BundleAnalyzerStyles.ToolbarValue)
                {
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset()
                });
                GUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(title, EditorStyles.boldLabel);
                    GUILayout.Label(description, ZMBuildStyles.SettingsHint);
                }
            }
        }

        private static void DrawWelcomeStepDivider()
        {
            Rect line = GUILayoutUtility.GetRect(34, 30, GUILayout.Width(34), GUILayout.Height(30));
            EditorGUI.DrawRect(new Rect(line.x + 6, line.center.y, line.width - 12, 1), ZMBuildStyles.Border);
        }

        // Unity 内置图标名（d_前缀=深色主题兼容）
        private static readonly string[] CardIconNames =
        {
            "d_tab_next@2x",          // 版本对比：箭头/切换
            "d_UnityEditor.SceneHierarchyWindow", // 跨游戏依赖：层级
            "d_LayoutDropDown@2x",    // 全局依赖总览：全局布局
            "d_Profiler.Memory@2x",   // 大小统计：内存分析
            "d_Project@2x",           // 资源浏览：项目窗口
        };
        private Texture2D[] _cardIcons; // 缓存

        private Texture2D GetCardIcon(int idx)
        {
            if (_cardIcons == null) _cardIcons = new Texture2D[CardIconNames.Length];
            if (_cardIcons[idx] != null) return _cardIcons[idx];
            var content = EditorGUIUtility.IconContent(CardIconNames[idx]);
            _cardIcons[idx] = content?.image as Texture2D;
            return _cardIcons[idx];
        }

        private void WelcomeCards(Color[] accents, Color borderColor, Color titleColor, Color descColor)
        {
            string[] titles = { "版本对比", "跨游戏依赖", "全局依赖总览", "大小统计", "资源浏览" };
            string[] descs  = {
                "对比两次打包 Bundle 的大小变化，支持柱状图与 CSV 导出。",
                "检测 Bundle 是否引用了其他模块资源，精准定位跨包冗余。",
                "一键扫描全部游戏模块，汇总跨游戏依赖情况总览。",
                "扫描全部 Bundle 磁盘占用，多维度排序，快速识别超标。",
                "浏览 Bundle 内资源，全局关键字搜索，一键定位资产。",
            };
            string[] fallbackLetters = { "V", "D", "G", "S", "A" };

            int   count  = titles.Length;
            int   cols   = 2;
            int   rows   = (count + cols - 1) / cols;
            float rowH   = 90f;
            float gap    = 12f;
            float rowGap = 10f;
            float side   = 8f;
            float totalH = rows * rowH + (rows - 1) * rowGap;

            Rect area = EditorGUILayout.GetControlRect(false, totalH);
            float cw  = (area.width - side * 2 - gap) / cols;

            for (int i = 0; i < count; i++)
            {
                int row = i / cols;
                int col = i % cols;
                float xStart;
                int thisRowCols = (row == rows - 1 && count % cols == 1) ? 1 : cols;
                xStart = thisRowCols == 1
                    ? area.x + side
                    : area.x + side + col * (cw + gap);

                Rect r = new Rect(xStart, area.y + row * (rowH + rowGap), cw, rowH);

                // 细边框
                EditorGUI.DrawRect(new Rect(r.x,        r.y,        r.width, 1), borderColor);
                EditorGUI.DrawRect(new Rect(r.x,        r.yMax - 1, r.width, 1), borderColor);
                EditorGUI.DrawRect(new Rect(r.x,        r.y,        1, r.height), borderColor);
                EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y,        1, r.height), borderColor);
                EditorGUI.DrawRect(new Rect(r.x + 1,    r.y + 1,    r.width - 2, 2),
                    new Color(accents[i].r, accents[i].g, accents[i].b, 0.7f));

                // 图标区：优先 Unity 内置图标，否则彩色字母方块
                Rect iconRect = new Rect(r.x + 14, r.y + (r.height - 32) / 2f, 32, 32);
                var  tex      = GetCardIcon(i);
                if (tex != null)
                {
                    var prevColor = GUI.color;
                    GUI.color = accents[i];
                    GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
                    GUI.color = prevColor;
                }
                else
                {
                    EditorGUI.DrawRect(iconRect, new Color(accents[i].r, accents[i].g, accents[i].b, 0.20f));
                    EditorGUI.DrawRect(new Rect(iconRect.x, iconRect.y, iconRect.width, 1), accents[i]);
                    EditorGUI.DrawRect(new Rect(iconRect.x, iconRect.yMax - 1, iconRect.width, 1), accents[i]);
                    EditorGUI.DrawRect(new Rect(iconRect.x, iconRect.y, 1, iconRect.height), accents[i]);
                    EditorGUI.DrawRect(new Rect(iconRect.xMax - 1, iconRect.y, 1, iconRect.height), accents[i]);
                    var fb = new GUIStyle(EditorStyles.label) { fontSize = 16, fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter, normal = { textColor = accents[i] } };
                    GUI.Label(iconRect, fallbackLetters[i], fb);
                }

                // 标题 + 描述（x轴对齐）
                float textX = iconRect.xMax + 10;
                float textW = r.xMax - textX - 10;
                var titleStyle = new GUIStyle(_featureCardTitle) { normal = { textColor = titleColor }, fontSize = 16,
                    fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
                var descStyle = new GUIStyle(_featureCardDesc) { normal = { textColor = descColor }, fontSize = 12,
                    wordWrap = true };
                GUI.Label(new Rect(textX, r.y + 14, textW, 28), titles[i], titleStyle);
                GUI.Label(new Rect(textX, r.y + 48, textW, r.height - 54), descs[i], descStyle);
            }
        }

        private void WelcomeChecklist(Color borderColor, Color textColor)
        {
            string[] lines = {
                "✔  各游戏模块已打包 AssetBundle 到  StreamingAssets/AssetBundle/<模块名>/  目录",
                "✔  每个模块目录内包含与目录同名的 Manifest 文件（无后缀）",
                "✔  点击右上角  ⚙ 设置  填写 AB 根目录路径并添加游戏模块，即可解锁全部功能",
            };
            var style = new GUIStyle(_checkItem) { normal = { textColor = textColor }, fontSize = 12 };
            var header = new GUIStyle(_checkItem) { fontStyle = FontStyle.Bold, fontSize = 13,
                normal = { textColor = new Color(Mathf.Min(textColor.r * 1.3f, 1f),
                    Mathf.Min(textColor.g * 1.3f, 1f), Mathf.Min(textColor.b * 1.3f, 1f)) } };
            Rect box = EditorGUILayout.GetControlRect(false, 100);
            EditorGUI.DrawRect(new Rect(box.x,        box.y,        box.width, 1), borderColor);
            EditorGUI.DrawRect(new Rect(box.x,        box.yMax - 1, box.width, 1), borderColor);
            EditorGUI.DrawRect(new Rect(box.x,        box.y,        1, box.height), borderColor);
            EditorGUI.DrawRect(new Rect(box.xMax - 1, box.y,        1, box.height), borderColor);
            GUI.Label(new Rect(box.x + 12, box.y +  6, box.width - 24, 20), "使用前请确认", header);
            for (int i = 0; i < lines.Length; i++)
                GUI.Label(new Rect(box.x + 12, box.y + 28 + i * 23, box.width - 24, 20), lines[i], style);
        }

        private void WelcomeCTA(string label, Color accent)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(label, ZMBuildStyles.PrimaryButton, GUILayout.Height(44), GUILayout.Width(230)))
            {
                _showSettings = true;
                OpenSettingsEditor();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void OpenSettingsEditor()
        {
            _cfgEditRootPath = _viewerConfig.abRootPath ?? "StreamingAssets/AssetBundle";
            _cfgEditModules.Clear();
            if (_viewerConfig.modules != null)
            {
                foreach (var m in _viewerConfig.modules)
                    _cfgEditModules.Add(new GameModuleConfig { moduleName = m.moduleName, abSubFolder = m.abSubFolder });
            }
        }

        private void DrawSettingsPanel()
        {
            GUILayout.Space(24);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(42);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label("工具配置", BundleAnalyzerStyles.PageTitle, GUILayout.Height(34));
                    GUILayout.Label("配置 AssetBundle 构建产物位置与需要分析的游戏模块。",
                        BundleAnalyzerStyles.PageSubtitle, GUILayout.Height(22));
                    GUILayout.Space(12);

                    DrawSettingsRootPath();
                    GUILayout.Space(12);
                    DrawSettingsModules();
                    GUILayout.Space(14);
                    DrawSettingsActions();
                }
                GUILayout.Space(42);
            }
        }

        private void DrawSettingsRootPath()
        {
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                GUILayout.Label("Bundle 根目录", ZMBuildStyles.SettingsSectionTitle);
                GUILayout.Label("填写相对于 Assets 的路径，每个模块对应根目录下的一个子目录。",
                    BundleAnalyzerStyles.PageSubtitle);
                GUILayout.Space(10);

                using (new EditorGUILayout.HorizontalScope(GUILayout.Height(36)))
                {
                    Rect field = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
                    _cfgEditRootPath = ZMBuildStyles.DrawTextField(field,
                        _cfgEditRootPath ?? string.Empty, ZMBuildStyles.InputField);
                    GUILayout.Space(8);
                    if (GUILayout.Button("选择目录", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(96)))
                    {
                        string initial = Path.Combine(Application.dataPath,
                            _cfgEditRootPath ?? "StreamingAssets/AssetBundle");
                        string selected = EditorUtility.OpenFolderPanel("选择 AssetBundle 根目录", initial, string.Empty);
                        if (!string.IsNullOrEmpty(selected))
                        {
                            string assets = Application.dataPath.Replace('\\', '/').TrimEnd('/');
                            string normalized = selected.Replace('\\', '/');
                            _cfgEditRootPath = normalized.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)
                                ? normalized.Substring(assets.Length + 1)
                                : normalized;
                        }
                    }
                }

                string preview = Path.Combine(Application.dataPath,
                    _cfgEditRootPath ?? "StreamingAssets/AssetBundle").Replace('\\', '/');
                GUILayout.Label($"实际目录：{preview}", ZMBuildStyles.SettingsHint);
            }
        }

        private void DrawSettingsModules()
        {
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        GUILayout.Label("游戏模块", ZMBuildStyles.SettingsSectionTitle);
                        GUILayout.Label($"已配置 {_cfgEditModules.Count} 个模块", BundleAnalyzerStyles.PageSubtitle);
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("扫描目录", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(92)))
                        ScanSettingsModules();
                    GUILayout.Space(8);
                    if (GUILayout.Button("添加模块", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(92)))
                        _cfgEditModules.Add(new GameModuleConfig());
                }
                GUILayout.Space(10);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("模块名称", ZMBuildStyles.SettingsHint, GUILayout.Width(220));
                    GUILayout.Label("Bundle 子目录（留空时使用模块名称）", ZMBuildStyles.SettingsHint);
                    GUILayout.Space(44);
                }
                GUILayout.Space(4);

                _cfgScroll = EditorGUILayout.BeginScrollView(_cfgScroll,
                    GUILayout.MinHeight(190), GUILayout.MaxHeight(Mathf.Max(220, position.height - 440)));
                if (_cfgEditModules.Count == 0)
                {
                    GUILayout.Label("还没有模块。点击“添加模块”，或从根目录自动扫描。",
                        BundleAnalyzerStyles.InfoBox, GUILayout.Height(54));
                }
                for (int i = 0; i < _cfgEditModules.Count; i++)
                {
                    if (DrawSettingsModuleRow(i)) break;
                    GUILayout.Space(7);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private bool DrawSettingsModuleRow(int index)
        {
            using (new EditorGUILayout.HorizontalScope(ZMBuildStyles.PathRow, GUILayout.Height(50)))
            {
                GUILayout.Label((index + 1).ToString("00"), ZMBuildStyles.StatusLabel, GUILayout.Width(32));
                Rect nameField = GUILayoutUtility.GetRect(180, 34, GUILayout.Width(180));
                _cfgEditModules[index].moduleName = ZMBuildStyles.DrawTextField(nameField,
                    _cfgEditModules[index].moduleName ?? string.Empty, ZMBuildStyles.InputField);
                GUILayout.Space(10);
                Rect pathField = GUILayoutUtility.GetRect(180, 34, GUILayout.ExpandWidth(true));
                _cfgEditModules[index].abSubFolder = ZMBuildStyles.DrawTextField(pathField,
                    _cfgEditModules[index].abSubFolder ?? string.Empty, ZMBuildStyles.InputField);
                GUILayout.Space(8);
                if (GUILayout.Button("×", ZMBuildStyles.PathDeleteButton, GUILayout.Width(34), GUILayout.Height(34)))
                {
                    _cfgEditModules.RemoveAt(index);
                    return true;
                }
            }
            return false;
        }

        private void ScanSettingsModules()
        {
            string abRoot = Path.Combine(Application.dataPath,
                    _cfgEditRootPath ?? "StreamingAssets/AssetBundle")
                .Replace('/', Path.DirectorySeparatorChar);
            if (!Directory.Exists(abRoot))
            {
                EditorUtility.DisplayDialog("目录不可用", $"没有找到目录：\n{abRoot}", "确定");
                return;
            }

            var existing = new HashSet<string>(_cfgEditModules.Select(m => m.moduleName ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            foreach (string directory in Directory.GetDirectories(abRoot).OrderBy(path => path))
            {
                string name = Path.GetFileName(directory);
                if (existing.Add(name))
                    _cfgEditModules.Add(new GameModuleConfig { moduleName = name });
            }
        }

        private void DrawSettingsActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("配置将在保存后立即重新加载分析数据。", ZMBuildStyles.SettingsHint);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("取消", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(88)))
                    _showSettings = false;
                GUILayout.Space(8);
                if (GUILayout.Button("保存并应用", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(118)))
                {
                    _viewerConfig.abRootPath = _cfgEditRootPath ?? "StreamingAssets/AssetBundle";
                    _viewerConfig.modules.Clear();
                    foreach (GameModuleConfig module in _cfgEditModules)
                        if (!string.IsNullOrWhiteSpace(module.moduleName))
                            _viewerConfig.modules.Add(new GameModuleConfig
                            {
                                moduleName = module.moduleName.Trim(),
                                abSubFolder = module.abSubFolder?.Trim()
                            });
                    SaveViewerConfig();
                    _showSettings = false;
                    _cache.Clear();
                    RefreshGameList();
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Tab1：AB 版本对比
        // ═════════════════════════════════════════════════════════════════════

        private static string CsvEscape(string val)
        {
            if (string.IsNullOrEmpty(val)) return string.Empty;
            if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
                return $"\"{val.Replace("\"", "\"\"")}\"";
            return val;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  辅助方法
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 判断一个 dep bundle name 是否属于其他游戏（跨游戏依赖）
        /// 规则：dep 名称以某个 otherPrefixes 中的前缀开头，且不以 sourcePrefix 开头
        /// </summary>
        private static bool IsCrossGame(string dep, string srcPrefix, string[] otherPrefixes)
        {
            if (string.IsNullOrEmpty(dep)) return false;
            string depLow = dep.ToLower();
            if (depLow.StartsWith(srcPrefix)) return false;
            return otherPrefixes.Any(p => depLow.StartsWith(p));
        }

        private static string GuessGamePrefix(string depName, string[] otherPrefixes)
        {
            string low = depName.ToLower();
            return otherPrefixes.FirstOrDefault(p => low.StartsWith(p));
        }

        private static void DrawSectionHeader(string title, Color color)
        {
            EditorGUILayout.LabelField(title,
                new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = color } });
        }

        // ═════════════════════════════════════════════════════════════════════
        //  数据模型
        // ═════════════════════════════════════════════════════════════════════

        private class GameManifestData
        {
            /// <summary>key=bundleName, value=直接依赖列表</summary>
            public readonly Dictionary<string, string[]> DirectDeps =
                new(StringComparer.OrdinalIgnoreCase);

            /// <summary>key=bundleName, value=所有传递依赖列表（含间接）</summary>
            public readonly Dictionary<string, string[]> AllDeps =
                new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
#endif
