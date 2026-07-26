using System;
using UnityEditor;
using UnityEngine;

[Serializable]
internal class BundleModuleDrawer
{
    private enum RuleType { Prefab, RootFolder, SingleBundle, Source }
    private static readonly string[] Tabs = { "预制体包", "文件夹子包", "单个 Bundle", "源文件配置" };

    [SerializeField] private bool isOpen;
    [SerializeField] private string moduleName;
    [SerializeField] private string originalName;
    [SerializeField] private bool addressable;
    [SerializeField] private string[] prefabPaths = Array.Empty<string>();
    [SerializeField] private string[] rootPaths = Array.Empty<string>();
    [SerializeField] private BundleFileInfo[] bundlePaths = Array.Empty<BundleFileInfo>();
    [SerializeField] private string[] sourcePaths = Array.Empty<string>();
    [SerializeField] private int selectedTab;
    [SerializeField] private Vector2 scroll;
    [SerializeField] private bool showRuleHelp;
    [NonSerialized] private float animation;
    [NonSerialized] private double lastAnimationTime;
    [NonSerialized] private float ruleHelpAnimation;
    [NonSerialized] private double lastRuleHelpTime;

    internal bool IsOpen => isOpen || animation > .001f;
    internal float Animation => animation * animation * (3f - 2f * animation);

    internal void Open(string targetName)
    {
        originalName = targetName ?? string.Empty;
        BundleModuleData data = BuildBundleConfigura.Instance?.GetBundleDataByName(originalName);
        moduleName = data?.moduleName ?? string.Empty;
        addressable = data != null && data.isAddressableAsset;
        prefabPaths = Clone(data?.prefabPathArr);
        rootPaths = Clone(data?.rootFolderPathArr);
        sourcePaths = Clone(data?.sourceFolderPathArr);
        bundlePaths = Clone(data?.signFolderPathArr);
        selectedTab = 0;
        showRuleHelp = false;
        ruleHelpAnimation = 0f;
        scroll = Vector2.zero;
        isOpen = true;
        lastAnimationTime = EditorApplication.timeSinceStartup;
    }

    internal void UpdateAnimation()
    {
        double now = EditorApplication.timeSinceStartup;
        float delta = lastAnimationTime <= 0 ? 0f : (float)(now - lastAnimationTime);
        lastAnimationTime = now;
        animation = Mathf.MoveTowards(animation, isOpen ? 1f : 0f, delta / .22f);
        if (animation > .001f && animation < .999f) RepaintOwner();
    }

    internal void Draw(Rect rect, Action onSaved)
    {
        GUI.Box(rect, GUIContent.none, ZMBuildStyles.DrawerPanel);
        GUILayout.BeginArea(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4));
        DrawHeader();
        using (var view = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.ExpandHeight(true)))
        {
            scroll = view.scrollPosition;
            GUILayout.Space(8);
            DrawBasicCard();
            GUILayout.Space(12);
            DrawRulesCard();
            GUILayout.Space(10);
        }
        DrawFooter(onSaved);
        GUILayout.EndArea();
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(68)))
        {
            GUILayout.Space(20);
            using (new EditorGUILayout.VerticalScope())
            {
                GUILayout.Space(10);
                GUILayout.Label("资源模块配置", ZMBuildStyles.CardTitle, GUILayout.Height(26));
                GUILayout.Label("配置模块打包规则与资源路径", ZMBuildStyles.SettingsHint, GUILayout.Height(18));
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("×", ZMBuildStyles.CloseButton, GUILayout.Width(34), GUILayout.Height(34))) Close();
            GUILayout.Space(12);
        }
        DrawSeparator();
    }

    private void DrawBasicCard()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(18);
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                GUILayout.Label("基础信息", ZMBuildStyles.SettingsSectionTitle);
                GUILayout.Space(9);
                GUILayout.Label("模块名称", ZMBuildStyles.SettingsLabel);
                Rect field = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
                moduleName = ZMBuildStyles.DrawTextField(field, moduleName, ZMBuildStyles.InputField);
                GUILayout.Space(10);
                using (new EditorGUILayout.HorizontalScope(GUILayout.Height(28)))
                {
                    GUILayout.Label("可寻址资源", ZMBuildStyles.SettingsLabel);
                    GUILayout.FlexibleSpace();
                    addressable = DrawSwitch(addressable);
                }
                GUILayout.Label("开启后，模块资源将按 Addressable 方式进行打包与加载", ZMBuildStyles.SettingsFieldHint);
                GUILayout.Space(3);
            }
            GUILayout.Space(18);
        }
    }

    private void DrawRulesCard()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(18);
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.SettingsCard))
            {
                using (new EditorGUILayout.HorizontalScope(GUILayout.Height(28)))
                {
                    GUILayout.Label("打包规则", ZMBuildStyles.SettingsSectionTitle);
                    GUILayout.FlexibleSpace();
                    string helpButtonText = showRuleHelp ? "收起说明" : "?  规则说明";
                    if (GUILayout.Button(helpButtonText, ZMBuildStyles.RuleHelpButton, GUILayout.Width(82), GUILayout.Height(26)))
                    {
                        showRuleHelp = !showRuleHelp;
                        lastRuleHelpTime = EditorApplication.timeSinceStartup;
                        RepaintOwner();
                    }
                }
                GUILayout.Space(10);
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int i = 0; i < Tabs.Length; i++)
                    {
                        if (GUILayout.Button(Tabs[i], i == selectedTab ? ZMBuildStyles.SegmentSelected : ZMBuildStyles.Segment, GUILayout.Height(34))) selectedTab = i;
                        if (i < Tabs.Length - 1) GUILayout.Space(4);
                    }
                }
                DrawAnimatedRuleHelp();
                GUILayout.Space(13);
                switch ((RuleType)selectedTab)
                {
                    case RuleType.Prefab: DrawPathList(ref prefabPaths, "选择预制体资源文件夹"); break;
                    case RuleType.RootFolder: DrawPathList(ref rootPaths, "选择文件夹子包路径"); break;
                    case RuleType.SingleBundle: DrawBundleList(); break;
                    case RuleType.Source: DrawPathList(ref sourcePaths, "选择源文件路径"); break;
                }
                GUILayout.Space(3);
            }
            GUILayout.Space(18);
        }
    }

    private void DrawAnimatedRuleHelp()
    {
        double now = EditorApplication.timeSinceStartup;
        float delta = lastRuleHelpTime <= 0 ? 0f : (float)(now - lastRuleHelpTime);
        lastRuleHelpTime = now;
        ruleHelpAnimation = Mathf.MoveTowards(ruleHelpAnimation, showRuleHelp ? 1f : 0f, delta / .18f);
        if (ruleHelpAnimation <= .001f)
        {
            if (showRuleHelp) RepaintOwner();
            return;
        }

        float eased = ruleHelpAnimation * ruleHelpAnimation * (3f - 2f * ruleHelpAnimation);
        GUILayout.Space(10f * eased);
        const float fullHeight = 186f;
        Rect clip = GUILayoutUtility.GetRect(0, fullHeight * eased, GUILayout.ExpandWidth(true), GUILayout.Height(fullHeight * eased));
        GUI.BeginGroup(clip);
        Color oldColor = GUI.color;
        GUI.color = new Color(oldColor.r, oldColor.g, oldColor.b, oldColor.a * eased);
        DrawRuleHelp(new Rect(0, 0, clip.width, fullHeight));
        GUI.color = oldColor;
        GUI.EndGroup();
        if (ruleHelpAnimation < .999f) RepaintOwner();
    }

    private void DrawRuleHelp(Rect rect)
    {
        GUI.Box(rect, GUIContent.none, ZMBuildStyles.RuleHelpPanel);
        string[] titles = { "预制体包", "文件夹子包", "单个 Bundle", "源文件配置" };
        string[] descriptions =
        {
            "文件夹中的每个预制体分别生成一个独立 Bundle",
            "所选目录下的每个一级子文件夹分别生成一个 Bundle",
            "将指定文件夹整体打成一个 Bundle，并可自定义 Bundle 名称",
            "不打包为 Bundle，直接将原文件复制到资源输出目录，例如：mp3、mp4 等"
        };

        for (int index = 0; index < titles.Length; index++)
        {
            float y = rect.y + 9 + index * 42f;
            Rect marker = new Rect(rect.x + 12, y + 11, 6, 6);
            GUI.Box(marker, GUIContent.none, index == selectedTab ? ZMBuildStyles.RuleMarkerSelected : ZMBuildStyles.RuleMarker);
            GUI.Label(new Rect(rect.x + 27, y, 88, 28), titles[index], index == selectedTab ? ZMBuildStyles.RuleHelpTitleSelected : ZMBuildStyles.RuleHelpTitle);
            GUI.Label(new Rect(rect.x + 115, y, rect.width - 127, 34), descriptions[index], ZMBuildStyles.RuleHelpDescription);
        }
    }

    private static bool DrawSwitch(bool value)
    {
        Rect track = GUILayoutUtility.GetRect(46, 24, GUILayout.Width(46), GUILayout.Height(24));
        GUI.Box(track, GUIContent.none, value ? ZMBuildStyles.SwitchOn : ZMBuildStyles.SwitchOff);
        GUI.Box(new Rect(value ? track.xMax - 20 : track.x + 4, track.y + 4, 16, 16), GUIContent.none, ZMBuildStyles.SwitchKnob);
        if (GUI.Button(track, GUIContent.none, GUIStyle.none)) value = !value;
        return value;
    }

    private static void DrawPathList(ref string[] paths, string pickerTitle)
    {
        paths ??= Array.Empty<string>();
        for (int i = 0; i < paths.Length; i++)
        {
            int index = i;
            using (new EditorGUILayout.HorizontalScope(ZMBuildStyles.PathRow, GUILayout.Height(48)))
            {
                Rect field = GUILayoutUtility.GetRect(80, 32, GUILayout.ExpandWidth(true));
                paths[index] = GUI.TextField(field, paths[index] ?? string.Empty, ZMBuildStyles.InputField);
                if (string.IsNullOrEmpty(paths[index]))
                    GUI.Label(new Rect(field.x + 11, field.y, field.width - 18, field.height), "请选择资源路径…", ZMBuildStyles.DrawerPlaceholder);
                GUILayout.Space(6);
                if (GUILayout.Button(EditorGUIUtility.IconContent("Folder Icon"), ZMBuildStyles.PathIconButton, GUILayout.Width(34), GUILayout.Height(32)))
                {
                    string selected = EditorUtility.OpenFolderPanel(pickerTitle, ToAbsolute(paths[index]), string.Empty);
                    if (!string.IsNullOrEmpty(selected)) paths[index] = ToProject(selected);
                }
                GUILayout.Space(6);
                if (GUILayout.Button("×", ZMBuildStyles.PathDeleteButton, GUILayout.Width(34), GUILayout.Height(32)))
                {
                    ArrayUtility.RemoveAt(ref paths, index);
                    GUIUtility.ExitGUI();
                }
            }
            GUILayout.Space(7);
        }
        Rect add = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
        GUI.Box(add, GUIContent.none, ZMBuildStyles.AddPathBox);
        GUI.Label(add, "+  添加资源路径", ZMBuildStyles.AddPathLabel);
        if (GUI.Button(add, GUIContent.none, GUIStyle.none)) ArrayUtility.Add(ref paths, string.Empty);
    }

    private void DrawBundleList()
    {
        bundlePaths ??= Array.Empty<BundleFileInfo>();
        for (int i = 0; i < bundlePaths.Length; i++)
        {
            int index = i;
            bundlePaths[index] ??= new BundleFileInfo();
            using (new EditorGUILayout.VerticalScope(ZMBuildStyles.PathRow))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    Rect deleteRect = GUILayoutUtility.GetRect(42, EditorGUIUtility.singleLineHeight, GUILayout.Width(42));
                    deleteRect.y -= 4f;
                    if (GUI.Button(deleteRect, "删除", ZMBuildStyles.LinkButton))
                    {
                        ArrayUtility.RemoveAt(ref bundlePaths, index);
                        GUIUtility.ExitGUI();
                    }
                }
                Rect nameRect = GUILayoutUtility.GetRect(0, 32, GUILayout.ExpandWidth(true));
                bundlePaths[index].abName = ZMBuildStyles.DrawTextField(nameRect, bundlePaths[index].abName, ZMBuildStyles.InputField);
                GUILayout.Space(6);
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect pathRect = GUILayoutUtility.GetRect(80, 32, GUILayout.ExpandWidth(true));
                    bundlePaths[index].bundlePath = ZMBuildStyles.DrawTextField(pathRect, bundlePaths[index].bundlePath, ZMBuildStyles.InputField);
                    if (GUILayout.Button(EditorGUIUtility.IconContent("Folder Icon"), ZMBuildStyles.PathIconButton, GUILayout.Width(34), GUILayout.Height(32)))
                    {
                        string selected = EditorUtility.OpenFolderPanel("选择 Bundle 文件夹", ToAbsolute(bundlePaths[index].bundlePath), string.Empty);
                        if (!string.IsNullOrEmpty(selected)) bundlePaths[index].bundlePath = ToProject(selected);
                    }
                }
            }
            GUILayout.Space(7);
        }
        Rect add = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
        GUI.Box(add, GUIContent.none, ZMBuildStyles.AddPathBox);
        GUI.Label(add, "+  添加 Bundle", ZMBuildStyles.AddPathLabel);
        if (GUI.Button(add, GUIContent.none, GUIStyle.none)) ArrayUtility.Add(ref bundlePaths, new BundleFileInfo());
    }

    private void DrawFooter(Action onSaved)
    {
        DrawSeparator();
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(66)))
        {
            GUILayout.Space(18);
            GUILayout.Label($"共 {RuleCount()} 条构建规则", ZMBuildStyles.StatusLabel, GUILayout.Height(66));
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(88)))
            {
                GUILayout.Space(14);
                if (GUILayout.Button("取消", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(88), GUILayout.Height(34))) Close();
            }
            GUILayout.Space(9);
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(116)))
            {
                GUILayout.Space(14);
                if (GUILayout.Button("保存配置", ZMBuildStyles.CompactPrimaryButton, GUILayout.Width(116), GUILayout.Height(34))) Save(onSaved);
            }
            GUILayout.Space(18);
        }
    }

    private int RuleCount() => (prefabPaths?.Length ?? 0) + (rootPaths?.Length ?? 0) + (bundlePaths?.Length ?? 0) + (sourcePaths?.Length ?? 0);

    private static void DrawSeparator()
    {
        Rect line = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
        EditorGUI.DrawRect(line, ZMBuildStyles.Border);
    }

    private void Save(Action onSaved)
    {
        moduleName = moduleName?.Trim();
        if (string.IsNullOrEmpty(moduleName)) { EditorUtility.DisplayDialog("保存失败", "模块名称不能为空。", "确定"); return; }
        BuildBundleConfigura config = BuildBundleConfigura.Instance;
        BundleModuleData duplicate = config.GetBundleDataByName(moduleName);
        if (duplicate != null && moduleName != originalName) { EditorUtility.DisplayDialog("保存失败", "已存在同名模块。", "确定"); return; }
        Undo.RecordObject(config, "保存资源模块配置");
        BundleModuleData data = config.GetBundleDataByName(originalName) ?? new BundleModuleData();
        data.moduleName = moduleName;
        data.isAddressableAsset = addressable;
        data.prefabPathArr = RemoveEmptyPaths(prefabPaths);
        data.rootFolderPathArr = RemoveEmptyPaths(rootPaths);
        data.signFolderPathArr = Array.FindAll(bundlePaths ?? Array.Empty<BundleFileInfo>(),
            item => item != null && !string.IsNullOrWhiteSpace(item.bundlePath));
        data.sourceFolderPathArr = RemoveEmptyPaths(sourcePaths);
        config.SaveModuleData(data);
        Close();
        onSaved?.Invoke();
    }

    private static string[] RemoveEmptyPaths(string[] paths)
    {
        return Array.FindAll(paths ?? Array.Empty<string>(), path => !string.IsNullOrWhiteSpace(path));
    }

    internal void Close()
    {
        isOpen = false;
        lastAnimationTime = EditorApplication.timeSinceStartup;
        RepaintOwner();
    }

    private static void RepaintOwner()
    {
        if (EditorWindow.focusedWindow != null) EditorWindow.focusedWindow.Repaint();
    }

    private static string[] Clone(string[] source) => source == null ? Array.Empty<string>() : (string[])source.Clone();
    private static BundleFileInfo[] Clone(BundleFileInfo[] source)
    {
        if (source == null) return Array.Empty<BundleFileInfo>();
        BundleFileInfo[] result = new BundleFileInfo[source.Length];
        for (int i = 0; i < source.Length; i++) result[i] = source[i] == null ? new BundleFileInfo() : new BundleFileInfo { abName = source[i].abName, bundlePath = source[i].bundlePath };
        return result;
    }

    private static string ToAbsolute(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "Assets") return Application.dataPath;
        return path.StartsWith("Assets/") ? Application.dataPath + path.Substring("Assets".Length) : path;
    }

    private static string ToProject(string absolute)
    {
        absolute = absolute.Replace('\\', '/');
        string assets = Application.dataPath.Replace('\\', '/');
        return absolute.StartsWith(assets) ? "Assets" + absolute.Substring(assets.Length) : absolute;
    }
}
