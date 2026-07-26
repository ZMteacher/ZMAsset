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
        private enum CompareStatus { Unchanged, Increased, Decreased, Added, Removed }

        private struct BundleCompareEntry
        {
            public string        BundleName;
            public long          OldBytes;   // 0 when Added
            public long          NewBytes;   // 0 when Removed
            public CompareStatus Status;
            public long          Delta => NewBytes - OldBytes;
        }

        private static readonly string[] CompareSortOptions  = { "按变化量", "按名称", "按状态" };
        private static readonly string[] CompareFilterOptions = { "全部", "仅变化（新增/删除/改动）", "仅增大", "仅新增", "仅删除" };

        private void DrawTab1_VersionCompare()
        {
            EditorGUILayout.BeginVertical(ZMBuildStyles.SettingsCard);
            EditorGUILayout.LabelField("AB 版本对比", BundleAnalyzerStyles.PageTitle, GUILayout.Height(32));
            EditorGUILayout.LabelField("选择两个 AssetBundle 文件夹，对比 Bundle 文件大小变化（支持任意路径，不限于本工程）。",
                BundleAnalyzerStyles.InfoBox, GUILayout.Height(38));
            EditorGUILayout.Space(4);

            // ── 路径选择 ──
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("旧版本目录", GUILayout.Width(70));
            _t1OldPath = EditorGUILayout.TextField(_t1OldPath);
            if (GUILayout.Button("浏览", GUILayout.Width(46)))
            {
                string p = EditorUtility.OpenFolderPanel("选择旧版本 AB 目录", _t1OldPath, "");
                if (!string.IsNullOrEmpty(p)) _t1OldPath = p;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("新版本目录", GUILayout.Width(70));
            _t1NewPath = EditorGUILayout.TextField(_t1NewPath);
            if (GUILayout.Button("浏览", GUILayout.Width(46)))
            {
                string p = EditorUtility.OpenFolderPanel("选择新版本 AB 目录", _t1NewPath, "");
                if (!string.IsNullOrEmpty(p)) _t1NewPath = p;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);
            // ── 操作行1：对比按钮 ──
            EditorGUILayout.BeginHorizontal();
            bool hasCompareResult = _t1CompareResults.Count > 0;
            if (GUILayout.Button(hasCompareResult ? "重新对比" : "开始对比",
                    hasCompareResult ? ZMBuildStyles.CompactSecondaryButton : ZMBuildStyles.CompactPrimaryButton,
                    GUILayout.Height(34), GUILayout.Width(104)))
                RunVersionCompare();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!hasCompareResult))
            if (GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}", ZMBuildStyles.CompactSecondaryButton,
                    GUILayout.Height(34), GUILayout.Width(100)))
                ExportVersionCompare(_t1CompareResults
                    .OrderByDescending(e => Math.Abs(e.Delta)).ToList());
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(10);
            // ── 操作行2：排序/筛选 ──
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("排序", GUILayout.Width(30));
            _t1SortMode = DrawAnalyzerPopup("compare.sort", _t1SortMode, CompareSortOptions, GUILayout.MinWidth(100));
            GUILayout.Space(12);
            EditorGUILayout.LabelField("筛选", GUILayout.Width(30));
            _t1FilterMode = DrawAnalyzerPopup("compare.filter", _t1FilterMode, CompareFilterOptions, GUILayout.MinWidth(160));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            if (_t1CompareResults.Count == 0) return;

            // ── 筛选 + 排序 ──
            IEnumerable<BundleCompareEntry> view = _t1CompareResults;
            switch (_t1FilterMode)
            {
                case 1: view = view.Where(e => e.Status != CompareStatus.Unchanged);  break;
                case 2: view = view.Where(e => e.Status == CompareStatus.Increased);  break;
                case 3: view = view.Where(e => e.Status == CompareStatus.Added);      break;
                case 4: view = view.Where(e => e.Status == CompareStatus.Removed);    break;
            }
            switch (_t1SortMode)
            {
                case 0: view = view.OrderByDescending(e => Math.Abs(e.Delta)); break;
                case 1: view = view.OrderBy(e => e.BundleName);                break;
                case 2: view = view.OrderBy(e => (int)e.Status);               break;
            }
            var list = view.ToList();

            // ── 汇总 header ──
            int added   = _t1CompareResults.Count(e => e.Status == CompareStatus.Added);
            int removed = _t1CompareResults.Count(e => e.Status == CompareStatus.Removed);
            int changed = _t1CompareResults.Count(e => e.Status == CompareStatus.Increased || e.Status == CompareStatus.Decreased);
            long totalDelta = _t1CompareResults.Sum(e => e.Delta);
            string deltaStr = (totalDelta >= 0 ? "+" : "-") + FormatBytes(Math.Abs(totalDelta));

            EditorGUILayout.BeginVertical(ZMBuildStyles.SettingsCard);
            EditorGUILayout.LabelField(
                $"共 {_t1CompareResults.Count} 个 Bundle  ✚新增 {added}  ✖删除 {removed}  △改动 {changed}  总体大小变化: {deltaStr}",
                totalDelta > 0 ? _styleRed : _styleGreen);
            EditorGUILayout.EndVertical();

            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                using (new EditorGUILayout.HorizontalScope(BundleAnalyzerStyles.TableHeader))
                {
                    GUILayout.Label("Bundle 名称", BundleAnalyzerStyles.TableHeaderLabel, GUILayout.MinWidth(240), GUILayout.Height(36));
                    GUILayout.Label("旧版本", BundleAnalyzerStyles.TableHeaderLabel, GUILayout.Width(88), GUILayout.Height(36));
                    GUILayout.Label("新版本", BundleAnalyzerStyles.TableHeaderLabel, GUILayout.Width(88), GUILayout.Height(36));
                    GUILayout.Label("变化量", BundleAnalyzerStyles.TableHeaderLabel, GUILayout.Width(88), GUILayout.Height(36));
                    GUILayout.Label("变化率", BundleAnalyzerStyles.TableHeaderLabel, GUILayout.Width(68), GUILayout.Height(36));
                    GUILayout.Label("状态", BundleAnalyzerStyles.TableHeaderLabel, GUILayout.Width(72), GUILayout.Height(36));
                }
                GUILayout.Space(6);

                _t1Scroll = EditorGUILayout.BeginScrollView(_t1Scroll, GUILayout.MaxHeight(400));
                foreach (var e in list)
                {
                    Color stateColor = e.Status switch
                    {
                        CompareStatus.Added => new Color32(71, 201, 141, 255),
                        CompareStatus.Removed => new Color32(238, 103, 111, 255),
                        CompareStatus.Increased => new Color32(239, 171, 72, 255),
                        CompareStatus.Decreased => new Color32(71, 201, 181, 255),
                        _ => new Color32(132, 142, 154, 255)
                    };
                    string statusText = e.Status switch
                    {
                        CompareStatus.Added => "新增",
                        CompareStatus.Removed => "删除",
                        CompareStatus.Increased => "增大",
                        CompareStatus.Decreased => "减小",
                        _ => "未变化"
                    };
                string deltaLabel = e.Status switch
                {
                    CompareStatus.Added   => $"+{FormatBytes(e.NewBytes)}",
                    CompareStatus.Removed => $"-{FormatBytes(e.OldBytes)}",
                    _                     => (e.Delta >= 0 ? "+" : "-") + FormatBytes(Math.Abs(e.Delta))
                };
                float pctChange = e.OldBytes > 0 ? e.Delta * 100f / e.OldBytes : float.NaN;
                string pctStr = float.IsNaN(pctChange) ? "—" : $"{(pctChange >= 0 ? "+" : "")}{pctChange:F1}%";

                    using (new EditorGUILayout.HorizontalScope(BundleAnalyzerStyles.TableRow))
                    {
                        GUILayout.Label(e.BundleName, new GUIStyle(BundleAnalyzerStyles.TableCell)
                        {
                            fontStyle = FontStyle.Bold
                        }, GUILayout.MinWidth(240), GUILayout.Height(46));
                        GUILayout.Label(e.OldBytes > 0 ? FormatBytes(e.OldBytes) : "—",
                            BundleAnalyzerStyles.TableCellRight, GUILayout.Width(88), GUILayout.Height(46));
                        GUILayout.Label(e.NewBytes > 0 ? FormatBytes(e.NewBytes) : "—",
                            BundleAnalyzerStyles.TableCellRight, GUILayout.Width(88), GUILayout.Height(46));
                        GUILayout.Label(deltaLabel, new GUIStyle(BundleAnalyzerStyles.TableCellRight)
                        {
                            normal = { textColor = stateColor }
                        }, GUILayout.Width(88), GUILayout.Height(46));
                        GUILayout.Label(pctStr, new GUIStyle(BundleAnalyzerStyles.TableCellRight)
                        {
                            normal = { textColor = stateColor }
                        }, GUILayout.Width(68), GUILayout.Height(46));
                        Rect badgeCell = GUILayoutUtility.GetRect(72, 46, GUILayout.Width(72), GUILayout.Height(46));
                        Rect badge = new Rect(badgeCell.x, badgeCell.center.y - 12, badgeCell.width, 24);
                        GUI.Box(badge, GUIContent.none, ZMBuildStyles.BadgeBox);
                        GUI.Label(badge, statusText, new GUIStyle(BundleAnalyzerStyles.TableHeaderLabel)
                        {
                            alignment = TextAnchor.MiddleCenter,
                            normal = { textColor = stateColor }
                        });
                    }
                    GUILayout.Space(5);
                }
                EditorGUILayout.EndScrollView();
            }

            // ── 大小对比图 ──
            EditorGUILayout.Space(6);
            _t1ShowChart = EditorGUILayout.Foldout(_t1ShowChart, "📊 大小对比图（按顶层目录分组）", true);
            if (_t1ShowChart)
                DrawVersionCompareChart();
        }

        private void RunVersionCompare()
        {
            _t1CompareResults.Clear();
            if (!Directory.Exists(_t1OldPath)) { EditorUtility.DisplayDialog("错误", $"旧版本目录不存在:\n{_t1OldPath}", "OK"); return; }
            if (!Directory.Exists(_t1NewPath)) { EditorUtility.DisplayDialog("错误", $"新版本目录不存在:\n{_t1NewPath}", "OK"); return; }

            // Collect all files recursively, key = relative path from root
            static Dictionary<string, long> ScanDir(string root)
            {
                var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                    result[rel] = new FileInfo(file).Length;
                }
                return result;
            }

            var oldFiles = ScanDir(_t1OldPath);
            var newFiles = ScanDir(_t1NewPath);
            var allKeys  = oldFiles.Keys.Union(newFiles.Keys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var key in allKeys)
            {
                bool inOld = oldFiles.TryGetValue(key, out long oldSize);
                bool inNew = newFiles.TryGetValue(key, out long newSize);

                CompareStatus status;
                if (!inOld)       status = CompareStatus.Added;
                else if (!inNew)  status = CompareStatus.Removed;
                else if (newSize > oldSize) status = CompareStatus.Increased;
                else if (newSize < oldSize) status = CompareStatus.Decreased;
                else              status = CompareStatus.Unchanged;

                _t1CompareResults.Add(new BundleCompareEntry
                {
                    BundleName = key,
                    OldBytes   = inOld ? oldSize : 0,
                    NewBytes   = inNew ? newSize : 0,
                    Status     = status
                });
            }
        }

        private void ExportVersionCompare(List<BundleCompareEntry> list)
        {
            string ext     = IsCsv ? "csv" : "txt";
            string defName = $"ABVersionCompare_{DateTime.Now:yyyyMMdd_HHmm}.{ext}";
            string path    = EditorUtility.SaveFilePanel("导出版本对比", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("BundleName,OldBytes,OldSize,NewBytes,NewSize,DeltaBytes,DeltaSize,DeltaPct,Status");
                foreach (var e in list)
                {
                    float pct = e.OldBytes > 0 ? e.Delta * 100f / e.OldBytes : float.NaN;
                    string pctStr = float.IsNaN(pct) ? "" : $"{pct:F1}%";
                    sw.WriteLine($"{CsvEscape(e.BundleName)},{e.OldBytes},{CsvEscape(e.OldBytes > 0 ? FormatBytes(e.OldBytes) : "")},{e.NewBytes},{CsvEscape(e.NewBytes > 0 ? FormatBytes(e.NewBytes) : "")},{e.Delta},{CsvEscape((e.Delta >= 0 ? "+" : "") + FormatBytes(Math.Abs(e.Delta)))},{pctStr},{e.Status}");
                }
            }
            else
            {
                sw.WriteLine($"=== AB 版本对比报告 ===");
                sw.WriteLine($"旧版本: {_t1OldPath}");
                sw.WriteLine($"新版本: {_t1NewPath}");
                sw.WriteLine($"时间:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine(new string('-', 120));
                sw.WriteLine($"{"Bundle 名",-60}{"旧大小",10}{"新大小",10}{"变化量",12}{"变化%",8}  {"状态"}");
                sw.WriteLine(new string('-', 120));
                foreach (var e in list)
                {
                    float pct = e.OldBytes > 0 ? e.Delta * 100f / e.OldBytes : float.NaN;
                    string pctStr = float.IsNaN(pct) ? "—" : $"{(pct >= 0 ? "+" : "")}{pct:F1}%";
                    string deltaStr = (e.Delta >= 0 ? "+" : "") + FormatBytes(Math.Abs(e.Delta));
                    sw.WriteLine($"{e.BundleName,-60}{(e.OldBytes > 0 ? FormatBytes(e.OldBytes) : "—"),10}{(e.NewBytes > 0 ? FormatBytes(e.NewBytes) : "—"),10}{deltaStr,12}{pctStr,8}  {e.Status}");
                }
            }

            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出版本对比: {path}");
        }

        private void DrawVersionCompareChart()
        {
            if (_t1CompareResults.Count == 0) return;

            // Group by top-level directory segment
            var groups = _t1CompareResults
                .GroupBy(e =>
                {
                    string rel = e.BundleName.Replace('\\', '/');
                    int sl = rel.IndexOf('/');
                    return sl >= 0 ? rel.Substring(0, sl) : "(根目录)";
                })
                .Select(g => (
                    Name:     g.Key,
                    OldTotal: g.Sum(e => e.OldBytes),
                    NewTotal: g.Sum(e => e.NewBytes),
                    Delta:    g.Sum(e => e.Delta)
                ))
                .OrderByDescending(g => Math.Max(g.OldTotal, g.NewTotal))
                .ToList();

            if (groups.Count == 0) return;

            long maxVal = groups.Max(g => Math.Max(g.OldTotal, g.NewTotal));
            if (maxVal == 0) maxVal = 1;

            const float chartH   = 180f;
            const float leftPad  = 68f;
            const float botPad   = 36f;
            const float topPad   = 26f;
            const float rightPad = 10f;

            Rect area = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Height(chartH + botPad + topPad), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(area, new Color(0.13f, 0.13f, 0.13f));

            Rect chart = new Rect(area.x + leftPad, area.y + topPad,
                                  area.width - leftPad - rightPad, chartH);

            // Y-axis grid + labels
            for (int gi = 0; gi <= 4; gi++)
            {
                float gy = chart.yMax - chart.height * gi / 4f;
                EditorGUI.DrawRect(new Rect(chart.x, gy, chart.width, 1), new Color(0.27f, 0.27f, 0.27f));
                GUI.Label(new Rect(area.x, gy - 8, leftPad - 4, 16), FormatBytes(maxVal * gi / 4), _miniRightStyle);
            }
            // Axes
            EditorGUI.DrawRect(new Rect(chart.x, chart.yMax, chart.width, 1), new Color(0.45f, 0.45f, 0.45f));
            EditorGUI.DrawRect(new Rect(chart.x, chart.y, 1, chart.height),   new Color(0.45f, 0.45f, 0.45f));

            int   n    = groups.Count;
            float colW = chart.width / n;
            float barW = Mathf.Max(colW * 0.33f, 4f);
            float gap  = Mathf.Max(colW * 0.04f, 1f);

            for (int i = 0; i < n; i++)
            {
                var g  = groups[i];
                float cx = chart.x + colW * i + colW * 0.5f;

                // Old bar (gray)
                float oldH = chart.height * g.OldTotal / maxVal;
                float oldBarX = cx - barW - gap * 0.5f;
                float oldBarY = chart.yMax - oldH;
                EditorGUI.DrawRect(new Rect(oldBarX, oldBarY, barW, oldH), new Color(0.50f, 0.50f, 0.50f));
                // Size label above old bar
                GUI.Label(new Rect(oldBarX - 4, oldBarY - 14, barW + 8, 13),
                    FormatBytes(g.OldTotal), _miniCenterStyle);

                // New bar (blue if ↓ or unchanged, red if ↑)
                float newH     = chart.height * g.NewTotal / maxVal;
                float newBarX  = cx + gap * 0.5f;
                float newBarY  = chart.yMax - newH;
                Color newColor = g.Delta > 0 ? new Color(0.95f, 0.35f, 0.25f) : new Color(0.28f, 0.72f, 1.0f);
                EditorGUI.DrawRect(new Rect(newBarX, newBarY, barW, newH), newColor);
                // Size label above new bar
                GUI.Label(new Rect(newBarX - 4, newBarY - 14, barW + 8, 13),
                    FormatBytes(g.NewTotal), _miniCenterStyle);

                // X label
                string lbl = g.Name.Length > 14 ? g.Name.Substring(0, 14) : g.Name;
                GUI.Label(new Rect(cx - colW * 0.5f, chart.yMax + 3, colW, 30), lbl, _miniCenterStyle);
            }

            // Legend (top-left of chart)
            float lx = chart.x + 4, ly = area.y + 3;
            EditorGUI.DrawRect(new Rect(lx,      ly + 1, 12, 9), new Color(0.50f, 0.50f, 0.50f));
            GUI.Label(new Rect(lx + 14, ly,      50, 14), "旧版本", EditorStyles.miniLabel);
            EditorGUI.DrawRect(new Rect(lx + 64, ly + 1, 12, 9), new Color(0.28f, 0.72f, 1.0f));
            GUI.Label(new Rect(lx + 78, ly,      50, 14), "新版本↓", EditorStyles.miniLabel);
            EditorGUI.DrawRect(new Rect(lx + 138, ly + 1, 12, 9), new Color(0.95f, 0.35f, 0.25f));
            GUI.Label(new Rect(lx + 152, ly,      50, 14), "新版本↑", EditorStyles.miniLabel);
        }
        // ═════════════════════════════════════════════════════════════════════


    }
}
#endif
