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
        private GUIStyle _crossGameActionButton;

        private GUIStyle CrossGameActionButton
        {
            get
            {
                if (_crossGameActionButton != null)
                    return _crossGameActionButton;

                _crossGameActionButton = new GUIStyle(ZMBuildStyles.CardEditButton)
                {
                    fixedHeight = 0,
                    stretchHeight = false,
                    alignment = TextAnchor.MiddleCenter,
                    padding = new RectOffset(0, 0, 0, 0)
                };
                return _crossGameActionButton;
            }
        }

        private struct CrossResult
        {
            public string SourceBundle;
            public string[] CrossDeps;
        }

        private void DrawTab2_CrossGame()
        {
            // ── 参数面板 ──
            EditorGUILayout.BeginVertical(ZMBuildStyles.SettingsCard);
            EditorGUILayout.LabelField("跨游戏依赖检测", BundleAnalyzerStyles.PageTitle, GUILayout.Height(32));
            EditorGUILayout.LabelField("检测「源游戏」的所有 Bundle 中，哪些引用了「目标游戏」的 Bundle（导致目标游戏资源被打包进源游戏，造成冗余）。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(38));
            EditorGUILayout.Space(4);

            if (_gameNames.Length == 0)
            {
                EditorGUILayout.HelpBox("未找到游戏配置。", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            // 显示全部游戏
            string[] targetOptions = new[] { "全部其他游戏（已构建）" }.Concat(_gameDisplayNames).ToArray();

            EditorGUILayout.LabelField("源游戏（被检测）", ZMBuildStyles.SettingsLabel);
            int newSrc = DrawAnalyzerPopup("cross.source", _t2SrcIdx, _gameDisplayNames, GUILayout.ExpandWidth(true));
            EditorGUILayout.LabelField("目标游戏（过滤）", ZMBuildStyles.SettingsLabel);
            int newDst = DrawAnalyzerPopup("cross.target", _t2DstIdx, targetOptions, GUILayout.ExpandWidth(true));
            if (newSrc != _t2SrcIdx || newDst != _t2DstIdx)
            {
                _t2SrcIdx = newSrc;
                _t2DstIdx = newDst;
                _t2Dirty  = true;
            }

            // 若源游戏未构建，提示并中止
            if (!_gameHasAb[_t2SrcIdx])
            {
                EditorGUILayout.HelpBox($"[{_gameNames[_t2SrcIdx]}] 尚未构建 AB 包，无法检测。请先打包该游戏。", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.Space(4);
            Rect deepScanToggle = GUILayoutUtility.GetRect(
                310, 28, GUILayout.ExpandWidth(true), GUILayout.Height(28));
            _t2DeepScan = DrawAnalyzerToggle(
                deepScanToggle,
                new GUIContent("深度扫描（加载 Bundle 内资源路径）",
                    "除 Manifest 依赖外，还会加载每个 .uab 文件，检查其包含的资源路径中是否含有其他游戏名。\n可检测到「资源被直接打入」的冗余情况，耗时较长。"),
                _t2DeepScan);
            EditorGUILayout.Space(8);
            bool hasDetectionResult = !_t2Dirty;
            if (GUILayout.Button(hasDetectionResult ? "重新检测" : "开始检测",
                    hasDetectionResult ? ZMBuildStyles.CompactSecondaryButton : ZMBuildStyles.CompactPrimaryButton,
                    GUILayout.Height(34))) RunCrossGameDetection();
            EditorGUILayout.EndVertical();

            if (_t2Dirty) return;

            EditorGUILayout.Space(4);

            // Early-exit before opening any scroll view
            if (_t2Results.Count == 0 && _t2EmbeddedResults.Count == 0)
            {
                EditorGUILayout.HelpBox("✅  未发现跨游戏资源依赖，当前游戏资源边界清晰！", MessageType.Info);
                return;
            }

            // Single scroll view for all results — no nesting
            _t2OuterScroll = EditorGUILayout.BeginScrollView(_t2OuterScroll);

            // ── Manifest 层结果 ──
            if (_t2Results.Count == 0)
            {
                EditorGUILayout.HelpBox("✅  Manifest 层未发现跨游戏 Bundle 依赖。", MessageType.Info);
            }
            else
            {
                int total = _t2Results.Sum(r => r.CrossDeps.Length);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox($"⚠  发现 {_t2Results.Count} 个 Bundle 存在跨游戏依赖，共 {total} 条冗余引用。", MessageType.Warning);
                if (GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}", GUILayout.Width(72), GUILayout.Height(38)))
                    ExportTab2(_gameNames[_t2SrcIdx]);
                EditorGUILayout.EndHorizontal();

                foreach (var r in _t2Results)
                {
                    EditorGUILayout.BeginVertical(ZMBuildStyles.SettingsCard);
                    EditorGUILayout.BeginHorizontal();
                    Rect icon = GUILayoutUtility.GetRect(28, 28, GUILayout.Width(28), GUILayout.Height(28));
                    BundleAnalyzerIcons.Draw(new Rect(icon.x + 4, icon.y + 4, 20, 20),
                        BundleAnalyzerIcons.Icon.Dependency, new Color32(238, 112, 116, 255), 1.7f);
                    GUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(r.SourceBundle, new GUIStyle(BundleAnalyzerStyles.TableCell)
                        {
                            fontStyle = FontStyle.Bold,
                            normal = { textColor = new Color32(238, 112, 116, 255) }
                        });
                        EditorGUILayout.LabelField($"{r.CrossDeps.Length} 条跨模块 Bundle 依赖",
                            BundleAnalyzerStyles.PageSubtitle);
                    }
                    GUILayout.FlexibleSpace();
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(92), GUILayout.Height(44)))
                    {
                        GUILayout.Space(8);
                        if (GUILayout.Button("查看 Bundle", CrossGameActionButton,
                                GUILayout.Width(92), GUILayout.Height(28)))
                            JumpToTab5(_t2SrcIdx, r.SourceBundle);
                    }
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(8);
                    foreach (var dep in r.CrossDeps)
                    {
                        using (new EditorGUILayout.HorizontalScope(BundleAnalyzerStyles.ListRow, GUILayout.Height(36)))
                        {
                            GUILayout.Label("依赖", new GUIStyle(BundleAnalyzerStyles.TableHeaderLabel)
                            {
                                normal = { textColor = new Color32(238, 112, 116, 255) }
                            }, GUILayout.Width(42), GUILayout.Height(36));
                            GUILayout.Label(dep, BundleAnalyzerStyles.TableCell, GUILayout.Height(36));
                        }
                        GUILayout.Space(4);
                    }
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(7);
                }
            }

            // ── 深度扫描结果 ──
            if (_t2EmbeddedResults.Count > 0)
            {
                int embTotal = _t2EmbeddedResults.Sum(r => r.CrossAssets.Count);
                EditorGUILayout.Space(6);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(
                    $"🔬 深度扫描：发现 {_t2EmbeddedResults.Count} 个 Bundle 内直接嵌入了其他游戏的资源，共 {embTotal} 个资源路径。",
                    MessageType.Warning);
                if (GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}", GUILayout.Width(72), GUILayout.Height(38)))
                    ExportEmbeddedResults(_gameNames[_t2SrcIdx]);
                EditorGUILayout.EndHorizontal();

                foreach (var r in _t2EmbeddedResults)
                {
                    EditorGUILayout.BeginVertical(ZMBuildStyles.SettingsCard);
                    EditorGUILayout.BeginHorizontal();
                    Rect icon = GUILayoutUtility.GetRect(28, 28, GUILayout.Width(28), GUILayout.Height(28));
                    BundleAnalyzerIcons.Draw(new Rect(icon.x + 4, icon.y + 4, 20, 20),
                        BundleAnalyzerIcons.Icon.Browser, new Color32(239, 171, 72, 255), 1.7f);
                    GUILayout.Space(8);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(r.SourceBundle, new GUIStyle(BundleAnalyzerStyles.TableCell)
                        {
                            fontStyle = FontStyle.Bold,
                            normal = { textColor = new Color32(239, 171, 72, 255) }
                        });
                        EditorGUILayout.LabelField($"嵌入 {r.CrossAssets.Count} 条跨模块资源",
                            BundleAnalyzerStyles.PageSubtitle);
                    }
                    GUILayout.FlexibleSpace();
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(92), GUILayout.Height(44)))
                    {
                        GUILayout.Space(8);
                        if (GUILayout.Button("查看 Bundle", CrossGameActionButton,
                                GUILayout.Width(92), GUILayout.Height(28)))
                            JumpToTab5(_t2SrcIdx, r.SourceBundle);
                    }
                    EditorGUILayout.EndHorizontal();
                    GUILayout.Space(8);
                    foreach (var (assetPath, matchedGame) in r.CrossAssets)
                    {
                        EditorGUILayout.BeginHorizontal(BundleAnalyzerStyles.ListRow, GUILayout.Height(38));
                        EditorGUILayout.LabelField(matchedGame, new GUIStyle(BundleAnalyzerStyles.TableHeaderLabel)
                        {
                            normal = { textColor = new Color32(239, 171, 72, 255) }
                        }, GUILayout.Width(90), GUILayout.Height(38));
                        EditorGUILayout.LabelField(assetPath, BundleAnalyzerStyles.TableCell, GUILayout.Height(38));
                        using (new EditorGUILayout.VerticalScope(GUILayout.Width(58), GUILayout.Height(38)))
                        {
                            GUILayout.Space(6);
                            if (GUILayout.Button("定位", CrossGameActionButton,
                                    GUILayout.Width(58), GUILayout.Height(26)))
                                PingAsset(assetPath);
                        }
                        EditorGUILayout.EndHorizontal();
                        GUILayout.Space(4);
                    }
                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(7);
                }
            }
            else if (_t2DeepScan)
            {
                EditorGUILayout.HelpBox("🔬 深度扫描：未发现跨游戏资源嵌入。", MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>Switch to Tab5 and pre-select the given bundle in the given game.</summary>
        private void JumpToTab5(int gameIdx, string bundleName)
        {
            _t5GameIdx       = gameIdx;
            _t5ViewMode      = false;          // single-bundle view
            _t5PendingBundle = bundleName;
            _t5NeedReload    = true;
            _tab             = 4;
            Repaint();
        }

        /// <summary>Switch to Tab2 and run cross-game detection for the given game.</summary>
        private void JumpToTab2(string gameName)
        {
            int idx = Array.IndexOf(_gameNames, gameName);
            if (idx < 0) return;
            _t2SrcIdx  = idx;
            _t2DstIdx  = 0;   // 全部其他游戏
            _t2DeepScan = _t3DeepScan; // inherit deep scan setting from Tab3
            _tab       = 1;
            RunCrossGameDetection();   // auto-run immediately
            Repaint();
        }

        /// <summary>
        /// Return file size string for an asset by its bundle-style path (e.g. "assets/gamedata/...").
        /// Returns empty string if not found in project.
        /// </summary>
        private static string GetAssetSizeStr(string bundleAssetPath)
        {
            if (string.IsNullOrEmpty(bundleAssetPath)) return string.Empty;
            string normalised = bundleAssetPath.Replace('\\', '/');
            if (normalised.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                normalised = "Assets" + normalised.Substring(6);
            string diskPath = Path.GetFullPath(normalised);
            if (File.Exists(diskPath)) return FormatBytes(new FileInfo(diskPath).Length);
            // Try via AssetDatabase
            string fullPath = Application.dataPath + "/.." + "/" + normalised;
            fullPath = fullPath.Replace('/', Path.DirectorySeparatorChar);
            if (File.Exists(fullPath)) return FormatBytes(new FileInfo(fullPath).Length);
            return string.Empty;
        }

        private static void PingAsset(string bundleAssetPath)
        {
            if (string.IsNullOrEmpty(bundleAssetPath)) return;

            // Normalise: AssetBundle paths start with "assets/" (lowercase), Unity needs "Assets/"
            string normalised = bundleAssetPath.Replace('\\', '/');
            if (normalised.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                normalised = "Assets" + normalised.Substring(6);

            var obj = AssetDatabase.LoadMainAssetAtPath(normalised);
            if (obj != null)
            {
                EditorGUIUtility.PingObject(obj);
                Selection.activeObject = obj;
                return;
            }

            // Fallback: GUID search by file name (handles case differences)
            string fileName = Path.GetFileNameWithoutExtension(normalised);
            string[] guids = AssetDatabase.FindAssets(fileName);
            foreach (string guid in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(p, normalised, StringComparison.OrdinalIgnoreCase))
                {
                    var o = AssetDatabase.LoadMainAssetAtPath(p);
                    if (o != null)
                    {
                        EditorGUIUtility.PingObject(o);
                        Selection.activeObject = o;
                        return;
                    }
                }
            }

            Debug.LogWarning($"[BundleDependencyViewer] Project 中找不到该资源: {bundleAssetPath}");
        }

        private void RunCrossGameDetection()
        {
            _t2Results.Clear();
            string srcName   = _gameNames[_t2SrcIdx];
            var    md        = LoadOrGetManifest(srcName);
            if (md == null)
            {
                EditorUtility.DisplayDialog("错误", $"无法加载 [{srcName}] 的 Manifest，请确认文件存在。", "确定");
                _t2Dirty = false;
                return;
            }

            // _t2DstIdx: 0=全部已构建游戏, 1+= 对应 _gameNames[N-1]
            string[] targetPfx = _t2DstIdx == 0
                ? _gameNames.Where((n, i) => _gameHasAb[i] && !n.Equals(srcName, StringComparison.OrdinalIgnoreCase))
                             .Select(n => n.ToLower()).ToArray()
                : new[] { _gameNames[_t2DstIdx - 1].ToLower() }; // _t2DstIdx=1 → _gameNames[0]

            string srcPfx = srcName.ToLower();

            foreach (var kvp in md.AllDeps)
            {
                var cross = kvp.Value
                    .Where(dep => IsCrossGame(dep, srcPfx, targetPfx))
                    .OrderBy(d => d)
                    .ToArray();
                if (cross.Length > 0)
                    _t2Results.Add(new CrossResult { SourceBundle = kvp.Key, CrossDeps = cross });
            }

            _t2Results.Sort((a, b) => string.Compare(a.SourceBundle, b.SourceBundle, StringComparison.Ordinal));

            // ── 深度扫描：加载每个 bundle 文件，检查资源路径中是否含有其他游戏名 ──
            _t2EmbeddedResults.Clear();
            if (_t2DeepScan)
            {
                // 构建「其他游戏的路径关键词」列表
                // 同时使用 moduleName（如 umogame）和去掉 Game 后缀（如 umo）
                var otherGameKeywords = BuildOtherGameKeywords(_t2SrcIdx, _t2DstIdx);
                string gameDir = Path.Combine(AbRootFullPath, srcName);

                var allBundleNames = md.AllDeps.Keys.ToList();
                int bundleTotal    = allBundleNames.Count;
                try
                {
                    for (int bi = 0; bi < bundleTotal; bi++)
                    {
                        string bundleName = allBundleNames[bi];
                        EditorUtility.DisplayProgressBar(
                            "深度扫描 —— 跨游戏资源嵌入检测",
                            $"[{bi + 1}/{bundleTotal}] {bundleName}",
                            (float)(bi + 1) / bundleTotal);

                        string bundlePath = Path.Combine(gameDir, bundleName);
                        if (!File.Exists(bundlePath) || !IsValidAssetBundle(bundlePath)) continue;

                        AssetBundle ab = null;
                        try
                        {
                            ab = AssetBundle.LoadFromFile(bundlePath);
                            if (ab == null) continue;

                            var crossAssets = new List<(string, string)>();
                            foreach (var assetPath in ab.GetAllAssetNames())
                            {
                                string pathLow = assetPath.ToLower();
                                foreach (var (keyword, gameName) in otherGameKeywords)
                                {
                                    if (pathLow.Contains(keyword))
                                    {
                                        crossAssets.Add((assetPath, gameName));
                                        break;
                                    }
                                }
                            }
                            if (crossAssets.Count > 0)
                                _t2EmbeddedResults.Add(new EmbeddedCrossResult
                                {
                                    SourceBundle = bundleName,
                                    CrossAssets  = crossAssets
                                });
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[BundleDependencyViewer] 深度扫描跳过 {bundleName}: {e.Message}");
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
                }
                _t2EmbeddedResults.Sort((a, b) =>
                    string.Compare(a.SourceBundle, b.SourceBundle, StringComparison.Ordinal));
            }

            _t2Dirty = false;
        }

        /// <summary>
        /// 构建「其他游戏路径关键词」列表。
        /// 每个元素是 (路径关键词, 游戏显示名)。
        /// 关键词使用 /gameFolderName/（去掉 Game 后缀并加路径分隔符），以避免误匹配。
        /// </summary>
        private List<(string keyword, string gameName)> BuildOtherGameKeywords(int srcIdx, int dstIdx)
        {
            IEnumerable<int> targetIndices;
            if (dstIdx == 0)
                targetIndices = Enumerable.Range(0, _gameNames.Length).Where(i => i != srcIdx);
            else
                targetIndices = new[] { dstIdx - 1 }; // offset -1

            var result = new List<(string, string)>();
            foreach (var i in targetIndices)
            {
                string modName = _gameNames[i]; // e.g. "UmoGame"
                string display = _gameDisplayNames[i];
                // 优先用去掉 "Game" 后缀的短名（e.g. "umo"），用路径斜杠包围防止误匹配
                string shortName = modName.ToLower().EndsWith("game")
                    ? modName.Substring(0, modName.Length - 4).ToLower()
                    : modName.ToLower();
                result.Add(($"/{shortName}/", display));
                // 也加上完整 moduleName 以防 bundle 内路径直接用模块名
                if (shortName != modName.ToLower())
                    result.Add(($"/{modName.ToLower()}/", display));
            }
            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Tab3：全局依赖总览
        // ═════════════════════════════════════════════════════════════════════


    }
}
#endif
