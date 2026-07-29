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
        private const float BundleSizeMetricsLeftOffset = 32f;
        private const float BundleSizeHeaderAlignmentOffset = 17f;
        private const float BundleSizeRiskColumnRightOffset = 5f;

        private struct BundleSizeEntry
        {
            public string GameName;
            public string BundleName;
            public long   Bytes;
            public bool   HasCrossDep;       // Manifest-level cross-game bundle dependency
            public bool   HasEmbeddedAsset;  // Deep scan: embedded other-game asset paths

            public float  KB => Bytes / 1024f;
            public float  MB => Bytes / 1048576f;
            public string SizeStr => Bytes >= 1048576
                ? $"{MB:F2} MB"
                : Bytes >= 1024 ? $"{KB:F1} KB" : $"{Bytes} B";
        }

        private void DrawTab4_BundleSize()
        {
            // ── 参数面板 ──
            EditorGUILayout.BeginVertical(ZMBuildStyles.SettingsCard);
            EditorGUILayout.LabelField("Bundle 大小统计", BundleAnalyzerStyles.PageTitle, GUILayout.Height(32));
            EditorGUILayout.LabelField("展示各游戏 .uab 文件大小排行，可按大小筛选并一键导出。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(38));
            EditorGUILayout.Space(4);

            if (_gameNames.Length == 0)
            {
                EditorGUILayout.HelpBox("未找到游戏目录。", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            Rect showAllToggle = GUILayoutUtility.GetRect(
                150, 34, GUILayout.Width(150), GUILayout.Height(34));
            bool newShowAll = DrawAnalyzerToggle(
                showAllToggle, new GUIContent("查看全部游戏"), _t4ShowAll);
            if (newShowAll != _t4ShowAll) { _t4ShowAll = newShowAll; }
            if (!_t4ShowAll)
            {
                GUILayout.Space(8);
                int newGame = DrawAnalyzerPopup("size.game", _t4GameIdx, _gameNames, GUILayout.MinWidth(160));
                if (newGame != _t4GameIdx) { _t4GameIdx = newGame; }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("排序方式", new GUIStyle(ZMBuildStyles.SettingsLabel)
            {
                alignment = TextAnchor.MiddleCenter
            }, GUILayout.Width(66), GUILayout.Height(34));
            _t4SortMode = DrawAnalyzerPopup("size.sort", _t4SortMode,
                new[] { "大小降序", "大小升序", "名称", "游戏+名称" }, GUILayout.Width(132));
            EditorGUILayout.EndHorizontal();

            bool newOnlyCross = EditorGUILayout.ToggleLeft("只看有问题的 Bundle（Manifest 冗余 / 嵌入跨游戏资源）", _t4OnlyCross);
            if (newOnlyCross != _t4OnlyCross) { _t4OnlyCross = newOnlyCross; }

            EditorGUILayout.Space(4);
            bool hasStatistics = !_t4NeedScan && _t4Entries.Count > 0;
            if (GUILayout.Button(hasStatistics ? "重新统计" : "统计 Bundle 大小",
                    hasStatistics ? ZMBuildStyles.CompactSecondaryButton : ZMBuildStyles.CompactPrimaryButton,
                    GUILayout.Height(34))) RunBundleSizeScan();
            EditorGUILayout.EndVertical();

            if (_t4NeedScan || _t4Entries.Count == 0) return;

            // ── 筛选 ──
            var display = _t4Entries.AsEnumerable();
            if (!_t4ShowAll)
                display = display.Where(e => e.GameName == _gameNames[_t4GameIdx]);
            if (_t4OnlyCross)
                display = display.Where(e => e.HasCrossDep || e.HasEmbeddedAsset);
            display = _t4SortMode switch
            {
                0 => display.OrderByDescending(e => e.Bytes),
                1 => display.OrderBy(e => e.Bytes),
                2 => display.OrderBy(e => e.BundleName),
                _ => display.OrderBy(e => e.GameName).ThenBy(e => e.BundleName)
            };

            var list = display.ToList();
            if (list.Count == 0)
            {
                EditorGUILayout.HelpBox("没有符合条件的 Bundle。", MessageType.Info);
                return;
            }

            long totalBytes = list.Sum(e => e.Bytes);

            // ── 汇总 header ──
            EditorGUILayout.BeginHorizontal(ZMBuildStyles.BadgeBox);
            EditorGUILayout.LabelField($"共 {list.Count} 个 Bundle   合计: {FormatBytes(totalBytes)}",
                new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft },
                GUILayout.ExpandWidth(true), GUILayout.Height(34));
            if (GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}",
                    ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(90), GUILayout.Height(34)))
                ExportTab4(list);
            EditorGUILayout.EndHorizontal();

            // ── 列表 ──
            long maxBytes = list.Max(e => e.Bytes);

            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                using (new EditorGUILayout.HorizontalScope(BundleAnalyzerStyles.TableHeader))
                {
                    GUILayout.Label("游戏模块", BundleAnalyzerStyles.TableHeaderLabel,
                        GUILayout.Width(126), GUILayout.Height(36));
                    GUILayout.Label("Bundle 名称", BundleAnalyzerStyles.TableHeaderLabel,
                        GUILayout.MinWidth(200), GUILayout.Height(36));
                    GUILayout.Label("文件大小", new GUIStyle(BundleAnalyzerStyles.TableHeaderLabel)
                        { alignment = TextAnchor.MiddleCenter },
                        GUILayout.Width(110), GUILayout.Height(36));
                    GUILayout.Label("占比", new GUIStyle(BundleAnalyzerStyles.TableHeaderLabel)
                        { alignment = TextAnchor.MiddleCenter },
                        GUILayout.Width(58), GUILayout.Height(36));
                    GUILayout.Space(BundleSizeRiskColumnRightOffset);
                    GUILayout.Label("风险状态", new GUIStyle(BundleAnalyzerStyles.TableHeaderLabel)
                        { alignment = TextAnchor.MiddleCenter },
                        GUILayout.Width(112), GUILayout.Height(36));
                    GUILayout.Space(BundleSizeMetricsLeftOffset +
                                    BundleSizeHeaderAlignmentOffset -
                                    BundleSizeRiskColumnRightOffset);
                    GUILayout.Label(string.Empty, GUILayout.Width(72));
                }
                GUILayout.Space(6);
                _t4Scroll = EditorGUILayout.BeginScrollView(_t4Scroll);
                foreach (var entry in list)
                {
                    bool risky = entry.HasCrossDep || entry.HasEmbeddedAsset;
                    GUIStyle sizeRow = new GUIStyle(BundleAnalyzerStyles.TableRow) { fixedHeight = 54 };
                    using (new EditorGUILayout.HorizontalScope(sizeRow, GUILayout.Height(54)))
                    {
                        GUILayout.Label(entry.GameName, BundleAnalyzerStyles.TableCell,
                            GUILayout.Width(126), GUILayout.Height(54));
                        GUILayout.Label(entry.BundleName, new GUIStyle(BundleAnalyzerStyles.TableCell)
                        {
                            fontStyle = FontStyle.Bold,
                            normal = { textColor = risky
                                ? new Color32(238, 112, 116, 255)
                                : new Color32(226, 231, 238, 255) }
                        }, GUILayout.MinWidth(200), GUILayout.Height(54));

                        float ratio = maxBytes > 0 ? (float)entry.Bytes / maxBytes : 0;
                        Rect sizeCell = GUILayoutUtility.GetRect(110, 54, GUILayout.Width(110), GUILayout.Height(54));
                        Rect track = new Rect(sizeCell.x, sizeCell.center.y - 5, sizeCell.width, 10);
                        EditorGUI.DrawRect(track, new Color32(43, 47, 54, 255));
                        EditorGUI.DrawRect(new Rect(track.x, track.y, track.width * ratio, track.height),
                            risky ? new Color32(232, 110, 115, 255) : ZMBuildStyles.Accent);
                        GUI.Label(sizeCell, entry.SizeStr, new GUIStyle(BundleAnalyzerStyles.TableCell)
                        {
                            fontSize = 11,
                            alignment = TextAnchor.MiddleCenter
                        });

                        float pct = totalBytes > 0 ? entry.Bytes * 100f / totalBytes : 0;
                        GUILayout.Label($"{pct:F1}%", new GUIStyle(BundleAnalyzerStyles.TableCell)
                            { alignment = TextAnchor.MiddleCenter },
                            GUILayout.Width(58), GUILayout.Height(54));
                        GUILayout.Space(BundleSizeRiskColumnRightOffset);
                        string marker = entry.HasCrossDep && entry.HasEmbeddedAsset ? "双重风险"
                            : entry.HasCrossDep ? "依赖风险"
                            : entry.HasEmbeddedAsset ? "嵌入资源" : "正常";
                        GUILayout.Label(marker, new GUIStyle(BundleAnalyzerStyles.TableCell)
                        {
                            alignment = TextAnchor.MiddleCenter,
                            normal = { textColor = risky
                                ? new Color32(238, 112, 116, 255)
                                : new Color32(82, 198, 143, 255) }
                        }, GUILayout.Width(112), GUILayout.Height(54));

                        GUILayout.Space(BundleSizeMetricsLeftOffset -
                                        BundleSizeRiskColumnRightOffset);
                        int gameIdx = Array.IndexOf(_gameNames, entry.GameName);
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(72), GUILayout.Height(54)))
                        {
                            GUILayout.Space(13);
                            GUIStyle viewButton = new GUIStyle(ZMBuildStyles.CompactSecondaryButton)
                            {
                                fixedHeight = 28,
                                alignment = TextAnchor.MiddleCenter,
                                padding = new RectOffset()
                            };
                            if (gameIdx >= 0 && GUILayout.Button("查看", viewButton,
                                    GUILayout.Width(72), GUILayout.Height(28)))
                                JumpToTab5(gameIdx, entry.BundleName);
                        }
                    }
                    GUILayout.Space(5);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void RunBundleSizeScan()
        {
            _t4Entries.Clear();

            // 只扫描已构建 AB 目录的游戏
            string[] gamesToScan = _t4ShowAll
                ? _gameNames.Where((n, i) => _gameHasAb[i]).ToArray()
                : (_gameHasAb[_t4GameIdx] ? new[] { _gameNames[_t4GameIdx] } : Array.Empty<string>());

            foreach (var gameName in gamesToScan)
            {
                string gameDir = Path.Combine(AbRootFullPath, gameName);
                if (!Directory.Exists(gameDir)) continue;

                // 加载 manifest 以获取有哪些 bundle 有跨游戏依赖
                var md      = LoadOrGetManifest(gameName);
                string pfx  = gameName.ToLower();
                string[] others = _gameNames
                    .Where(n => !n.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.ToLower()).ToArray();

                HashSet<string> crossBundles = new(StringComparer.OrdinalIgnoreCase);
                if (md != null)
                {
                    foreach (var kvp in md.AllDeps)
                        if (kvp.Value.Any(dep => IsCrossGame(dep, pfx, others)))
                            crossBundles.Add(kvp.Key);
                }

                // 扫描 .uab 文件大小
                foreach (var file in Directory.GetFiles(gameDir, "*.uab", SearchOption.TopDirectoryOnly))
                {
                    string bundleName = Path.GetFileName(file);
                    long   size       = new FileInfo(file).Length;
                    _t4Entries.Add(new BundleSizeEntry
                    {
                        GameName    = gameName,
                        BundleName  = bundleName,
                        Bytes       = size,
                        HasCrossDep = crossBundles.Contains(bundleName)
                    });
                }
            }

            // ── 深度扫描：标记嵌入了其他游戏资源的 Bundle ──
            var t4EntryMap = new Dictionary<string, int>(); // "GameName/BundleName" → index in _t4Entries
            for (int i = 0; i < _t4Entries.Count; i++)
                t4EntryMap[$"{_t4Entries[i].GameName}/{_t4Entries[i].BundleName}"] = i;

            var t4AllTasks = _t4Entries
                .Where(e => IsValidAssetBundle(Path.Combine(AbRootFullPath, e.GameName, e.BundleName)))
                .Select((e, _) => e)
                .ToList();

            if (t4AllTasks.Count > 0)
            {
                try
                {
                    for (int ti = 0; ti < t4AllTasks.Count; ti++)
                    {
                        var e = t4AllTasks[ti];
                        EditorUtility.DisplayProgressBar(
                            "Bundle 大小统计 — 深度扫描嵌入资源",
                            $"[{ti + 1}/{t4AllTasks.Count}] {e.GameName}/{e.BundleName}",
                            (float)(ti + 1) / t4AllTasks.Count);

                        var kw = BuildOtherGameKeywordsForGame(e.GameName);
                        bool hasEmbed = GetOrComputeEmbedded(e.GameName, e.BundleName, kw);
                        if (hasEmbed)
                        {
                            string key = $"{e.GameName}/{e.BundleName}";
                            if (t4EntryMap.TryGetValue(key, out int idx))
                            {
                                var entry = _t4Entries[idx];
                                entry.HasEmbeddedAsset = true;
                                _t4Entries[idx] = entry;
                            }
                        }
                    }
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                    if (_embedCache.IsDirty) SaveEmbedCache();
                }
            }

            _t4NeedScan = false;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1073741824) return $"{bytes / 1073741824f:F2} GB";
            if (bytes >= 1048576)    return $"{bytes / 1048576f:F2} MB";
            return $"{bytes / 1024f:F1} KB";   // always KB for anything < 1 MB
        }

        private void ExportTab4(List<BundleSizeEntry> list)
        {
            string ext     = IsCsv ? "csv" : "txt";
            string defName = $"ABSize_Report_{DateTime.Now:yyyyMMdd_HHmm}.{ext}";
            string path    = EditorUtility.SaveFilePanel("导出 Bundle 大小统计", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            long totalBytes = list.Sum(e => e.Bytes);

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("GameName,BundleName,Bytes,Size,Percent,HasCrossDep,HasEmbeddedAsset");
                foreach (var e in list)
                {
                    float pct = totalBytes > 0 ? e.Bytes * 100f / totalBytes : 0;
                    sw.WriteLine($"{CsvEscape(e.GameName)},{CsvEscape(e.BundleName)},{e.Bytes},{CsvEscape(e.SizeStr)},{pct:F1}%,{(e.HasCrossDep ? "是" : "否")},{(e.HasEmbeddedAsset ? "是" : "否")}");
                }
            }
            else
            {
                sw.WriteLine($"=== Bundle 大小统计报告 ===");
                sw.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}    总大小: {FormatBytes(totalBytes)}    Bundle数: {list.Count}");
                sw.WriteLine(new string('-', 100));
                sw.WriteLine($"{"游戏",-20}{"Bundle名",-55}{"大小",10}{"占比",8}  {"问题标记"}");
                sw.WriteLine(new string('-', 100));
                foreach (var e in list)
                {
                    float pct = totalBytes > 0 ? e.Bytes * 100f / totalBytes : 0;
                    string marker = "";
                    if (e.HasCrossDep)      marker += "⚠Manifest ";
                    if (e.HasEmbeddedAsset) marker += "🔬嵌入";
                    sw.WriteLine($"{e.GameName,-20}{e.BundleName,-55}{e.SizeStr,10}{pct,7:F1}%  {marker}");
                }
            }

            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出: {path}");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  导出功能
        // ═════════════════════════════════════════════════════════════════════

        private bool IsCsv => _exportFmtIdx == 1;


    }
}
#endif
