using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.IO;

public partial class BuildWindows : EditorWindow
{
    private enum Page { AssetBundle, HotPatch, Settings, Manual }

    [SerializeField] private BuildBundleWindow buildBundleWindow = new BuildBundleWindow();
    [SerializeField] private BuildHotPatchWindow buildHotWindow = new BuildHotPatchWindow();
    [SerializeField] private Page currentPage;
    [SerializeField] private Vector2 settingsScroll;
    [SerializeField] private bool showEncryptKey;
    private readonly Dictionary<string, int> popupSelections = new Dictionary<string, int>();
    [SerializeField] private BundleModuleDrawer moduleDrawer = new BundleModuleDrawer();
    [System.NonSerialized] private int displayedProgressRunId = -1;
    [System.NonSerialized] private float displayedOverallProgress;
    [System.NonSerialized] private float displayedStageProgress;
    [System.NonSerialized] private float overallProgressVelocity;
    [System.NonSerialized] private float stageProgressVelocity;
    [System.NonSerialized] private double lastProgressDrawTime;

    [MenuItem("ZM/AssetBundle Hub", priority = 1)]
    public static void ShowAssetBundleWindow()
    {
        BuildWindows window = GetWindow<BuildWindows>();
        window.UpdateWindowTitle();
        window.minSize = new Vector2(900, 600);
        if (window.position.width < 900f)
            window.position = CenteredRect(1100, 680);
        window.Show();
    }

    private static Rect CenteredRect(float width, float height)
    {
        Rect main = EditorGUIUtility.GetMainWindowPosition();
        return new Rect(main.x + (main.width - width) * .5f, main.y + (main.height - height) * .5f, width, height);
    }

    private void OnEnable()
    {
        UpdateWindowTitle();
        wantsMouseMove = true;
        buildBundleWindow ??= new BuildBundleWindow();
        buildHotWindow ??= new BuildHotPatchWindow();
        moduleDrawer ??= new BundleModuleDrawer();
        buildBundleWindow.EditModuleRequested = OpenModuleDrawer;
        buildHotWindow.EditModuleRequested = OpenModuleDrawer;
        ZMBuildProgress.Changed -= Repaint;
        ZMBuildProgress.Changed += Repaint;
        ZMEditorAnimation.Restart("ZMAsset.Page", .2f);
        Refresh();
    }

    private void OnDisable()
    {
        ZMBuildProgress.Changed -= Repaint;
    }

    private void UpdateWindowTitle()
    {
        Texture icon = EditorGUIUtility.IconContent("UnityLogo").image;
        titleContent = new GUIContent(string.Empty, icon, "ZMAsset 构建中心");
    }

    private void Refresh()
    {
        buildBundleWindow.Initzation();
        buildHotWindow.Initzation();
        Repaint();
    }

    private void OnGUI()
    {
        ZMBuildStyles.Ensure();
        EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), ZMBuildStyles.Window);
        using (new EditorGUI.DisabledScope(moduleDrawer.IsOpen || ZMBuildProgress.Visible))
        {
            DrawHeader(new Rect(0, 0, position.width, 66));
            DrawSidebar(new Rect(0, 66, 190, position.height - 66));

            GUILayout.BeginArea(new Rect(190, 66, position.width - 190, position.height - 66));
            float pageProgress = ZMEditorAnimation.Tween("ZMAsset.Page", 1f, .2f);
            Color oldGuiColor = GUI.color;
            Matrix4x4 oldGuiMatrix = GUI.matrix;
            GUI.color = new Color(oldGuiColor.r, oldGuiColor.g, oldGuiColor.b, oldGuiColor.a * Mathf.Lerp(.35f, 1f, pageProgress));
            GUI.matrix = Matrix4x4.TRS(new Vector3((1f - pageProgress) * 14f, 0, 0), Quaternion.identity, Vector3.one) * oldGuiMatrix;
            switch (currentPage)
            {
                case Page.AssetBundle: buildBundleWindow.OGUI(); break;
                case Page.HotPatch: buildHotWindow.OGUI(); break;
                case Page.Settings: DrawSettings(); break;
                case Page.Manual: DrawManual(); break;
            }
            GUI.matrix = oldGuiMatrix;
            GUI.color = oldGuiColor;
            GUILayout.EndArea();
        }
        if (moduleDrawer.IsOpen) DrawModuleDrawer();
        if (ZMBuildProgress.Visible) DrawBuildProgress();
    }

    private void DrawBuildProgress()
    {
        UpdateDisplayedProgress();
        EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(0, 0, 0, .62f));
        float width = Mathf.Min(590f, position.width - 80f);
        const float height = 390f;
        Rect panel = new Rect((position.width - width) * .5f, (position.height - height) * .5f, width, height);
        GUI.Box(panel, GUIContent.none, ZMBuildStyles.BuildProgressPanel);

        float x = panel.x + 26;
        float contentWidth = panel.width - 52;
        bool failed = ZMBuildProgress.State == ZMBuildProgress.BuildState.Failed;
        if (failed)
        {
            Texture errorIcon = EditorGUIUtility.IconContent("console.erroricon").image;
            if (errorIcon != null)
                GUI.DrawTexture(new Rect(x, panel.y + 23, 32, 32), errorIcon, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(x + 40, panel.y + 17, contentWidth - 40, 32),
                "资源构建失败", ZMBuildStyles.BuildFailureTitle);
            GUI.Label(new Rect(x + 40, panel.y + 47, contentWidth - 40, 18),
                $"{ZMBuildProgress.Title} · 构建流程已停止", ZMBuildStyles.BuildFailureSubtitle);

            Rect failureRect = new Rect(x, panel.y + 78, contentWidth, 55);
            GUI.Box(failureRect, GUIContent.none, ZMBuildStyles.BuildFailurePanel);
            GUI.Label(new Rect(failureRect.x + 12, failureRect.y + 6, failureRect.width - 24, 18),
                ZMBuildProgress.Stage, ZMBuildStyles.BuildFailureLabel);
            GUI.Label(new Rect(failureRect.x + 12, failureRect.y + 24, failureRect.width - 24, 26),
                string.IsNullOrEmpty(ZMBuildProgress.Detail) ? "未返回具体错误信息，请查看 Console。" : ZMBuildProgress.Detail,
                ZMBuildStyles.BuildFailureDetail);
        }
        else
        {
            GUI.Label(new Rect(x, panel.y + 20, contentWidth - 40, 28), ZMBuildProgress.Title, ZMBuildStyles.CardTitle);
            GUI.Label(new Rect(x, panel.y + 48, contentWidth, 20), BuildStateText(), ZMBuildStyles.BuildProgressState);
            GUI.Label(new Rect(x, panel.y + 84, contentWidth, 22), ZMBuildProgress.Stage, ZMBuildStyles.SettingsSectionTitle);
            GUI.Label(new Rect(x, panel.y + 108, contentWidth, 19), ZMBuildProgress.Detail, ZMBuildStyles.SettingsHint);
        }

        GUI.Label(new Rect(x, panel.y + 143, 150, 18), "总体进度", ZMBuildStyles.SettingsLabel);
        GUI.Label(new Rect(panel.xMax - 156, panel.y + 143, 130, 18),
            $"{ZMBuildProgress.CompletedJobs} / {ZMBuildProgress.TotalJobs} 模块", ZMBuildStyles.BuildProgressRightLabel);
        DrawProgressBar(new Rect(x, panel.y + 166, contentWidth, 12), displayedOverallProgress, failed);

        GUI.Label(new Rect(x, panel.y + 191, 150, 18), "当前阶段", ZMBuildStyles.SettingsLabel);
        GUI.Label(new Rect(panel.xMax - 126, panel.y + 191, 100, 18),
            $"{Mathf.RoundToInt(displayedStageProgress * 100f)}%", ZMBuildStyles.BuildProgressRightLabel);
        DrawProgressBar(new Rect(x, panel.y + 214, contentWidth, 12), displayedStageProgress, failed);

        Rect logRect = new Rect(x, panel.y + 243, contentWidth, 78);
        GUI.Box(logRect, GUIContent.none, ZMBuildStyles.BuildLogPanel);
        int logStart = Mathf.Max(0, ZMBuildProgress.RecentLogs.Count - 3);
        for (int i = logStart; i < ZMBuildProgress.RecentLogs.Count; i++)
            GUI.Label(new Rect(logRect.x + 12, logRect.y + 8 + (i - logStart) * 20, logRect.width - 24, 18),
                "• " + ZMBuildProgress.RecentLogs[i], ZMBuildStyles.BuildLogText);

        GUI.Label(new Rect(x, panel.yMax - 49, 180, 34), $"耗时 {FormatElapsed(ZMBuildProgress.ElapsedSeconds)}", ZMBuildStyles.SettingsHint);
        if (ZMBuildProgress.State == ZMBuildProgress.BuildState.Running)
        {
            using (new EditorGUI.DisabledScope(!ZMBuildProgress.CanCancel))
                if (GUI.Button(new Rect(panel.xMax - 126, panel.yMax - 52, 100, 34),
                    ZMBuildProgress.CanCancel ? "取消构建" : "构建中…", ZMBuildStyles.CompactSecondaryButton))
                    ZMBuildProgress.RequestCancel();
        }
        else
        {
            float closeX = panel.xMax - 120;
            if (failed && GUI.Button(new Rect(closeX - 118, panel.yMax - 52, 106, 34), "查看 Console", ZMBuildStyles.BuildFailureButton))
                EditorApplication.ExecuteMenuItem("Window/General/Console");
            else if (!failed && !string.IsNullOrEmpty(ZMBuildProgress.OutputPath) && GUI.Button(new Rect(panel.xMax - 244, panel.yMax - 52, 112, 34), "打开输出目录", ZMBuildStyles.CompactSecondaryButton))
                OpenBuildOutput(ZMBuildProgress.OutputPath);
            if (GUI.Button(new Rect(closeX, panel.yMax - 52, 94, 34), "关闭", failed ? ZMBuildStyles.BuildFailureButton : ZMBuildStyles.CompactPrimaryButton))
                ZMBuildProgress.Dismiss();
        }
    }

    private void UpdateDisplayedProgress()
    {
        double now = EditorApplication.timeSinceStartup;
        if (displayedProgressRunId != ZMBuildProgress.RunId)
        {
            displayedProgressRunId = ZMBuildProgress.RunId;
            displayedOverallProgress = 0f;
            displayedStageProgress = 0f;
            overallProgressVelocity = 0f;
            stageProgressVelocity = 0f;
            lastProgressDrawTime = now;
        }

        float delta = Mathf.Clamp((float)(now - lastProgressDrawTime), 0f, .05f);
        lastProgressDrawTime = now;
        const float smoothTime = .09f;
        displayedOverallProgress = Mathf.SmoothDamp(displayedOverallProgress, ZMBuildProgress.OverallProgress,
            ref overallProgressVelocity, smoothTime, Mathf.Infinity, Mathf.Max(.001f, delta));
        displayedStageProgress = Mathf.SmoothDamp(displayedStageProgress, ZMBuildProgress.StageProgress,
            ref stageProgressVelocity, smoothTime, Mathf.Infinity, Mathf.Max(.001f, delta));
        if (Mathf.Abs(displayedOverallProgress - ZMBuildProgress.OverallProgress) < .001f)
            displayedOverallProgress = ZMBuildProgress.OverallProgress;
        if (Mathf.Abs(displayedStageProgress - ZMBuildProgress.StageProgress) < .001f)
            displayedStageProgress = ZMBuildProgress.StageProgress;
        if (!Mathf.Approximately(displayedOverallProgress, ZMBuildProgress.OverallProgress) ||
            !Mathf.Approximately(displayedStageProgress, ZMBuildProgress.StageProgress) ||
            ZMBuildProgress.State == ZMBuildProgress.BuildState.Running)
            Repaint();
    }

    private static void DrawProgressBar(Rect rect, float value, bool failed)
    {
        GUI.Box(rect, GUIContent.none, ZMBuildStyles.BuildProgressTrack);
        float width = Mathf.Max(0, (rect.width - 4) * Mathf.Clamp01(value));
        if (width > 1f)
            GUI.Box(new Rect(rect.x + 2, rect.y + 2, Mathf.Max(8, width), rect.height - 4), GUIContent.none,
                failed ? ZMBuildStyles.BuildFailureFill : ZMBuildStyles.BuildProgressFill);
    }

    private static string BuildStateText()
    {
        switch (ZMBuildProgress.State)
        {
            case ZMBuildProgress.BuildState.Running: return "正在构建 · 请勿关闭 Unity";
            case ZMBuildProgress.BuildState.Succeeded: return "构建成功 · 输出内容已准备完成";
            case ZMBuildProgress.BuildState.Cancelled: return "构建已取消 · 已完成的模块不会回滚";
            case ZMBuildProgress.BuildState.Failed: return "构建失败 · 请查看下方信息与 Console";
            default: return string.Empty;
        }
    }

    private static string FormatElapsed(double seconds) => seconds < 60 ? $"{seconds:F1} 秒" : $"{(int)(seconds / 60)} 分 {seconds % 60:F0} 秒";

    private static void OpenBuildOutput(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenModuleDrawer(string moduleName)
    {
        moduleDrawer.Open(moduleName);
        GUI.FocusControl(null);
        Repaint();
    }

    private void DrawModuleDrawer()
    {
        moduleDrawer.UpdateAnimation();
        float progress = moduleDrawer.Animation;
        EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(0, 0, 0, .48f * progress));
        float width = Mathf.Clamp(position.width * .42f, 500f, 570f);
        float openX = position.width - width - 14;
        float closedX = position.width + 14;
        Rect drawerRect = new Rect(Mathf.Lerp(closedX, openX, progress), 78, width, position.height - 92);
        Event current = Event.current;
        if (current.type == EventType.MouseDown && current.button == 0 && !drawerRect.Contains(current.mousePosition))
        {
            moduleDrawer.Close();
            current.Use();
        }
        moduleDrawer.Draw(drawerRect, Refresh);
        if (progress > .001f && progress < .999f) Repaint();
    }

    private void DrawHeader(Rect rect)
    {
        EditorGUI.DrawRect(rect, ZMBuildStyles.Header);
        EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), ZMBuildStyles.Border);

        Texture cube = EditorGUIUtility.IconContent("Prefab Icon").image;
        if (cube != null) GUI.DrawTexture(new Rect(22, 20, 27, 27), cube, ScaleMode.ScaleToFit);
        GUI.Label(new Rect(58, 17, 320, 34), "ZMAsset 构建中心", ZMBuildStyles.Title);

        if (currentPage == Page.AssetBundle || currentPage == Page.HotPatch)
        {
            int selected = currentPage == Page.AssetBundle ? buildBundleWindow.SelectedCount : buildHotWindow.SelectedCount;
            string platform = EditorUserBuildSettings.activeBuildTarget.ToString();
            string compression = BundleSettings.Instance == null ? "LZ4" : CompressionName(BundleSettings.Instance.buildbundleOptions);
            float x = rect.xMax - 390;
            DrawBadge(new Rect(x, 15, 130, 36), platform, PlatformIcon());
            DrawBadge(new Rect(x + 140, 15, 82, 36), compression, null);
            DrawBadge(new Rect(x + 232, 15, 142, 36), $"{selected} 个模块已选择", null);
        }
    }

    private static string CompressionName(BuildAssetBundleOptions option)
    {
        if ((option & BuildAssetBundleOptions.ChunkBasedCompression) != 0) return "LZ4";
        if ((option & BuildAssetBundleOptions.UncompressedAssetBundle) != 0) return "无压缩";
        return "LZMA";
    }

    private static Texture PlatformIcon()
    {
        string name = EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.iOS
            ? "BuildSettings.iPhone.Small" : "BuildSettings.Android.Small";
        return EditorGUIUtility.IconContent(name).image;
    }

    private static void DrawBadge(Rect rect, string text, Texture icon)
    {
        GUI.Box(rect, GUIContent.none, ZMBuildStyles.BadgeBox);
        Rect labelRect = rect;
        if (icon != null)
        {
            GUI.DrawTexture(new Rect(rect.x + 10, rect.y + 8, 20, 20), icon, ScaleMode.ScaleToFit);
            labelRect.x += 29;
            labelRect.width -= 29;
        }
        GUI.Label(labelRect, text, ZMBuildStyles.Badge);
    }

    private void DrawSidebar(Rect rect)
    {
        EditorGUI.DrawRect(rect, ZMBuildStyles.Sidebar);
        EditorGUI.DrawRect(new Rect(rect.xMax - 1, rect.y, 1, rect.height), ZMBuildStyles.Border);
        float y = rect.y + 22;
        DrawNavigationItem(new Rect(0, y, rect.width, 52), Page.AssetBundle, "资源构建");
        y += 62;
        DrawNavigationItem(new Rect(0, y, rect.width, 52), Page.HotPatch, "热更补丁");
        y += 62;
        DrawNavigationItem(new Rect(0, y, rect.width, 52), Page.Settings, "Bundle 设置");
        y += 62;
        DrawNavigationItem(new Rect(0, y, rect.width, 52), Page.Manual, "使用手册");
    }

    private void DrawNavigationItem(Rect rect, Page page, string text)
    {
        bool selected = currentPage == page;
        bool hovered = rect.Contains(Event.current.mousePosition);
        float selection = ZMEditorAnimation.Tween($"ZMAsset.Nav.Selection.{page}", selected ? 1f : 0f, .18f);
        float hover = ZMEditorAnimation.Tween($"ZMAsset.Nav.Hover.{page}", hovered && !selected ? 1f : 0f, .12f);
        if (hover > .001f)
            EditorGUI.DrawRect(rect, new Color(ZMBuildStyles.SelectedNavigation.r, ZMBuildStyles.SelectedNavigation.g, ZMBuildStyles.SelectedNavigation.b, .36f * hover));
        if (selection > .001f)
        {
            EditorGUI.DrawRect(rect, new Color(ZMBuildStyles.SelectedNavigation.r, ZMBuildStyles.SelectedNavigation.g, ZMBuildStyles.SelectedNavigation.b, selection));
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, Mathf.Lerp(1f, 3f, selection), rect.height), new Color(ZMBuildStyles.Accent.r, ZMBuildStyles.Accent.g, ZMBuildStyles.Accent.b, selection));
        }
        DrawNavigationIcon(new Rect(rect.x + 23, rect.y + 14, 24, 24), page,
            selected ? new Color32(75, 171, 255, 255) : new Color32(165, 170, 179, 255));
        GUI.Label(new Rect(rect.x + 58, rect.y, rect.width - 64, rect.height), text,
            selected ? ZMBuildStyles.NavigationSelected : ZMBuildStyles.Navigation);
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            currentPage = page;
            ZMEditorAnimation.Restart("ZMAsset.Page", .2f);
            GUI.FocusControl(null);
            Repaint();
        }
    }

    private static void DrawNavigationIcon(Rect rect, Page page, Color color)
    {
        Handles.BeginGUI();
        Color old = Handles.color;
        Matrix4x4 oldMatrix = Handles.matrix;
        Handles.matrix = Matrix4x4.identity;
        Handles.color = color;
        if (page == Page.AssetBundle)
        {
            Vector3[] top = { new(rect.center.x, rect.y), new(rect.xMax - 2, rect.y + 6), new(rect.center.x, rect.y + 12), new(rect.x + 2, rect.y + 6), new(rect.center.x, rect.y) };
            Handles.DrawAAPolyLine(2f, top);
            Handles.DrawAAPolyLine(2f, new Vector3(rect.x + 2, rect.y + 11), new Vector3(rect.center.x, rect.y + 17), new Vector3(rect.xMax - 2, rect.y + 11));
            Handles.DrawAAPolyLine(2f, new Vector3(rect.x + 2, rect.y + 17), new Vector3(rect.center.x, rect.y + 23), new Vector3(rect.xMax - 2, rect.y + 17));
        }
        else if (page == Page.HotPatch)
        {
            Rect doc = new(rect.x + 3, rect.y + 1, 16, 21);
            Handles.DrawAAPolyLine(2f, new Vector3(doc.x, doc.y), new Vector3(doc.x + 11, doc.y), new Vector3(doc.xMax, doc.y + 5), new Vector3(doc.xMax, doc.yMax), new Vector3(doc.x, doc.yMax), new Vector3(doc.x, doc.y));
            Handles.DrawAAPolyLine(2f, new Vector3(rect.x + 14, rect.y + 13), new Vector3(rect.x + 10, rect.y + 19), new Vector3(rect.x + 15, rect.y + 19), new Vector3(rect.x + 12, rect.y + 24));
        }
        else if (page == Page.Settings)
        {
            ZMBuildStyles.DrawGuiCircle(rect.center, 7f, 2f);
            ZMBuildStyles.DrawGuiCircle(rect.center, 2.5f, 2f, 16);
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI / 4f;
                Vector3 direction = new(Mathf.Cos(angle), Mathf.Sin(angle));
                Handles.DrawAAPolyLine(2f, (Vector3)rect.center + direction * 8f, (Vector3)rect.center + direction * 11f);
            }
        }
        else
        {
            // 书本图标使用左右双页轮廓，保持与其他导航图标相同的线宽和光学尺寸。
            Vector3 centerTop = new(rect.center.x, rect.y + 4);
            Vector3 centerBottom = new(rect.center.x, rect.yMax - 2);
            Handles.DrawAAPolyLine(2f,
                centerTop, new Vector3(rect.x + 2, rect.y + 1),
                new Vector3(rect.x + 2, rect.yMax - 5),
                centerBottom, centerTop);
            Handles.DrawAAPolyLine(2f,
                centerTop, new Vector3(rect.xMax - 2, rect.y + 1),
                new Vector3(rect.xMax - 2, rect.yMax - 5),
                centerBottom, centerTop);
            Handles.DrawAAPolyLine(1.4f,
                new Vector3(rect.x + 6, rect.y + 7), new Vector3(rect.center.x - 3, rect.y + 9));
            Handles.DrawAAPolyLine(1.4f,
                new Vector3(rect.xMax - 6, rect.y + 7), new Vector3(rect.center.x + 3, rect.y + 9));
        }
        Handles.matrix = oldMatrix;
        Handles.color = old;
        Handles.EndGUI();
    }

    private void DrawSettings()
    {
        GUILayout.Space(28);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(34);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
            {
                GUILayout.Label("Bundle 设置", ZMBuildStyles.Heading);
                GUILayout.Label("管理资源加载、热更、加密与构建平台", ZMBuildStyles.Subtitle);
                GUILayout.Space(18);
                BundleSettings settings = BundleSettings.Instance;
                if (settings == null)
                    EditorGUILayout.HelpBox("未找到 Resources/AssetsBundleSettings.asset。", MessageType.Error);
                else
                {
                    using (var scroll = new EditorGUILayout.ScrollViewScope(settingsScroll))
                    {
                        settingsScroll = scroll.scrollPosition;
                        EditorGUI.BeginChangeCheck();
                        Undo.RecordObject(settings, "修改 Bundle 设置");

                        DrawSettingsSection("远程资源", "配置资源服务器地址与文件命名规则", () =>
                        {
                            settings.AssetBundleDownLoadUrl = DrawSettingsTextField("下载地址", settings.AssetBundleDownLoadUrl, "例如：https://cdn.example.com/assets/");
                            settings.ABSUFFIX = DrawSettingsTextField("Bundle 后缀", settings.ABSUFFIX, "建议留空；如需设置可填写 .ab");
                        });

                        DrawSettingsSection("构建策略", "控制输出平台与 AssetBundle 压缩方式", () =>
                        {
                            settings.buildTarget = (BuildTarget)DrawSettingsPopup("目标平台", (int)settings.buildTarget,
                                new[] { "自动识别", "iPhone（旧）", "macOS", "macOS Universal", "iOS", "Android", "Linux", "Windows 64 位" },
                                new[] { -2, -1, 2, 3, 9, 13, 17, 19 });
                            settings.buildbundleOptions = (BuildAssetBundleOptions)DrawSettingsPopup("压缩格式", (int)settings.buildbundleOptions,
                                new[] { "LZMA（体积优先）", "不压缩（速度优先）", "LZ4（推荐）" }, new[] { 0, 1, 256 });
                        });

                        DrawSettingsSection("运行时", "决定资源加载来源、更新方式与下载并发", () =>
                        {
                            settings.loadAssetType = (LoadAssetEnum)DrawSettingsPopup("加载模式", (int)settings.loadAssetType, System.Enum.GetNames(typeof(LoadAssetEnum)));
                            settings.bundleHotType = (BundleHotEnum)DrawSettingsPopup("热更模式", (int)settings.bundleHotType, System.Enum.GetNames(typeof(BundleHotEnum)));
                            settings.MAX_THREAD_COUNT = Mathf.Max(1, DrawSettingsIntField("下载线程", settings.MAX_THREAD_COUNT, "建议 3–8，移动网络下不宜过高"));
                        });

                        DrawSettingsSection("安全与路径", "配置 Bundle 加密和框架根目录", () =>
                        {
                            settings.bundleEncrypt ??= new BundleEncryptToggle();
                            settings.bundleEncrypt.isEncrypt = DrawSettingsSwitch("资源加密", settings.bundleEncrypt.isEncrypt,
                                settings.bundleEncrypt.isEncrypt ? "已启用，加密密钥将参与资源构建" : "未启用，Bundle 将以原始内容输出");
                            if (settings.bundleEncrypt.isEncrypt)
                                settings.bundleEncrypt.encryptKey = DrawPasswordField("加密密钥", settings.bundleEncrypt.encryptKey);
                            settings.ZMAssetRootPath = DrawSettingsTextField("框架根路径", settings.ZMAssetRootPath, "基于 Assets 目录，例如 ThirdParty/ZMAsset");
                        });

                        if (EditorGUI.EndChangeCheck())
                        {
                            EditorUtility.SetDirty(settings);
                            AssetDatabase.SaveAssetIfDirty(settings);
                        }
                        GUILayout.Space(18);
                    }
                }
            }
            GUILayout.Space(34);
        }
    }

    private static void DrawSettingsSection(string title, string description, System.Action content)
    {
        using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
        {
            GUILayout.Space(5);
            GUILayout.Label(title, ZMBuildStyles.SettingsSectionTitle);
            GUILayout.Label(description, ZMBuildStyles.SettingsHint);
            GUILayout.Space(12);
            content();
            GUILayout.Space(5);
        }
        GUILayout.Space(12);
    }

    private static string DrawSettingsTextField(string label, string value, string hint, bool password = false)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(52)))
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(150)))
            {
                GUILayout.Space(3);
                GUILayout.Label(label, ZMBuildStyles.SettingsLabel);
                GUILayout.Label(hint, ZMBuildStyles.SettingsFieldHint);
            }
            GUILayout.Space(12);
            Rect rect = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
            return ZMBuildStyles.DrawTextField(rect, value, ZMBuildStyles.InputField, password);
        }
    }

    private static int DrawSettingsIntField(string label, int value, string hint)
    {
        string text = DrawSettingsTextField(label, value.ToString(), hint);
        return int.TryParse(text, out int parsed) ? parsed : value;
    }

    private string DrawPasswordField(string label, string value)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(52)))
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(150)))
            {
                GUILayout.Space(3);
                GUILayout.Label(label, ZMBuildStyles.SettingsLabel);
                GUILayout.Label(string.IsNullOrEmpty(value) ? "尚未配置" : $"已配置 · {value.Length} 字符", ZMBuildStyles.SettingsFieldHint);
            }
            GUILayout.Space(12);
            Rect rect = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, ZMBuildStyles.FieldBox);
            Rect inputRect = new(rect.x, rect.y, rect.width - 88, rect.height);
            value = showEncryptKey
                ? GUI.TextField(inputRect, value ?? string.Empty, ZMBuildStyles.InputField)
                : GUI.PasswordField(inputRect, value ?? string.Empty, '\u2022', 256, ZMBuildStyles.InputField);

            Rect revealRect = new(rect.xMax - 87, rect.y + 1, 52, rect.height - 2);
            Rect copyRect = new(rect.xMax - 35, rect.y + 1, 34, rect.height - 2);
            EditorGUI.DrawRect(new Rect(revealRect.x, revealRect.y, 1, revealRect.height), ZMBuildStyles.Border);
            EditorGUI.DrawRect(new Rect(copyRect.x, copyRect.y, 1, copyRect.height), ZMBuildStyles.Border);
            if (GUI.Button(revealRect, showEncryptKey ? "隐藏" : "显示", ZMBuildStyles.LinkButton)) showEncryptKey = !showEncryptKey;
            GUIContent copyContent = EditorGUIUtility.IconContent("Clipboard");
            copyContent.tooltip = "复制密钥";
            if (GUI.Button(copyRect, copyContent, ZMBuildStyles.IconButton) && !string.IsNullOrEmpty(value))
                EditorGUIUtility.systemCopyBuffer = value;
            return value;
        }
    }

    private int DrawSettingsPopup(string label, int value, string[] labels, int[] values = null)
    {
        if (popupSelections.TryGetValue(label, out int pendingSelection))
        {
            popupSelections.Remove(label);
            value = values == null ? pendingSelection : values[Mathf.Clamp(pendingSelection, 0, values.Length - 1)];
        }

        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(48)))
        {
            GUILayout.Label(label, ZMBuildStyles.SettingsLabel, GUILayout.Width(150));
            GUILayout.Space(12);
            Rect rect = GUILayoutUtility.GetRect(100, 34, GUILayout.ExpandWidth(true));
            GUI.Box(rect, GUIContent.none, ZMBuildStyles.FieldBox);
            int selected = values == null ? Mathf.Clamp(value, 0, labels.Length - 1) : Mathf.Max(0, System.Array.IndexOf(values, value));
            if (GUI.Button(rect, labels[Mathf.Clamp(selected, 0, labels.Length - 1)], ZMBuildStyles.SettingsPopup))
            {
                Rect screenRect = GUIUtility.GUIToScreenRect(rect);
                DarkDropdownWindow.Show(screenRect, labels, selected, index =>
                {
                    popupSelections[label] = index;
                    Repaint();
                });
            }
            DrawPopupArrow(rect);
            return value;
        }
    }

    private static void DrawPopupArrow(Rect rect)
    {
        Handles.BeginGUI();
        Color old = Handles.color;
        Handles.color = new Color32(158, 164, 174, 255);
        float x = rect.xMax - 17;
        float y = rect.center.y - 2;
        Handles.DrawAAPolyLine(1.7f, new Vector3(x - 4, y), new Vector3(x, y + 4), new Vector3(x + 4, y));
        Handles.color = old;
        Handles.EndGUI();
    }

    private static bool DrawSettingsSwitch(string label, bool value, string hint)
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(52)))
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(150)))
            {
                GUILayout.Space(3);
                GUILayout.Label(label, ZMBuildStyles.SettingsLabel);
                GUILayout.Label(hint, ZMBuildStyles.SettingsFieldHint);
            }
            GUILayout.FlexibleSpace();
            Rect track = GUILayoutUtility.GetRect(46, 24, GUILayout.Width(46), GUILayout.Height(24));
            GUI.Box(track, GUIContent.none, value ? ZMBuildStyles.SwitchOn : ZMBuildStyles.SwitchOff);
            Rect knob = new Rect(value ? track.xMax - 20 : track.x + 4, track.y + 4, 16, 16);
            GUI.Box(knob, GUIContent.none, ZMBuildStyles.SwitchKnob);
            if (GUI.Button(track, GUIContent.none, GUIStyle.none)) value = !value;
        }
        return value;
    }
}

internal class DarkDropdownWindow : EditorWindow
{
    private string[] options;
    private int selected;
    private int keyboardIndex;
    private System.Action<int> onSelected;
    private Vector2 scroll;
    private bool showCheck;

    internal static void Show(Rect anchor, string[] options, int selected, System.Action<int> onSelected, bool showCheck = true)
    {
        if (options == null || options.Length == 0) return;
        var window = CreateInstance<DarkDropdownWindow>();
        window.options = options;
        window.selected = Mathf.Clamp(selected, 0, options.Length - 1);
        window.keyboardIndex = window.selected;
        window.onSelected = onSelected;
        window.showCheck = showCheck;
        float height = Mathf.Min(options.Length * 34f + 12f, 250f);
        window.position = new Rect(anchor.x, anchor.yMax + 3, anchor.width, height);
        window.ShowPopup();
        window.Focus();
    }

    private void OnLostFocus() => Close();

    private void OnGUI()
    {
        ZMBuildStyles.Ensure();
        GUI.Box(new Rect(0, 0, position.width, position.height), GUIContent.none, ZMBuildStyles.DropdownPanel);
        Rect viewport = new(6, 6, position.width - 12, position.height - 12);
        float contentHeight = options.Length * 34f;
        scroll = GUI.BeginScrollView(viewport, scroll, new Rect(0, 0, viewport.width - (contentHeight > viewport.height ? 14 : 0), contentHeight), false, contentHeight > viewport.height);
        for (int i = 0; i < options.Length; i++)
        {
            Rect item = new(0, i * 34f, viewport.width - (contentHeight > viewport.height ? 16 : 0), 30);
            GUIStyle style = i == keyboardIndex ? ZMBuildStyles.DropdownItemSelected : ZMBuildStyles.DropdownItem;
            if (GUI.Button(item, options[i], style)) Select(i);
            if (showCheck && i == selected)
                GUI.Label(new Rect(item.xMax - 25, item.y, 20, item.height), "✓", ZMBuildStyles.DropdownCheck);
        }
        GUI.EndScrollView();
        HandleKeyboard();
    }

    private void HandleKeyboard()
    {
        Event current = Event.current;
        if (current.type != EventType.KeyDown) return;
        if (current.keyCode == KeyCode.Escape) { Close(); current.Use(); }
        else if (current.keyCode == KeyCode.UpArrow) { keyboardIndex = Mathf.Max(0, keyboardIndex - 1); EnsureVisible(); current.Use(); Repaint(); }
        else if (current.keyCode == KeyCode.DownArrow) { keyboardIndex = Mathf.Min(options.Length - 1, keyboardIndex + 1); EnsureVisible(); current.Use(); Repaint(); }
        else if (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter) { Select(keyboardIndex); current.Use(); }
    }

    private void EnsureVisible()
    {
        float top = keyboardIndex * 34f;
        float bottom = top + 34f;
        if (top < scroll.y) scroll.y = top;
        else if (bottom > scroll.y + position.height - 12) scroll.y = bottom - position.height + 12;
    }

    private void Select(int index)
    {
        onSelected?.Invoke(index);
        Close();
    }
}

internal static class ZMBuildStyles
{
    internal static readonly Color Window = new Color32(28, 30, 34, 255);
    internal static readonly Color Header = new Color32(24, 26, 30, 255);
    internal static readonly Color Sidebar = new Color32(27, 29, 33, 255);
    internal static readonly Color Panel = new Color32(35, 38, 43, 255);
    internal static readonly Color Card = new Color32(36, 39, 44, 255);
    internal static readonly Color CardSelected = new Color32(31, 42, 51, 255);
    internal static readonly Color Border = new Color32(58, 62, 70, 255);
    internal static readonly Color Accent = new Color32(56, 168, 255, 255);
    internal static readonly Color Muted = new Color32(145, 150, 160, 255);
    internal static readonly Color SelectedNavigation = new Color32(31, 44, 57, 255);

    internal static GUIStyle Title, Heading, Subtitle, Navigation, NavigationSelected, Badge;
    internal static GUIStyle CardTitle, CardMeta, Search, InputField, TextArea, FlatField, FieldBox, BadgeBox, CardBox, CardSelectedBox, AddCardBox, PrimaryButton, SecondaryButton, ToggleOn, ToggleOff, StatusLabel, SettingsPanel;
    internal static GUIStyle SettingsCard, SettingsSectionTitle, SettingsHint, SettingsLabel, SettingsFieldHint, SettingsPopup, SwitchOn, SwitchOff, SwitchKnob, LinkButton, IconButton, CloseButton, CompactPrimaryButton, CompactSecondaryButton, PopupWindowBox, PopupHeaderBox, DropdownPanel, DropdownItem, DropdownItemSelected, DropdownCheck, CardEditButton;
    internal static GUIStyle DrawerPanel, Segment, SegmentSelected, PathRow, PathIconButton, PathDeleteButton, AddPathBox, AddPathLabel, DrawerPlaceholder, RuleHelpButton, RuleHelpPanel, RuleHelpTitle, RuleHelpTitleSelected, RuleHelpDescription, RuleMarker, RuleMarkerSelected;
    internal static GUIStyle BuildProgressPanel, BuildProgressTrack, BuildProgressFill, BuildProgressState, BuildProgressRightLabel, BuildLogPanel, BuildLogText;
    internal static GUIStyle BuildFailurePanel, BuildFailureTitle, BuildFailureSubtitle, BuildFailureLabel, BuildFailureDetail, BuildFailureFill, BuildFailureButton;
    private static Texture2D primaryTexture, primaryHoverTexture, secondaryTexture, secondaryHoverTexture;

    internal static void Ensure()
    {
        if (Title != null) return;
        Title = Label(20, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
        Heading = Label(25, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
        Subtitle = Label(14, FontStyle.Normal, Muted, TextAnchor.MiddleLeft);
        Navigation = Label(14, FontStyle.Normal, new Color32(176, 180, 187, 255), TextAnchor.MiddleLeft);
        NavigationSelected = Label(14, FontStyle.Bold, new Color32(89, 179, 255, 255), TextAnchor.MiddleLeft);
        Badge = Label(12, FontStyle.Normal, new Color32(205, 210, 218, 255), TextAnchor.MiddleCenter);
        CardTitle = Label(18, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
        CardMeta = Label(12, FontStyle.Normal, new Color32(166, 171, 181, 255), TextAnchor.MiddleLeft);
        StatusLabel = Label(13, FontStyle.Normal, new Color32(185, 190, 198, 255), TextAnchor.MiddleLeft);

        Texture2D fieldNormal = RoundedTexture(24, 6, new Color32(20, 22, 26, 255), Border, 1);
        Texture2D searchFocused = RoundedTexture(24, 6, new Color32(20, 22, 26, 255), new Color32(68, 132, 184, 255), 1);
        Search = TransparentFieldStyle(13, new RectOffset(31, 10, 0, 0));
        InputField = new GUIStyle(Search) { padding = new RectOffset(11, 11, 0, 0), alignment = TextAnchor.MiddleLeft };
        TextArea = new GUIStyle(InputField) { wordWrap = true, alignment = TextAnchor.UpperLeft, padding = new RectOffset(11, 11, 8, 8) };
        FlatField = new GUIStyle(EditorStyles.label) { fontSize = 13, padding = new RectOffset(12, 38, 0, 0), alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color32(185, 190, 198, 255) } };
        FieldBox = BoxStyle(fieldNormal, 7);
        BadgeBox = BoxStyle(RoundedTexture(24, 7, Panel, Border, 1), 8);
        CardBox = BoxStyle(RoundedTexture(24, 7, Card, Border, 1), 8);
        CardSelectedBox = BoxStyle(RoundedTexture(24, 7, CardSelected, Accent, 2), 8);
        AddCardBox = BoxStyle(RoundedTexture(24, 7, Window, new Color32(110, 114, 123, 255), 1), 8);
        primaryTexture = RoundedTexture(24, 6, new Color32(40, 137, 245, 255), new Color32(40, 137, 245, 255), 0);
        primaryHoverTexture = RoundedTexture(24, 6, new Color32(52, 150, 255, 255), new Color32(52, 150, 255, 255), 0);
        secondaryTexture = RoundedTexture(24, 6, new Color32(53, 56, 63, 255), new Color32(70, 74, 82, 255), 1);
        secondaryHoverTexture = RoundedTexture(24, 6, new Color32(63, 67, 75, 255), new Color32(82, 87, 96, 255), 1);
        PrimaryButton = Button(primaryTexture, primaryHoverTexture, Color.white, 15, FontStyle.Bold);
        SecondaryButton = Button(secondaryTexture, secondaryHoverTexture, new Color32(225, 228, 233, 255), 14, FontStyle.Normal);
        ToggleOn = ToggleStyle(RoundedTexture(20, 4, Accent, Accent, 0));
        ToggleOff = ToggleStyle(RoundedTexture(20, 4, Card, new Color32(125, 130, 139, 255), 1));
        SettingsPanel = new GUIStyle(EditorStyles.helpBox) { padding = new RectOffset(18, 18, 14, 14) };
        SettingsCard = new GUIStyle(CardBox) { padding = new RectOffset(22, 22, 16, 16) };
        SettingsSectionTitle = Label(16, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
        SettingsHint = Label(12, FontStyle.Normal, Muted, TextAnchor.MiddleLeft);
        SettingsLabel = Label(13, FontStyle.Bold, new Color32(220, 224, 230, 255), TextAnchor.MiddleLeft);
        SettingsFieldHint = Label(10, FontStyle.Normal, new Color32(124, 130, 140, 255), TextAnchor.MiddleLeft);
        SettingsFieldHint.wordWrap = false;
        SettingsPopup = new GUIStyle(GUIStyle.none)
        {
            fontSize = 13, fixedHeight = 34, alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(11, 30, 0, 0), border = new RectOffset(0, 0, 0, 0),
            normal = { background = null, textColor = Color.white },
            focused = { background = null, textColor = Color.white },
            hover = { background = null, textColor = Color.white },
            active = { background = null, textColor = Color.white }
        };
        SwitchOn = BoxStyle(RoundedTexture(24, 12, Accent, Accent, 0), 12);
        SwitchOff = BoxStyle(RoundedTexture(24, 12, new Color32(64, 68, 76, 255), new Color32(86, 91, 101, 255), 1), 12);
        SwitchKnob = BoxStyle(RoundedTexture(32, 16, Color.white, new Color32(220, 224, 230, 255), 1), 0);
        LinkButton = new GUIStyle(GUIStyle.none) { fontSize = 11, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color32(103, 184, 255, 255) }, hover = { textColor = Color.white } };
        IconButton = new GUIStyle(GUIStyle.none) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(7, 7, 7, 7) };
        CloseButton = new GUIStyle(GUIStyle.none) { fontSize = 20, alignment = TextAnchor.MiddleCenter, normal = { textColor = Muted }, hover = { textColor = Color.white } };
        CompactPrimaryButton = new GUIStyle(PrimaryButton) { fixedHeight = 34, fontSize = 13 };
        CompactSecondaryButton = new GUIStyle(SecondaryButton) { fixedHeight = 34, fontSize = 13 };
        PopupWindowBox = BoxStyle(RoundedTexture(32, 10, Window, new Color32(82, 91, 103, 255), 2), 11);
        PopupHeaderBox = BoxStyle(RoundedTexture(28, 8, Header, Header, 0), 9);
        DropdownPanel = BoxStyle(RoundedTexture(28, 8, new Color32(31, 34, 39, 255), new Color32(76, 82, 92, 255), 1), 9);
        DropdownItem = Button(RoundedTexture(24, 6, new Color32(31, 34, 39, 255), Color.clear, 0), RoundedTexture(24, 6, new Color32(42, 48, 56, 255), Color.clear, 0), new Color32(215, 219, 225, 255), 13, FontStyle.Normal);
        DropdownItem.fixedHeight = 30;
        DropdownItem.alignment = TextAnchor.MiddleLeft;
        DropdownItem.padding = new RectOffset(12, 30, 0, 0);
        DropdownItemSelected = new GUIStyle(DropdownItem);
        DropdownItemSelected.normal.background = RoundedTexture(24, 6, new Color32(35, 66, 91, 255), Color.clear, 0);
        DropdownItemSelected.normal.textColor = Color.white;
        DropdownCheck = Label(14, FontStyle.Bold, new Color32(79, 176, 255, 255), TextAnchor.MiddleCenter);
        CardEditButton = Button(
            RoundedTexture(24, 6, new Color32(45, 49, 56, 255), new Color32(66, 72, 82, 255), 1),
            RoundedTexture(24, 6, new Color32(39, 92, 132, 255), new Color32(69, 157, 224, 255), 1),
            new Color32(205, 211, 219, 255), 11, FontStyle.Normal);
        CardEditButton.fixedHeight = 24;
        CardEditButton.padding = new RectOffset(17, 5, 0, 0);
        DrawerPanel = BoxStyle(RoundedTexture(32, 10, new Color32(29, 32, 37, 255), new Color32(82, 89, 100, 255), 1), 11);
        Segment = Button(RoundedTexture(24, 6, new Color32(31, 34, 39, 255), Border, 1), RoundedTexture(24, 6, new Color32(43, 48, 56, 255), new Color32(74, 81, 92, 255), 1), new Color32(180, 185, 194, 255), 11, FontStyle.Normal);
        Segment.fixedHeight = 34;
        SegmentSelected = Button(RoundedTexture(24, 6, new Color32(42, 137, 229, 255), new Color32(55, 155, 247, 255), 1), RoundedTexture(24, 6, new Color32(51, 149, 241, 255), Accent, 1), Color.white, 11, FontStyle.Bold);
        SegmentSelected.fixedHeight = 34;
        PathRow = new GUIStyle(BadgeBox) { padding = new RectOffset(10, 8, 8, 8) };
        PathIconButton = new GUIStyle(IconButton);
        PathIconButton.normal.background = RoundedTexture(24, 6, new Color32(45, 49, 56, 255), Border, 1);
        PathIconButton.hover.background = RoundedTexture(24, 6, new Color32(55, 62, 71, 255), new Color32(82, 91, 103, 255), 1);
        PathIconButton.border = new RectOffset(7, 7, 7, 7);
        PathDeleteButton = new GUIStyle(PathIconButton) { fontSize = 17, alignment = TextAnchor.MiddleCenter };
        PathDeleteButton.normal.textColor = new Color32(181, 186, 195, 255);
        PathDeleteButton.hover.textColor = new Color32(255, 118, 118, 255);
        AddPathBox = BoxStyle(RoundedTexture(28, 8, new Color32(31, 34, 39, 255), new Color32(112, 119, 130, 255), 1), 9);
        AddPathLabel = Label(12, FontStyle.Normal, new Color32(178, 184, 194, 255), TextAnchor.MiddleCenter);
        DrawerPlaceholder = Label(12, FontStyle.Normal, new Color32(132, 138, 148, 255), TextAnchor.MiddleLeft);
        RuleHelpButton = Button(
            RoundedTexture(24, 6, new Color32(43, 47, 54, 255), new Color32(70, 76, 86, 255), 1),
            RoundedTexture(24, 6, new Color32(39, 76, 103, 255), new Color32(66, 145, 202, 255), 1),
            new Color32(176, 205, 228, 255), 11, FontStyle.Normal);
        RuleHelpButton.fixedHeight = 26;
        RuleHelpPanel = new GUIStyle(BadgeBox) { padding = new RectOffset(12, 12, 9, 9) };
        RuleHelpTitle = Label(11, FontStyle.Bold, new Color32(191, 196, 204, 255), TextAnchor.MiddleLeft);
        RuleHelpTitleSelected = Label(11, FontStyle.Bold, new Color32(89, 179, 255, 255), TextAnchor.MiddleLeft);
        RuleHelpDescription = Label(10, FontStyle.Normal, new Color32(144, 150, 160, 255), TextAnchor.MiddleLeft);
        RuleHelpDescription.wordWrap = true;
        RuleMarker = BoxStyle(RoundedTexture(16, 8, new Color32(67, 72, 81, 255), Color.clear, 0), 0);
        RuleMarkerSelected = BoxStyle(RoundedTexture(16, 8, Accent, Color.clear, 0), 0);
        BuildProgressPanel = BoxStyle(RoundedTexture(32, 11, new Color32(29, 32, 37, 255), new Color32(87, 96, 109, 255), 1), 12);
        BuildProgressTrack = BoxStyle(RoundedTexture(16, 4, new Color32(18, 20, 24, 255), new Color32(57, 62, 70, 255), 1), 4);
        BuildProgressFill = BoxStyle(RoundedTexture(16, 4, new Color32(47, 149, 241, 255), new Color32(67, 174, 255, 255), 1), 4);
        BuildProgressState = Label(12, FontStyle.Normal, new Color32(116, 184, 235, 255), TextAnchor.MiddleLeft);
        BuildProgressRightLabel = Label(11, FontStyle.Normal, new Color32(148, 154, 164, 255), TextAnchor.MiddleRight);
        BuildLogPanel = BoxStyle(RoundedTexture(24, 7, new Color32(22, 24, 28, 255), new Color32(54, 59, 67, 255), 1), 8);
        BuildLogText = Label(10, FontStyle.Normal, new Color32(148, 154, 164, 255), TextAnchor.MiddleLeft);
        BuildFailurePanel = BoxStyle(RoundedTexture(24, 7, new Color32(49, 29, 32, 255), new Color32(151, 62, 68, 255), 1), 8);
        BuildFailureTitle = Label(19, FontStyle.Bold, new Color32(255, 112, 118, 255), TextAnchor.MiddleLeft);
        BuildFailureTitle.imagePosition = ImagePosition.ImageLeft;
        BuildFailureTitle.padding = new RectOffset(0, 0, 0, 0);
        BuildFailureTitle.contentOffset = new Vector2(0, 0);
        BuildFailureSubtitle = Label(11, FontStyle.Normal, new Color32(206, 155, 159, 255), TextAnchor.MiddleLeft);
        BuildFailureLabel = Label(12, FontStyle.Bold, new Color32(255, 169, 173, 255), TextAnchor.MiddleLeft);
        BuildFailureDetail = Label(11, FontStyle.Normal, new Color32(229, 199, 201, 255), TextAnchor.UpperLeft);
        BuildFailureDetail.wordWrap = true;
        BuildFailureDetail.clipping = TextClipping.Clip;
        BuildFailureFill = BoxStyle(RoundedTexture(16, 4, new Color32(214, 67, 74, 255), new Color32(241, 91, 98, 255), 1), 4);
        BuildFailureButton = Button(
            RoundedTexture(24, 6, new Color32(139, 47, 53, 255), new Color32(179, 67, 73, 255), 1),
            RoundedTexture(24, 6, new Color32(169, 55, 62, 255), new Color32(221, 87, 94, 255), 1),
            Color.white, 13, FontStyle.Bold);
        BuildFailureButton.fixedHeight = 34;
    }

    private static GUIStyle Label(int size, FontStyle font, Color color, TextAnchor alignment) => new GUIStyle(EditorStyles.label)
    { fontSize = size, fontStyle = font, normal = { textColor = color }, alignment = alignment };

    private static GUIStyle TransparentFieldStyle(int fontSize, RectOffset padding)
    {
        var style = new GUIStyle(GUIStyle.none)
        {
            fontSize = fontSize,
            padding = padding,
            border = new RectOffset(0, 0, 0, 0),
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip
        };
        style.normal.background = null;
        style.hover.background = null;
        style.active.background = null;
        style.focused.background = null;
        style.onNormal.background = null;
        style.onHover.background = null;
        style.onActive.background = null;
        style.onFocused.background = null;
        style.normal.textColor = Color.white;
        style.hover.textColor = Color.white;
        style.active.textColor = Color.white;
        style.focused.textColor = Color.white;
        style.onNormal.textColor = Color.white;
        style.onHover.textColor = Color.white;
        style.onActive.textColor = Color.white;
        style.onFocused.textColor = Color.white;
        return style;
    }

    internal static string DrawTextField(Rect rect, string value, GUIStyle style, bool password = false)
    {
        GUI.Box(rect, GUIContent.none, FieldBox);
        return password
            ? GUI.PasswordField(rect, value ?? string.Empty, '\u2022', 256, style)
            : GUI.TextField(rect, value ?? string.Empty, style);
    }

    internal static string DrawTextArea(Rect rect, string value, GUIStyle style)
    {
        GUI.Box(rect, GUIContent.none, FieldBox);
        return GUI.TextArea(rect, value ?? string.Empty, style);
    }

    internal static void DrawGuiCircle(Vector2 center, float radius, float thickness, int segments = 28)
    {
        segments = Mathf.Max(8, segments);
        Vector3[] points = new Vector3[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;
            points[i] = new Vector3(
                Mathf.Round(center.x + Mathf.Cos(angle) * radius) + .5f,
                Mathf.Round(center.y + Mathf.Sin(angle) * radius) + .5f,
                0f);
        }
        Handles.DrawAAPolyLine(thickness, points);
    }

    private static GUIStyle Button(Texture2D background, Texture2D hover, Color color, int size, FontStyle font) => new GUIStyle(GUIStyle.none)
    { fontSize = size, fontStyle = font, fixedHeight = 48, alignment = TextAnchor.MiddleCenter, border = new RectOffset(7, 7, 7, 7), normal = { background = background, textColor = color }, hover = { background = hover, textColor = Color.white }, active = { background = hover, textColor = Color.white } };

    private static GUIStyle ToggleStyle(Texture2D background) => new GUIStyle(GUIStyle.none)
    { border = new RectOffset(5, 5, 5, 5), normal = { background = background }, hover = { background = background } };

    private static GUIStyle BoxStyle(Texture2D background, int border) => new GUIStyle(GUIStyle.none)
    { border = new RectOffset(border, border, border, border), normal = { background = background } };

    private static Texture2D Texture(Color color)
    {
        var texture = new Texture2D(1, 1)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixel(0, 0, color); texture.Apply(); return texture;
    }

    private static Texture2D RoundedTexture(int size, float radius, Color fill, Color border, float borderWidth)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            alphaIsTransparency = true
        };
        const int samplesPerAxis = 4;
        const float inverseSamples = 1f / (samplesPerAxis * samplesPerAxis);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            Color accumulated = Color.clear;
            int coveredSamples = 0;
            for (int sy = 0; sy < samplesPerAxis; sy++)
            for (int sx = 0; sx < samplesPerAxis; sx++)
            {
                float sampleX = x + (sx + .5f) / samplesPerAxis;
                float sampleY = y + (sy + .5f) / samplesPerAxis;
                if (!InsideRoundedRect(sampleX, sampleY, size, radius, 0)) continue;

                bool insideFill = borderWidth <= 0 || InsideRoundedRect(sampleX, sampleY, size, radius, borderWidth);
                Color sampleColor = insideFill ? fill : border;
                accumulated.r += sampleColor.r;
                accumulated.g += sampleColor.g;
                accumulated.b += sampleColor.b;
                coveredSamples++;
            }

            if (coveredSamples == 0)
                texture.SetPixel(x, y, Color.clear);
            else
                texture.SetPixel(x, y, new Color(
                    accumulated.r / coveredSamples,
                    accumulated.g / coveredSamples,
                    accumulated.b / coveredSamples,
                    coveredSamples * inverseSamples));
        }
        texture.Apply();
        return texture;
    }

    private static bool InsideRoundedRect(float x, float y, float size, float radius, float inset)
    {
        float left = inset;
        float top = inset;
        float right = size - inset;
        float bottom = size - inset;
        if (x < left || x > right || y < top || y > bottom) return false;

        float innerRadius = Mathf.Max(0, radius - inset);
        if (innerRadius <= 0) return true;
        float closestX = Mathf.Clamp(x, left + innerRadius, right - innerRadius);
        float closestY = Mathf.Clamp(y, top + innerRadius, bottom - innerRadius);
        float dx = x - closestX;
        float dy = y - closestY;
        return dx * dx + dy * dy <= innerRadius * innerRadius;
    }

    internal static void DrawPanel(Rect rect, Color fill, Color border, float thickness)
    {
        EditorGUI.DrawRect(rect, border);
        EditorGUI.DrawRect(new Rect(rect.x + thickness, rect.y + thickness, rect.width - thickness * 2, rect.height - thickness * 2), fill);
    }

    internal static void DrawDashedBorder(Rect rect, Color color)
    {
        const float dash = 7f;
        const float gap = 5f;
        for (float x = rect.x; x < rect.xMax; x += dash + gap)
        {
            float width = Mathf.Min(dash, rect.xMax - x);
            EditorGUI.DrawRect(new Rect(x, rect.y, width, 1), color);
            EditorGUI.DrawRect(new Rect(x, rect.yMax - 1, width, 1), color);
        }
        for (float y = rect.y; y < rect.yMax; y += dash + gap)
        {
            float height = Mathf.Min(dash, rect.yMax - y);
            EditorGUI.DrawRect(new Rect(rect.x, y, 1, height), color);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1, y, 1, height), color);
        }
    }
}
