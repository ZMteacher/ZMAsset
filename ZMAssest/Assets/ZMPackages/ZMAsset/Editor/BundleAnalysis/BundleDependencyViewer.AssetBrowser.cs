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
        private struct AssetEntry
        {
            public string GameName;
            public string BundleName;
            public string AssetPath;
        }

        /// <summary>
        /// 商业化三栏资源浏览器。选择、列表与详情各自独立，避免在长页面中反复上下滚动。
        /// 数据读取仍复用原分析服务与缓存，布局层不介入 Bundle 解析逻辑。
        /// </summary>
        private void DrawTab5_AssetBrowser()
        {
            EditorGUILayout.LabelField("资源浏览器", BundleAnalyzerStyles.PageTitle, GUILayout.Height(32));
            EditorGUILayout.LabelField("按模块浏览 Bundle，并在同一工作区查看资源、依赖与文件信息。", BundleAnalyzerStyles.PageSubtitle, GUILayout.Height(22));
            EditorGUILayout.Space(10);

            if (_gameNames.Length == 0)
            {
                EditorGUILayout.HelpBox("未找到游戏配置，请先在设置中配置资源模块。", MessageType.Warning);
                return;
            }

            RefreshAssetBrowserSelection();

            float browserHeight = Mathf.Max(330f, position.height - 275f);
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(browserHeight)))
            {
                DrawAssetBrowserModules(browserHeight);
                GUILayout.Space(10);
                DrawAssetBrowserBundles(browserHeight);
                GUILayout.Space(10);
                DrawAssetBrowserDetails(browserHeight);
            }

            EditorGUILayout.Space(10);
            DrawAssetBrowserToolbar();
        }

        private void RefreshAssetBrowserSelection()
        {
            _t5GameIdx = Mathf.Clamp(_t5GameIdx, 0, _gameNames.Length - 1);
            if (!_t5NeedReload) return;

            _t5NeedReload = false;
            var manifest = _gameHasAb[_t5GameIdx] ? LoadOrGetManifest(_gameNames[_t5GameIdx]) : null;
            _t5BundleNames = manifest?.AllDeps.Keys.OrderBy(name => name).ToArray() ?? Array.Empty<string>();
            _t5BundleIdx = Mathf.Clamp(_t5BundleIdx, 0, Mathf.Max(0, _t5BundleNames.Length - 1));

            if (string.IsNullOrEmpty(_t5PendingBundle)) return;
            int pendingIndex = Array.IndexOf(_t5BundleNames, _t5PendingBundle);
            if (pendingIndex >= 0)
            {
                _t5BundleIdx = pendingIndex;
                LoadAssetList(_gameNames[_t5GameIdx], _t5PendingBundle);
            }
            _t5PendingBundle = string.Empty;
        }

        private void DrawAssetBrowserModules(float height)
        {
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard, GUILayout.Width(180), GUILayout.Height(height)))
            {
                EditorGUILayout.LabelField("资源模块", ZMBuildStyles.SettingsSectionTitle);
                EditorGUILayout.LabelField($"{_gameNames.Length} 个已配置模块", ZMBuildStyles.SettingsHint);
                EditorGUILayout.Space(8);

                _t5ModuleScroll = EditorGUILayout.BeginScrollView(_t5ModuleScroll);
                for (int i = 0; i < _gameNames.Length; i++)
                {
                    bool selected = i == _t5GameIdx;
                    GUIStyle rowStyle = selected ? BundleAnalyzerStyles.ListRowSelected : BundleAnalyzerStyles.ListRow;
                    Rect row = GUILayoutUtility.GetRect(0, 54, GUILayout.ExpandWidth(true));
                    if (GUI.Button(row, GUIContent.none, rowStyle))
                    {
                        _t5GameIdx = i;
                        _t5BundleIdx = 0;
                        _t5NeedReload = true;
                        GUI.FocusControl(null);
                    }

                    Color status = _gameHasAb[i] ? new Color32(77, 205, 139, 255) : new Color32(126, 135, 147, 255);
                    EditorGUI.DrawRect(new Rect(row.x + 10, row.y + 18, 7, 7), status);
                    GUI.Label(new Rect(row.x + 26, row.y + 7, row.width - 34, 20), _gameNames[i],
                        new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold, normal = { textColor = Color.white } });
                    GUI.Label(new Rect(row.x + 26, row.y + 28, row.width - 34, 17),
                        _gameHasAb[i] ? "已构建" : "尚未构建", ZMBuildStyles.SettingsHint);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawAssetBrowserBundles(float height)
        {
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard, GUILayout.ExpandWidth(true), GUILayout.Height(height)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Bundle 列表", ZMBuildStyles.SettingsSectionTitle);
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField($"{_t5BundleNames.Length} 项", ZMBuildStyles.StatusLabel, GUILayout.Width(64));
                }

                Rect searchRect = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
                _t5BundleSearch = ZMBuildStyles.DrawTextField(searchRect, _t5BundleSearch, ZMBuildStyles.Search);
                EditorGUILayout.Space(6);

                if (!_gameHasAb[_t5GameIdx])
                {
                    EditorGUILayout.HelpBox("该模块尚未构建，暂无 Bundle 数据。", MessageType.Info);
                    return;
                }

                string filter = (_t5BundleSearch ?? string.Empty).Trim();
                var currentManifest = LoadOrGetManifest(_gameNames[_t5GameIdx]);
                _t5BundleScroll = EditorGUILayout.BeginScrollView(_t5BundleScroll);
                for (int i = 0; i < _t5BundleNames.Length; i++)
                {
                    string bundleName = _t5BundleNames[i];
                    if (filter.Length > 0 && bundleName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    bool selected = i == _t5BundleIdx;
                    Rect row = GUILayoutUtility.GetRect(0, 52, GUILayout.ExpandWidth(true));
                    if (GUI.Button(row, GUIContent.none,
                            selected ? BundleAnalyzerStyles.ListRowSelected : BundleAnalyzerStyles.ListRow))
                    {
                        _t5BundleIdx = i;
                        GUI.FocusControl(null);
                    }

                    Rect bundleIcon = new Rect(row.x + 10, row.y + 14, 22, 22);
                    BundleAnalyzerIcons.Draw(bundleIcon, BundleAnalyzerIcons.Icon.Bundle,
                        selected ? Color.white : new Color32(133, 151, 171, 255), 1.5f);
                    GUI.Label(new Rect(row.x + 42, row.y + 6, row.width - 54, 20), bundleName,
                        new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold, normal = { textColor = Color.white } });
                    string path = Path.Combine(AbRootFullPath, _gameNames[_t5GameIdx], bundleName);
                    string cacheKey = $"{_gameNames[_t5GameIdx]}/{bundleName}";
                    string meta = File.Exists(path) ? FormatBytes(new FileInfo(path).Length) : "文件不可用";
                    if (_assetListCache.TryGetValue(cacheKey, out var cachedAssets))
                        meta += $"  ·  {cachedAssets.Length} 个资源";
                    int dependencyCount = currentManifest != null &&
                                          currentManifest.AllDeps.TryGetValue(bundleName, out var bundleDeps)
                        ? bundleDeps.Length : 0;
                    meta += $"  ·  {dependencyCount} 个依赖";
                    GUI.Label(new Rect(row.x + 42, row.y + 28, row.width - 54, 17), meta, ZMBuildStyles.SettingsHint);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawAssetBrowserDetails(float height)
        {
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard, GUILayout.Width(430), GUILayout.Height(height)))
            {
                EditorGUILayout.LabelField("Bundle 详情", ZMBuildStyles.SettingsSectionTitle);
                EditorGUILayout.Space(6);
                if (_t5BundleNames.Length == 0)
                {
                    EditorGUILayout.LabelField("选择包含构建产物的模块后查看详情。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(52));
                    return;
                }

                _t5BundleIdx = Mathf.Clamp(_t5BundleIdx, 0, _t5BundleNames.Length - 1);
                string gameName = _gameNames[_t5GameIdx];
                string bundleName = _t5BundleNames[_t5BundleIdx];
                string bundlePath = Path.Combine(AbRootFullPath, gameName, bundleName);
                string cacheKey = $"{gameName}/{bundleName}";
                var manifest = LoadOrGetManifest(gameName);
                string[] dependencies = manifest != null && manifest.AllDeps.TryGetValue(bundleName, out var deps)
                    ? deps
                    : Array.Empty<string>();

                EditorGUILayout.LabelField(bundleName, new GUIStyle(EditorStyles.label)
                {
                    fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = true,
                    normal = { textColor = Color.white }
                }, GUILayout.MinHeight(38));
                EditorGUILayout.LabelField($"模块  {gameName}", ZMBuildStyles.SettingsHint);
                EditorGUILayout.LabelField($"大小  {(File.Exists(bundlePath) ? FormatBytes(new FileInfo(bundlePath).Length) : "--")}", ZMBuildStyles.SettingsHint);
                EditorGUILayout.LabelField($"依赖  {dependencies.Length}", ZMBuildStyles.SettingsHint);
                EditorGUILayout.Space(8);

                using (new EditorGUILayout.HorizontalScope())
                {
                    bool assetsLoaded = _assetListCache.ContainsKey(cacheKey);
                    using (new EditorGUI.DisabledScope(assetsLoaded))
                    {
                        if (GUILayout.Button(assetsLoaded ? "资源已加载" : "加载资源",
                                assetsLoaded ? ZMBuildStyles.CompactSecondaryButton : ZMBuildStyles.CompactPrimaryButton))
                            LoadAssetList(gameName, bundleName);
                    }
                    if (GUILayout.Button("定位文件", ZMBuildStyles.CompactSecondaryButton))
                        EditorUtility.RevealInFinder(bundlePath);
                }

                EditorGUILayout.Space(10);
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool resourceSelected = !_t5ViewMode;
                    if (GUILayout.Button("包含资源",
                            resourceSelected ? ZMBuildStyles.SegmentSelected : ZMBuildStyles.Segment,
                            GUILayout.Height(34)))
                        _t5ViewMode = false;
                    GUILayout.Space(6);
                    if (GUILayout.Button("依赖项",
                            _t5ViewMode ? ZMBuildStyles.SegmentSelected : ZMBuildStyles.Segment,
                            GUILayout.Height(34)))
                        _t5ViewMode = true;
                }
                EditorGUILayout.Space(6);

                _t5DetailScroll = EditorGUILayout.BeginScrollView(_t5DetailScroll);
                if (!_t5ViewMode)
                {
                    if (!_assetListCache.TryGetValue(cacheKey, out var assets))
                        EditorGUILayout.LabelField("点击“加载资源”读取 Bundle 内容。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(48));
                    else if (assets.Length == 0)
                        EditorGUILayout.LabelField("该 Bundle 不包含可枚举资源。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(48));
                    else
                        foreach (string asset in assets)
                            DrawAssetDetailRow(asset);
                }
                else
                {
                    if (dependencies.Length == 0)
                        EditorGUILayout.LabelField("该 Bundle 没有依赖项。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(48));
                    else
                        foreach (string dependency in dependencies)
                            DrawBundleDependencyRow(dependency);
                }
                EditorGUILayout.EndScrollView();

                if (_assetListCache.TryGetValue(cacheKey, out var exportAssets) &&
                    GUILayout.Button($"导出资源清单 {ExportFormats[_exportFmtIdx]}", ZMBuildStyles.CompactSecondaryButton))
                    ExportAssetList(gameName, bundleName, exportAssets);
            }
        }

        private void DrawAssetDetailRow(string assetPath)
        {
            Rect row = GUILayoutUtility.GetRect(0, 76, GUILayout.ExpandWidth(true));
            GUI.Box(row, GUIContent.none, BundleAnalyzerStyles.ListRow);
            GUIStyle assetName = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color32(226, 231, 238, 255) }
            };
            GUIStyle assetMeta = new GUIStyle(ZMBuildStyles.SettingsHint)
            {
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                fontSize = 10
            };
            GUI.Label(new Rect(row.x + 10, row.y + 5, row.width - 20, 21),
                new GUIContent(Path.GetFileName(assetPath), assetPath), assetName);
            Rect pathRect = new Rect(row.x + 12, row.y + 27, row.width - 24, 18);
            GUI.Label(pathRect, new GUIContent(assetPath, assetPath), assetMeta);

            string size = GetAssetSizeStr(assetPath);
            GUI.Label(new Rect(row.x + 12, row.y + 50, row.width - 82, 18),
                string.IsNullOrEmpty(size) ? "大小未知" : $"资源大小  {size}", ZMBuildStyles.SettingsHint);
            GUIStyle locateStyle = new GUIStyle(ZMBuildStyles.CompactSecondaryButton)
            {
                fixedHeight = 24,
                stretchHeight = false,
                fontSize = 11,
                padding = new RectOffset(0, 0, 0, 0),
                normal = { textColor = new Color32(220, 226, 234, 255) }
            };
            if (GUI.Button(new Rect(row.xMax - 74, row.y + 46, 64, 24), "定位", locateStyle))
                PingAsset(assetPath);
            EditorGUIUtility.AddCursorRect(pathRect, MouseCursor.Text);
        }

        private void DrawBundleDependencyRow(string dependency)
        {
            Rect row = GUILayoutUtility.GetRect(0, 40, GUILayout.ExpandWidth(true));
            GUI.Box(row, GUIContent.none, BundleAnalyzerStyles.ListRow);
            Rect iconRect = new Rect(row.x + 10, row.y + 10, 20, 20);
            BundleAnalyzerIcons.Draw(iconRect, BundleAnalyzerIcons.Icon.Dependency,
                new Color32(126, 164, 202, 255), 1.5f);
            GUI.Label(new Rect(row.x + 40, row.y, row.width - 112, row.height),
                new GUIContent(dependency, dependency),
                new GUIStyle(BundleAnalyzerStyles.TableCell)
                {
                    alignment = TextAnchor.MiddleLeft,
                    clipping = TextClipping.Clip,
                    normal = { textColor = new Color32(218, 225, 234, 255) }
                });
            GUIStyle copyStyle = new GUIStyle(ZMBuildStyles.CompactSecondaryButton)
            {
                fixedHeight = 24,
                stretchHeight = false,
                fontSize = 11,
                padding = new RectOffset(0, 0, 0, 0)
            };
            if (GUI.Button(new Rect(row.xMax - 62, row.y + 8, 52, 24),
                    new GUIContent("复制", "复制完整 Bundle 名称"), copyStyle))
            {
                EditorGUIUtility.systemCopyBuffer = dependency;
                ShowNotification(new GUIContent("已复制 Bundle 名称"));
            }
            GUILayout.Space(5);
        }

        private void DrawAssetBrowserToolbar()
        {
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    int totalBundles = _builtGameNames.Sum(game =>
                    {
                        var manifest = LoadOrGetManifest(game);
                        return manifest?.AllDeps.Count ?? 0;
                    });
                    EditorGUILayout.LabelField($"已加载 {_assetListCache.Count} / {totalBundles} 个 Bundle",
                        ZMBuildStyles.SettingsHint, GUILayout.Width(190));
                    if (!_t5BatchLoading && GUILayout.Button("加载当前模块", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(112)))
                        BatchLoadGame(_gameNames[_t5GameIdx]);
                    if (!_t5BatchLoading && GUILayout.Button("加载全部模块", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(112)))
                        BatchLoadAll();
                    if (!_t5BatchLoading && _assetListCache.Count > 0 &&
                        GUILayout.Button("清空缓存", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(88)))
                    {
                        _assetListCache.Clear();
                        _t5SearchResults.Clear();
                    }
                    GUILayout.FlexibleSpace();
                    Rect search = GUILayoutUtility.GetRect(210, 34);
                    _t5SearchKeyword = ZMBuildStyles.DrawTextField(search, _t5SearchKeyword, ZMBuildStyles.InputField);
                    if (GUILayout.Button("搜索", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(68)))
                    {
                        RunAssetSearch(_t5SearchKeyword);
                        _t5LastSearchKey = _t5SearchKeyword;
                    }
                    bool hasSearchResult = !string.IsNullOrEmpty(_t5LastSearchKey);
                    if (GUILayout.Button(hasSearchResult ? "重新扫描" : "全局扫描",
                            hasSearchResult ? ZMBuildStyles.CompactSecondaryButton : ZMBuildStyles.CompactPrimaryButton,
                            GUILayout.Width(88)))
                    {
                        RunGlobalAssetSearch(_t5SearchKeyword);
                        _t5LastSearchKey = _t5SearchKeyword;
                    }
                }

                if (_t5BatchLoading)
                {
                    float progress = _t5BatchTotal > 0 ? (float)_t5BatchProgress / _t5BatchTotal : 0f;
                    EditorGUI.ProgressBar(GUILayoutUtility.GetRect(18, 18, GUILayout.ExpandWidth(true)), progress,
                        $"{_t5BatchStatus} ({_t5BatchProgress}/{_t5BatchTotal})");
                }
                else if (_t5SearchResults.Count > 0)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"找到 {_t5SearchResults.Count} 条匹配资源", ZMBuildStyles.SettingsHint);
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}",
                                ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(82)))
                            ExportSearchResults(_t5LastSearchKey);
                    }

                    _t5SearchScroll = EditorGUILayout.BeginScrollView(_t5SearchScroll, GUILayout.MaxHeight(116));
                    foreach (AssetEntry result in _t5SearchResults)
                    {
                        using (new EditorGUILayout.HorizontalScope(BundleAnalyzerStyles.ListRow, GUILayout.Height(34)))
                        {
                            EditorGUILayout.LabelField(result.GameName, ZMBuildStyles.SettingsHint,
                                GUILayout.Width(105), GUILayout.Height(34));
                            EditorGUILayout.LabelField(result.BundleName, BundleAnalyzerStyles.TableCell,
                                GUILayout.Width(180), GUILayout.Height(34));
                            EditorGUILayout.LabelField(result.AssetPath, ZMBuildStyles.SettingsHint, GUILayout.Height(34));
                            if (GUILayout.Button("定位", ZMBuildStyles.CardEditButton, GUILayout.Width(48), GUILayout.Height(34)))
                            {
                                int gameIndex = Array.IndexOf(_gameNames, result.GameName);
                                if (gameIndex >= 0) JumpToTab5(gameIndex, result.BundleName);
                            }
                            if (GUILayout.Button("Ping", ZMBuildStyles.CardEditButton, GUILayout.Width(48), GUILayout.Height(34)))
                                PingAsset(result.AssetPath);
                        }
                    }
                    EditorGUILayout.EndScrollView();
                }
            }
        }

        // 保留旧版绘制实现作为短期回滚锚点；确认新版体验后可在后续版本移除。
        private void DrawTab5_AssetBrowserLegacy()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("资源浏览器", BundleAnalyzerStyles.PageTitle, GUILayout.Height(32));
            EditorGUILayout.LabelField("查看指定 Bundle 内包含的所有资源，或按关键词搜索资源所属 Bundle。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(38));
            EditorGUILayout.Space(4);

            if (_gameNames.Length == 0)
            {
                EditorGUILayout.HelpBox("未找到游戏配置。", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            // ── 游戏/Bundle 选择（显示全部游戏）──
            EditorGUILayout.LabelField("游戏", ZMBuildStyles.SettingsLabel);
            int newGame5 = DrawAnalyzerPopup("legacy.game", _t5GameIdx, _gameDisplayNames, GUILayout.ExpandWidth(true));
            if (newGame5 != _t5GameIdx) { _t5GameIdx = newGame5; _t5BundleIdx = 0; _t5NeedReload = true; }

            // 若未构建则提示
            if (!_gameHasAb[_t5GameIdx])
            {
                EditorGUILayout.HelpBox($"[{_gameNames[_t5GameIdx]}] 尚未构建 AB 包，无法浏览资源。", MessageType.Warning);
                EditorGUILayout.EndVertical();
                return;
            }

            // 加载 bundle 列表
            if (_t5NeedReload)
            {
                _t5NeedReload = false;
                var md = LoadOrGetManifest(_gameNames[_t5GameIdx]);
                _t5BundleNames = md?.AllDeps.Keys.OrderBy(k => k).ToArray() ?? Array.Empty<string>();
                _t5BundleIdx   = 0;
                // If Tab2 requested a specific bundle, select it now
                if (!string.IsNullOrEmpty(_t5PendingBundle))
                {
                    int idx = Array.IndexOf(_t5BundleNames, _t5PendingBundle);
                    if (idx >= 0)
                    {
                        _t5BundleIdx = idx;
                        // Auto-load the asset list for this bundle
                        LoadAssetList(_gameNames[_t5GameIdx], _t5PendingBundle);
                    }
                    _t5PendingBundle = string.Empty;
                }
            }

            if (_t5BundleNames.Length > 0)
            {
                _t5BundleIdx = Mathf.Clamp(_t5BundleIdx, 0, _t5BundleNames.Length - 1);
                EditorGUILayout.LabelField("Bundle", ZMBuildStyles.SettingsLabel);
                _t5BundleIdx = DrawAnalyzerPopup("legacy.bundle", _t5BundleIdx, _t5BundleNames, GUILayout.ExpandWidth(true));
            }

            EditorGUILayout.EndVertical();

            // ── 批量加载面板 ──────────────────────────────────────────────────
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("批量加载 Bundle（启用全局资源搜索）", EditorStyles.boldLabel);

            // 统计已缓存数量
            int totalBuiltBundles = 0;
            foreach (var gn in _builtGameNames)
            {
                var md = LoadOrGetManifest(gn);
                if (md != null) totalBuiltBundles += md.AllDeps.Count;
            }
            int cachedCount = _assetListCache.Count;

            EditorGUILayout.LabelField($"已加载: {cachedCount} 个 Bundle / 共 {totalBuiltBundles} 个可用 Bundle", EditorStyles.miniLabel);

            if (_t5BatchLoading)
            {
                // 显示进度
                float prog = _t5BatchTotal > 0 ? (float)_t5BatchProgress / _t5BatchTotal : 0f;
                EditorGUI.ProgressBar(GUILayoutUtility.GetRect(18, 18, GUILayout.ExpandWidth(true)), prog,
                    $"{_t5BatchStatus}  ({_t5BatchProgress}/{_t5BatchTotal})");
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("批量加载当前游戏所有 Bundle", GUILayout.Height(24)))
                {
                    // 使用当前选中游戏（已确保是已构建的）
                    BatchLoadGame(_gameNames[_t5GameIdx]);
                }
                if (GUILayout.Button("批量加载全部游戏", GUILayout.Height(24)))
                    BatchLoadAll();
                if (cachedCount > 0 && GUILayout.Button("清空已加载缓存", GUILayout.Height(24), GUILayout.Width(110)))
                {
                    _assetListCache.Clear();
                    _t5SearchResults.Clear();
                    Repaint();
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(4);

            // ── 查看模式切换 ──────────────────────────────────────────────────
            int selMode = GUILayout.SelectionGrid(_t5ViewMode ? 1 : 0,
                new[] { "单包查看", "全部已加载Bundle" }, 2, GUILayout.Height(24));
            _t5ViewMode = selMode == 1;

            EditorGUILayout.Space(2);

            if (!_t5ViewMode)
            {
                // ── 单包查看模式 ──
                if (_t5BundleNames.Length > 0)
                {
                    string selectedBundle = _t5BundleNames[_t5BundleIdx];
                    string gameName       = _gameNames[_t5GameIdx];
                    string cacheKey       = $"{gameName}/{selectedBundle}";

                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.BeginHorizontal();
                    _t5ShowFoldout = EditorGUILayout.Foldout(_t5ShowFoldout, $"📦 {selectedBundle} 内资源", true, EditorStyles.foldoutHeader);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("加载", GUILayout.Width(52)))
                        LoadAssetList(gameName, selectedBundle);
                    if (_assetListCache.ContainsKey(cacheKey) && GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}", GUILayout.Width(80)))
                        ExportAssetList(gameName, selectedBundle, _assetListCache[cacheKey]);
                    EditorGUILayout.EndHorizontal();

                    if (_t5ShowFoldout && _assetListCache.TryGetValue(cacheKey, out var assets))
                    {
                        if (assets.Length == 0)
                            EditorGUILayout.HelpBox("该 Bundle 为空或无法读取资源列表（可能是仅含原始数据的 bundle）。", MessageType.Info);
                        else
                        {
                            EditorGUILayout.LabelField($"共 {assets.Length} 个资源", EditorStyles.miniLabel);
                            _t5AssetScroll = EditorGUILayout.BeginScrollView(_t5AssetScroll, GUILayout.MaxHeight(260));
                            foreach (var a in assets)
                            {
                                EditorGUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField($"  {a}", EditorStyles.miniLabel);
                                string sz = GetAssetSizeStr(a);
                                if (!string.IsNullOrEmpty(sz))
                                    EditorGUILayout.LabelField(sz, EditorStyles.miniLabel, GUILayout.Width(72));
                                if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(56)))
                                    PingAsset(a);
                                EditorGUILayout.EndHorizontal();
                            }
                            EditorGUILayout.EndScrollView();
                        }
                    }
                    else if (_t5ShowFoldout && !_assetListCache.ContainsKey(cacheKey))
                    {
                        EditorGUILayout.LabelField("  点击「加载」读取 Bundle 内资源列表", EditorStyles.miniLabel);
                    }
                    EditorGUILayout.EndVertical();
                }
            }
            else
            {
                // ── 全部已加载 Bundle 列表模式 ──
                if (_assetListCache.Count == 0)
                {
                    EditorGUILayout.HelpBox("尚未加载任何 Bundle，请先点击上方「批量加载」按钮。", MessageType.Info);
                }
                else
                {
                    // 按 GameName / BundleName 排序
                    var allEntries = _assetListCache
                        .OrderBy(kvp => kvp.Key)
                        .ToList();

                    int totalAssets = allEntries.Sum(e => e.Value.Length);
                    EditorGUILayout.BeginHorizontal("box");
                    EditorGUILayout.LabelField(
                        $"已加载 {allEntries.Count} 个 Bundle，含 {totalAssets} 个资源",
                        EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("全部展开",  GUILayout.Width(68)))
                        foreach (var k in allEntries.Select(e => e.Key).ToList())
                            _t5Foldouts[k] = true;
                    if (GUILayout.Button("全部折叠",  GUILayout.Width(68)))
                        foreach (var k in allEntries.Select(e => e.Key).ToList())
                            _t5Foldouts[k] = false;
                    if (GUILayout.Button($"导出全部 {ExportFormats[_exportFmtIdx]}", GUILayout.Width(96)))
                        ExportAllLoadedBundles(allEntries);
                    EditorGUILayout.EndHorizontal();

                    _t5AllBundlesScroll = EditorGUILayout.BeginScrollView(_t5AllBundlesScroll);
                    foreach (var kvp in allEntries)
                    {
                        // 解析 cacheKey = "GameName/bundleName"
                        int slash = kvp.Key.IndexOf('/');
                        string gn = slash >= 0 ? kvp.Key.Substring(0, slash) : kvp.Key;
                        string bn = slash >= 0 ? kvp.Key.Substring(slash + 1) : kvp.Key;

                        if (!_t5Foldouts.TryGetValue(kvp.Key, out bool folded)) folded = false;

                        EditorGUILayout.BeginVertical("box");
                        EditorGUILayout.BeginHorizontal();
                        bool newFolded = EditorGUILayout.Foldout(folded, $"📦  [{gn}]  {bn}  ({kvp.Value.Length} 资源)", true);
                        if (newFolded != folded) _t5Foldouts[kvp.Key] = newFolded;
                        EditorGUILayout.EndHorizontal();

                        if (newFolded)
                        {
                            if (kvp.Value.Length == 0)
                                EditorGUILayout.LabelField("   （空 Bundle 或非资源包）", EditorStyles.miniLabel);
                            else
                                foreach (var asset in kvp.Value)
                                {
                                    EditorGUILayout.BeginHorizontal();
                                    EditorGUILayout.LabelField($"      {asset}", EditorStyles.miniLabel);
                                    string sz = GetAssetSizeStr(asset);
                                    if (!string.IsNullOrEmpty(sz))
                                        EditorGUILayout.LabelField(sz, EditorStyles.miniLabel, GUILayout.Width(72));
                                    if (GUILayout.Button("定位", EditorStyles.miniButton, GUILayout.Width(56)))
                                        PingAsset(asset);
                                    EditorGUILayout.EndHorizontal();
                                }
                        }
                        EditorGUILayout.EndVertical();
                    }
                    EditorGUILayout.EndScrollView();
                }
            }

            EditorGUILayout.Space(4);

            // ── 资源路径搜索 ──
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("🔍 资源路径搜索（在已加载的游戏中查找）", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("在此处搜索资源归属。「搜索」只查已加载 Bundle；「全局扫描」扫描所有游戏全部 Bundle（含未加载）。", BundleAnalyzerStyles.InfoBox, GUILayout.Height(38));
            EditorGUILayout.BeginHorizontal();
            _t5SearchKeyword = EditorGUILayout.TextField("关键词", _t5SearchKeyword);
            if (GUILayout.Button("搜索", GUILayout.Width(52)) || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
            {
                RunAssetSearch(_t5SearchKeyword);
                _t5LastSearchKey = _t5SearchKeyword;
                GUI.FocusControl(null);
            }
            if (GUILayout.Button("全局扫描", GUILayout.Width(68)))
            {
                RunGlobalAssetSearch(_t5SearchKeyword);
                _t5LastSearchKey = _t5SearchKeyword;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            if (_t5SearchResults.Count > 0)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"搜索「{_t5LastSearchKey}」：找到 {_t5SearchResults.Count} 条", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button($"导出 {ExportFormats[_exportFmtIdx]}", GUILayout.Width(80)))
                    ExportSearchResults(_t5LastSearchKey);
                EditorGUILayout.EndHorizontal();

                _t5SearchScroll = EditorGUILayout.BeginScrollView(_t5SearchScroll, GUILayout.MaxHeight(300));
                foreach (var r in _t5SearchResults)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(r.GameName,   GUILayout.Width(120));
                    EditorGUILayout.LabelField(r.BundleName, GUILayout.Width(200));
                    EditorGUILayout.LabelField(r.AssetPath,  EditorStyles.miniLabel);
                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(56)))
                        PingAsset(r.AssetPath);
                    if (GUILayout.Button("→ 定位", GUILayout.Width(55)))
                    {
                        int gameIdx = Array.IndexOf(_gameNames, r.GameName);
                        if (gameIdx >= 0)
                            JumpToTab5(gameIdx, r.BundleName);
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
            else if (!string.IsNullOrEmpty(_t5LastSearchKey))
            {
                EditorGUILayout.HelpBox($"未找到包含「{_t5LastSearchKey}」的资源。请点击「全局扫描」扫描所有 Bundle。", MessageType.Info);
            }
        }

        private void BatchLoadGame(string gameName)
        {
            var md = LoadOrGetManifest(gameName);
            if (md == null) return;
            var bundles = md.AllDeps.Keys
                .Where(b => !_assetListCache.ContainsKey($"{gameName}/{b}"))
                .ToArray();
            DoBatchLoad(new[] { (gameName, bundles) });
        }

        private void BatchLoadAll()
        {
            var groups = new List<(string, string[])>();
            foreach (var gameName in _builtGameNames)
            {
                var md = LoadOrGetManifest(gameName);
                if (md == null) continue;
                var pending = md.AllDeps.Keys
                    .Where(b => !_assetListCache.ContainsKey($"{gameName}/{b}"))
                    .ToArray();
                if (pending.Length > 0)
                    groups.Add((gameName, pending));
            }
            DoBatchLoad(groups.ToArray());
        }

        private void DoBatchLoad((string gameName, string[] bundles)[] groups)
        {
            _t5BatchTotal    = groups.Sum(g => g.bundles.Length);
            _t5BatchProgress = 0;
            _t5BatchLoading  = true;
            Repaint();

            int processed = 0;
            foreach (var (gameName, bundles) in groups)
            {
                foreach (var bundleName in bundles)
                {
                    _t5BatchStatus = $"加载 {gameName}/{bundleName}";
                    LoadAssetList(gameName, bundleName);
                    processed++;
                    _t5BatchProgress = processed;

                    // 每隔 5 个 bundle 刷新 UI
                    if (processed % 5 == 0) Repaint();
                }
            }

            _t5BatchLoading = false;
            _t5BatchStatus  = string.Empty;
            Repaint();
            Debug.Log($"[BundleDependencyViewer] 批量加载完成，共加载 {processed} 个 Bundle，资源总数: {_assetListCache.Values.Sum(a => a.Length)}");
        }

        /// <summary>
        /// 校验文件是否为合法的 Unity AssetBundle（文件头为 "UnityFS" 或 "UnityRaw" 或 "UnityWeb"）。
        /// hotscripts 等包含 DLL 的 bundle 文件头不符合，需跳过，避免 Unity 打印错误日志。
        /// </summary>
        private static bool IsValidAssetBundle(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 7) return false;
                var header = new byte[7];
                fs.Read(header, 0, 7);
                string sig = System.Text.Encoding.ASCII.GetString(header);
                return sig.StartsWith("UnityFS") || sig.StartsWith("UnityRaw") || sig.StartsWith("UnityWeb");
            }
            catch { return false; }
        }

        private void LoadAssetList(string gameName, string bundleName)
        {
            string cacheKey  = $"{gameName}/{bundleName}";
            if (_assetListCache.ContainsKey(cacheKey)) return;

            string bundlePath = Path.Combine(AbRootFullPath, gameName, bundleName);
            if (!File.Exists(bundlePath))
            {
                _assetListCache[cacheKey] = Array.Empty<string>();
                Debug.LogWarning($"[BundleDependencyViewer] Bundle 文件不存在: {bundlePath}");
                return;
            }

            // 校验文件头，避免对非 AssetBundle 文件（如 hotscripts DLL 包）调用 LoadFromFile
            if (!IsValidAssetBundle(bundlePath))
            {
                _assetListCache[cacheKey] = new[] { "<非资源包：可能是 DLL 或加密 Bundle>" };
                return;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    _assetListCache[cacheKey] = Array.Empty<string>();
                    return;
                }
                _assetListCache[cacheKey] = bundle.GetAllAssetNames().OrderBy(a => a).ToArray();
            }
            catch (Exception e)
            {
                Debug.LogError($"[BundleDependencyViewer] 加载 Bundle 资产列表失败: {e.Message}");
                _assetListCache[cacheKey] = Array.Empty<string>();
            }
            finally
            {
                bundle?.Unload(true);
            }
        }

        private void RunAssetSearch(string keyword)
        {
            _t5SearchResults.Clear();
            if (string.IsNullOrEmpty(keyword)) return;

            string kw = keyword.ToLower();
            foreach (var kvp in _assetListCache)
            {
                // cacheKey = "GameName/bundleName"
                int slash = kvp.Key.IndexOf('/');
                if (slash < 0) continue;
                string gn = kvp.Key.Substring(0, slash);
                string bn = kvp.Key.Substring(slash + 1);

                foreach (var asset in kvp.Value)
                    if (asset.ToLower().Contains(kw))
                        _t5SearchResults.Add(new AssetEntry { GameName = gn, BundleName = bn, AssetPath = asset });
            }
        }

        /// <summary>
        /// Search all bundles across all built games, including ones not yet loaded.
        /// Uses _assetListCache for already-loaded bundles; loads others on demand (then caches them).
        /// </summary>
        private void RunGlobalAssetSearch(string keyword)
        {
            _t5SearchResults.Clear();
            if (string.IsNullOrEmpty(keyword)) return;

            string kw = keyword.ToLower();

            // Collect all (gameName, bundleName) pairs to search
            var allPairs = new List<(string gameName, string bundleName)>();
            foreach (var gameName in _gameNames.Where((n, i) => _gameHasAb[i]))
            {
                var md = LoadOrGetManifest(gameName);
                if (md == null) continue;
                foreach (var bn in md.AllDeps.Keys)
                    allPairs.Add((gameName, bn));
            }

            int total = allPairs.Count;
            try
            {
                for (int i = 0; i < total; i++)
                {
                    var (gameName, bundleName) = allPairs[i];
                    EditorUtility.DisplayProgressBar(
                        "全局资源搜索",
                        $"[{i + 1}/{total}] {gameName}/{bundleName}",
                        (float)(i + 1) / total);

                    string cacheKey = $"{gameName}/{bundleName}";
                    string[] assets;
                    if (!_assetListCache.TryGetValue(cacheKey, out assets))
                    {
                        // Not yet loaded — load it and add to cache
                        LoadAssetList(gameName, bundleName);
                        _assetListCache.TryGetValue(cacheKey, out assets);
                    }
                    if (assets == null) continue;

                    foreach (var asset in assets)
                        if (asset.ToLower().Contains(kw))
                            _t5SearchResults.Add(new AssetEntry { GameName = gameName, BundleName = bundleName, AssetPath = asset });
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Repaint();
        }

        private void ExportAssetList(string gameName, string bundleName, string[] assets)
        {
            string ext     = IsCsv ? "csv" : "txt";
            string defName = $"{gameName}_{bundleName}_assets.{ext}";
            string path    = EditorUtility.SaveFilePanel("导出资源列表", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("GameName,BundleName,AssetPath");
                foreach (var a in assets)
                    sw.WriteLine($"{CsvEscape(gameName)},{CsvEscape(bundleName)},{CsvEscape(a)}");
            }
            else
            {
                sw.WriteLine($"=== Bundle 资源列表 ===");
                sw.WriteLine($"游戏: {gameName}    Bundle: {bundleName}    共 {assets.Length} 个资源");
                sw.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine(new string('-', 80));
                foreach (var a in assets)
                    sw.WriteLine(a);
            }
            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出: {path}");
        }

        private void ExportAllLoadedBundles(List<KeyValuePair<string, string[]>> entries)
        {
            string ext     = IsCsv ? "csv" : "txt";
            string defName = $"AllBundleAssets_{DateTime.Now:yyyyMMdd_HHmm}.{ext}";
            string path    = EditorUtility.SaveFilePanel("导出全部已加载 Bundle 资源", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("GameName,BundleName,AssetPath");
                foreach (var kvp in entries)
                {
                    int slash = kvp.Key.IndexOf('/');
                    string gn = slash >= 0 ? kvp.Key.Substring(0, slash) : kvp.Key;
                    string bn = slash >= 0 ? kvp.Key.Substring(slash + 1) : kvp.Key;
                    foreach (var a in kvp.Value)
                        sw.WriteLine($"{CsvEscape(gn)},{CsvEscape(bn)},{CsvEscape(a)}");
                }
            }
            else
            {
                sw.WriteLine($"=== 全部 Bundle 资源列表 ===");
                sw.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}    Bundle数: {entries.Count}");
                sw.WriteLine(new string('=', 80));
                foreach (var kvp in entries)
                {
                    int slash = kvp.Key.IndexOf('/');
                    string gn = slash >= 0 ? kvp.Key.Substring(0, slash) : kvp.Key;
                    string bn = slash >= 0 ? kvp.Key.Substring(slash + 1) : kvp.Key;
                    sw.WriteLine($"[{gn}]  {bn}  ({kvp.Value.Length} 资源)");
                    foreach (var a in kvp.Value)
                        sw.WriteLine($"  {a}");
                    sw.WriteLine();
                }
            }
            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出: {path}");
        }

        private void ExportSearchResults(string keyword)
        {
            string ext     = IsCsv ? "csv" : "txt";
            string defName = $"AssetSearch_{keyword.Replace("/", "_")}.{ext}";
            string path    = EditorUtility.SaveFilePanel("导出搜索结果", "", defName, ext);
            if (string.IsNullOrEmpty(path)) return;

            using var sw = new StreamWriter(path, false, System.Text.Encoding.UTF8);
            if (IsCsv)
            {
                sw.WriteLine("GameName,BundleName,AssetPath");
                foreach (var r in _t5SearchResults)
                    sw.WriteLine($"{CsvEscape(r.GameName)},{CsvEscape(r.BundleName)},{CsvEscape(r.AssetPath)}");
            }
            else
            {
                sw.WriteLine($"=== 资源搜索结果 ===  关键词: {keyword}  共 {_t5SearchResults.Count} 条");
                sw.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sw.WriteLine(new string('-', 80));
                foreach (var r in _t5SearchResults)
                    sw.WriteLine($"{r.GameName,-20}{r.BundleName,-40}{r.AssetPath}");
            }
            EditorUtility.RevealInFinder(path);
            Debug.Log($"[BundleDependencyViewer] 已导出: {path}");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Tab4：Bundle 大小统计
        // ═════════════════════════════════════════════════════════════════════


    }
}
#endif
