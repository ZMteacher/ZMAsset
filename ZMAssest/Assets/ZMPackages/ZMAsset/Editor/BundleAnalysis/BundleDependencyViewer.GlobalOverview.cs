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
    /// BundleDependencyViewer 的独立功能页面。业务状态由主窗口持有，
    /// 本文件仅维护该页面的交互、分析调度与结果展示。
    /// </summary>
    public partial class BundleDependencyViewer
    {
        private struct GameSummary
        {
            public string GameName;
            public bool   LoadFailed;
            public int    TotalBundles;
            public int    BundlesWithCross;
            public int    TotalCrossCount;
            public string CrossBreakdown;
            // Deep scan: bundles with embedded other-game assets
            public int    EmbeddedBundleCount;
            public int    EmbeddedAssetCount;
            public string EmbeddedBreakdown;
        }

        private void DrawTab3_GlobalOverview()
        {
            EditorGUILayout.LabelField("全局依赖总览", BundleAnalyzerStyles.PageTitle, GUILayout.Height(32));
            EditorGUILayout.LabelField("从全局视角查看模块依赖风险与资源冗余情况", BundleAnalyzerStyles.PageSubtitle,
                GUILayout.Height(20));
            GUILayout.Space(12);

            using (new EditorGUILayout.HorizontalScope(ZMBuildStyles.SettingsCard))
            {
                Rect deepScanToggle = GUILayoutUtility.GetRect(
                    108, 34, GUILayout.Width(108), GUILayout.Height(34));
                _t3DeepScan = DrawAnalyzerToggle(
                    deepScanToggle,
                    new GUIContent("深度扫描", "加载每个 Bundle 并检查资源路径，耗时较长。"),
                    _t3DeepScan);
                GUIStyle scanModeLabel = new GUIStyle(BundleAnalyzerStyles.PageSubtitle)
                {
                    alignment = TextAnchor.MiddleLeft
                };
                GUILayout.Label(_t3DeepScan ? "包含直接嵌入资源检查" : "仅检查 Manifest 依赖",
                    scanModeLabel, GUILayout.Width(220), GUILayout.Height(34));
                GUILayout.FlexibleSpace();
                if (!_t3Dirty && _t3Summary.Count > 0 &&
                    GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}", ZMBuildStyles.CompactSecondaryButton,
                        GUILayout.Width(88)))
                    ExportTab3();
                GUILayout.Space(8);
                bool hasScanResult = !_t3Dirty && _t3Summary.Count > 0;
                if (GUILayout.Button(hasScanResult ? "重新扫描" : "扫描全部模块",
                        hasScanResult ? ZMBuildStyles.CompactSecondaryButton : ZMBuildStyles.CompactPrimaryButton,
                        GUILayout.Width(112)))
                    RunGlobalScan();
            }

            GUILayout.Space(12);
            int totalBundles = _t3Summary.Where(s => !s.LoadFailed).Sum(s => s.TotalBundles);
            int crossCount = _t3Summary.Sum(s => s.TotalCrossCount);
            int embeddedCount = _t3Summary.Sum(s => s.EmbeddedAssetCount);
            int dirtyCount = _t3Summary.Count(s => !s.LoadFailed &&
                (s.BundlesWithCross > 0 || s.EmbeddedBundleCount > 0));
            DrawGlobalMetrics(totalBundles, crossCount, embeddedCount, dirtyCount);

            GUILayout.Space(12);
            if (_t3Dirty || _t3Summary.Count == 0)
            {
                GUILayout.Label("尚未生成全局分析数据，点击“扫描全部模块”开始检查。",
                    BundleAnalyzerStyles.InfoBox, GUILayout.Height(48));
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard, GUILayout.ExpandWidth(true)))
                {
                    GUILayout.Label("模块依赖概览", ZMBuildStyles.SettingsSectionTitle);
                    GUILayout.Label("按模块汇总 Bundle 数量与风险依赖", BundleAnalyzerStyles.PageSubtitle);
                    GUILayout.Space(8);
                    _t3Scroll = EditorGUILayout.BeginScrollView(_t3Scroll);
                    foreach (GameSummary summary in _t3Summary)
                        DrawGlobalModuleRow(summary);
                    EditorGUILayout.EndScrollView();
                }

                GUILayout.Space(12);
                using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard, GUILayout.Width(300)))
                {
                    GUILayout.Label("风险模块", ZMBuildStyles.SettingsSectionTitle);
                    GUILayout.Label("按问题数量降序排列", BundleAnalyzerStyles.PageSubtitle);
                    GUILayout.Space(8);
                    foreach (GameSummary summary in _t3Summary
                                 .Where(s => !s.LoadFailed)
                                 .OrderByDescending(s => s.TotalCrossCount + s.EmbeddedAssetCount)
                                 .Take(6))
                        DrawRiskRow(summary);
                }
            }
        }

        private void DrawGlobalMetrics(int bundles, int cross, int embedded, int dirty)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawMetric("游戏模块", _gameNames.Length.ToString(), new Color32(63, 165, 246, 255));
                GUILayout.Space(10);
                DrawMetric("Bundle 总数", bundles.ToString("N0"), new Color32(67, 198, 190, 255));
                GUILayout.Space(10);
                DrawMetric("跨模块依赖", cross.ToString(), new Color32(238, 112, 116, 255));
                GUILayout.Space(10);
                DrawMetric("风险模块", dirty.ToString(), new Color32(236, 174, 66, 255),
                    embedded > 0 ? $"嵌入资源 {embedded}" : "无嵌入资源");
            }
        }

        private static void DrawMetric(string label, string value, Color accent, string hint = null)
        {
            using (new EditorGUILayout.VerticalScope(BundleAnalyzerStyles.MetricCard, GUILayout.Height(82),
                       GUILayout.ExpandWidth(true)))
            {
                Rect marker = GUILayoutUtility.GetRect(0, 3, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(new Rect(marker.x, marker.y, 34, 3), accent);
                GUILayout.Space(3);
                GUILayout.Label(label, BundleAnalyzerStyles.MetricLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(value, BundleAnalyzerStyles.MetricValue);
                    GUILayout.FlexibleSpace();
                    if (!string.IsNullOrEmpty(hint))
                        GUILayout.Label(hint, BundleAnalyzerStyles.PageSubtitle);
                }
            }
        }

        private void DrawGlobalModuleRow(GameSummary summary)
        {
            bool risky = summary.LoadFailed || summary.TotalCrossCount > 0 || summary.EmbeddedAssetCount > 0;
            using (new EditorGUILayout.HorizontalScope(BundleAnalyzerStyles.ListRow, GUILayout.Height(54)))
            {
                GUILayout.Label(summary.LoadFailed ? "×" : risky ? "!" : "✓",
                    new GUIStyle(risky ? _styleBoldRed : _styleGreen)
                    {
                        alignment = TextAnchor.MiddleCenter
                    }, GUILayout.Width(22), GUILayout.Height(38));
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(summary.GameName, new GUIStyle(EditorStyles.boldLabel)
                    {
                        alignment = TextAnchor.MiddleLeft
                    }, GUILayout.Height(19));
                    GUILayout.Label(summary.LoadFailed
                            ? "Manifest 加载失败"
                            : $"{summary.TotalBundles} Bundles · {summary.BundlesWithCross} 个依赖风险 · {summary.EmbeddedAssetCount} 个嵌入资源",
                        BundleAnalyzerStyles.PageSubtitle, GUILayout.Height(19));
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(84)))
                {
                    GUILayout.Space(3);
                    if (!summary.LoadFailed && GUILayout.Button("查看详情", ZMBuildStyles.CompactSecondaryButton,
                            GUILayout.Width(84), GUILayout.Height(30)))
                        JumpToTab2(summary.GameName);
                }
            }
            GUILayout.Space(6);
        }

        private static void DrawRiskRow(GameSummary summary)
        {
            int risk = summary.TotalCrossCount + summary.EmbeddedAssetCount;
            using (new EditorGUILayout.VerticalScope(BundleAnalyzerStyles.ListRow))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(summary.GameName, EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(risk.ToString(), risk > 0 ? new GUIStyle(EditorStyles.boldLabel)
                    {
                        normal = { textColor = new Color32(238, 112, 116, 255) }
                    } : BundleAnalyzerStyles.PageSubtitle);
                }
                Rect track = GUILayoutUtility.GetRect(0, 5, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(track, new Color32(45, 49, 56, 255));
                float ratio = Mathf.Clamp01(risk / 10f);
                EditorGUI.DrawRect(new Rect(track.x, track.y, track.width * ratio, track.height),
                    risk > 4 ? new Color32(238, 99, 105, 255) : new Color32(232, 164, 67, 255));
            }
            GUILayout.Space(6);
        }

        private void RunGlobalScan()
        {
            _t3Summary.Clear();

            // ── 第一阶段：Manifest 层扫描 ──
            foreach (var gameName in _gameNames)
            {
                var md = LoadOrGetManifest(gameName);
                if (md == null)
                {
                    _t3Summary.Add(new GameSummary { GameName = gameName, LoadFailed = true });
                    continue;
                }

                string srcPfx   = gameName.ToLower();
                string[] others = _gameNames
                    .Where(n => !n.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.ToLower()).ToArray();

                int bundlesWithCross = 0;
                var allCrossDeps = new List<string>();

                foreach (var kvp in md.AllDeps)
                {
                    var cross = kvp.Value.Where(dep => IsCrossGame(dep, srcPfx, others)).ToArray();
                    if (cross.Length > 0)
                    {
                        bundlesWithCross++;
                        allCrossDeps.AddRange(cross);
                    }
                }

                // 聚合：按目标游戏前缀分组，统计引用数
                string breakdown = string.Empty;
                if (allCrossDeps.Count > 0)
                {
                    var groups = allCrossDeps
                        .GroupBy(dep => GuessGamePrefix(dep, others))
                        .Where(g => g.Key != null)
                        .Select(g => $"{g.Key}({g.Count()}条)")
                        .OrderByDescending(s => s);
                    breakdown = string.Join("  ", groups);
                }

                _t3Summary.Add(new GameSummary
                {
                    GameName         = gameName,
                    TotalBundles     = md.AllDeps.Count,
                    BundlesWithCross = bundlesWithCross,
                    TotalCrossCount  = allCrossDeps.Count,
                    CrossBreakdown   = breakdown
                });
            }

            // ── 第二阶段：深度扫描（可选）──
            if (_t3DeepScan)
            {
                // Collect all (game, bundle) pairs that need scanning
                var tasks = new List<(string gameName, string gameDir, string bundleName, List<(string,string)> keywords)>();
                foreach (var gameName in _gameNames.Where((n, i) => _gameHasAb[i]))
                {
                    var md = LoadOrGetManifest(gameName);
                    if (md == null) continue;
                    string gameDir = Path.Combine(AbRootFullPath, gameName);
                    // keywords for this game = all other games' path segments
                    var kw = BuildOtherGameKeywordsForGame(gameName);
                    foreach (var bundleName in md.AllDeps.Keys)
                        tasks.Add((gameName, gameDir, bundleName, kw));
                }

                // Map from gameName → summary index
                var summaryIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _t3Summary.Count; i++)
                    summaryIdx[_t3Summary[i].GameName] = i;

                // Per-game accumulators: embeddedBundles count, embeddedAssets by target game
                var embBundleCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var embAssetCount  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var embByTarget    = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    for (int ti = 0; ti < tasks.Count; ti++)
                    {
                        var (gameName, gameDir, bundleName, kw) = tasks[ti];
                        EditorUtility.DisplayProgressBar(
                            "全局深度扫描",
                            $"[{ti + 1}/{tasks.Count}] {gameName}/{bundleName}",
                            (float)(ti + 1) / tasks.Count);

                        // Check cache first — skip loading if known clean
                        bool hasEmbed = GetOrComputeEmbedded(gameName, bundleName, kw);
                        if (!hasEmbed) continue;

                        // Bundle has embedded assets — load again for breakdown detail
                        embBundleCount[gameName] = (embBundleCount.TryGetValue(gameName, out var bc) ? bc : 0) + 1;

                        string bundlePath = Path.Combine(gameDir, bundleName);
                        AssetBundle ab = null;
                        try
                        {
                            ab = AssetBundle.LoadFromFile(bundlePath);
                            if (ab == null) continue;
                            foreach (var assetPath in ab.GetAllAssetNames())
                            {
                                string pathLow = assetPath.ToLower();
                                foreach (var (keyword, targetGame) in kw)
                                {
                                    if (!pathLow.Contains(keyword)) continue;
                                    embAssetCount[gameName] = (embAssetCount.TryGetValue(gameName, out var ac) ? ac : 0) + 1;
                                    if (!embByTarget.TryGetValue(gameName, out var tmap))
                                        embByTarget[gameName] = tmap = new Dictionary<string, int>();
                                    tmap[targetGame] = (tmap.TryGetValue(targetGame, out var tc) ? tc : 0) + 1;
                                    break;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[BundleDependencyViewer] 全局深度扫描跳过 {gameName}/{bundleName}: {e.Message}");
                        }
                        finally
                        {
                            ab?.Unload(true);
                        }
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                    if (_embedCache.IsDirty) SaveEmbedCache();
                }

                // Write back into summaries
                for (int i = 0; i < _t3Summary.Count; i++)
                {
                    var s = _t3Summary[i];
                    if (s.LoadFailed) continue;
                    s.EmbeddedBundleCount = embBundleCount.TryGetValue(s.GameName, out var ebc) ? ebc : 0;
                    s.EmbeddedAssetCount  = embAssetCount.TryGetValue(s.GameName,  out var eac) ? eac : 0;
                    if (embByTarget.TryGetValue(s.GameName, out var tmap))
                        s.EmbeddedBreakdown = string.Join("  ", tmap.Select(kv => $"{kv.Key}({kv.Value}条)").OrderByDescending(x => x));
                    _t3Summary[i] = s;
                }
            }

            // 有冗余的排前面
            _t3Summary.Sort((a, b) => (b.BundlesWithCross + b.EmbeddedBundleCount).CompareTo(a.BundlesWithCross + a.EmbeddedBundleCount));
            _t3Dirty = false;
        }

        /// <summary>Build path keywords for all OTHER games relative to <paramref name="srcGameName"/>.</summary>
        private List<(string keyword, string gameName)> BuildOtherGameKeywordsForGame(string srcGameName)
        {
            var result = new List<(string, string)>();
            for (int i = 0; i < _gameNames.Length; i++)
            {
                if (_gameNames[i].Equals(srcGameName, StringComparison.OrdinalIgnoreCase)) continue;
                string modName = _gameNames[i];
                string display = _gameDisplayNames[i];
                string shortName = modName.ToLower().EndsWith("game")
                    ? modName.Substring(0, modName.Length - 4).ToLower()
                    : modName.ToLower();
                result.Add(($"/{shortName}/", display));
                if (shortName != modName.ToLower())
                    result.Add(($"/{modName.ToLower()}/", display));
            }
            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Tab5：资源浏览器
        // ═════════════════════════════════════════════════════════════════════


    }
}
#endif
