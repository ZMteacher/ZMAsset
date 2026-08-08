using System;
using UnityEditor;
using UnityEngine;

[Serializable]
internal class BundleModuleDrawer
{
    private enum RuleType { Prefab, RootFolder, SingleBundle, SingleFile, Source }
    private static readonly string[] Tabs = { "预制体包", "文件夹子包", "文件夹包", "单文件包", "源文件配置" };

    [SerializeField] private bool isOpen;
    [SerializeField] private string moduleName;
    [SerializeField] private string originalName;
    [SerializeField] private bool addressable;
    [SerializeField] private BundleModuleRole moduleRole;
    [SerializeField] private PrefabDependencyEntryMode prefabDependencyEntryMode;
    [SerializeField] private string[] prefabPaths = Array.Empty<string>();
    [SerializeField] private string[] rootPaths = Array.Empty<string>();
    [SerializeField] private BundleFileInfo[] bundlePaths = Array.Empty<BundleFileInfo>();
    [SerializeField] private string[] sourcePaths = Array.Empty<string>();
    [SerializeField] private string[] singleFilePaths = Array.Empty<string>();
    [SerializeField] private int selectedTab;
    [SerializeField] private Vector2 scroll;
    [SerializeField] private bool showRuleHelp;
    [NonSerialized] private bool confirmDeleteVisible;
    [NonSerialized] private float animation;
    [NonSerialized] private double lastAnimationTime;
    [NonSerialized] private float ruleHelpAnimation;
    [NonSerialized] private double lastRuleHelpTime;
    //00 保存实际绘制抽屉的宿主窗口，避免下拉回调误重绘即将关闭的 PopupWindow。
    [NonSerialized] private EditorWindow ownerWindow;

    internal bool IsOpen => isOpen || animation > .001f;
    internal float Animation => animation * animation * (3f - 2f * animation);
    /// <summary>
    /// 00 宿主据此在弹窗打开时暂停抽屉外点击关闭，保持模态语义。
    /// </summary>
    internal bool IsConfirmDialogOpen => confirmDeleteVisible;

    internal void Open(string targetName)
    {
        originalName = targetName ?? string.Empty;
        BundleModuleData data = BuildBundleConfigura.Instance?.GetBundleDataByName(originalName);
        moduleName = data?.moduleName ?? string.Empty;
        addressable = data != null && data.isAddressableAsset;
        //00 旧配置没有该字段时枚举零值自动读取为 Business。
        moduleRole = data?.moduleRole ?? BundleModuleRole.Business;
        //00 旧配置没有该字段时 Unity 自动读取枚举零值，即保持“仅开放 Prefab”。
        prefabDependencyEntryMode = data?.prefabDependencyEntryMode ?? PrefabDependencyEntryMode.PrefabOnly;
        prefabPaths = Clone(data?.prefabPathArr);
        rootPaths = Clone(data?.rootFolderPathArr);
        sourcePaths = Clone(data?.sourceFolderPathArr);
        singleFilePaths = Clone(data?.singleFilePathArr);
        bundlePaths = Clone(data?.signFolderPathArr);
        selectedTab = 0;
        showRuleHelp = false;
        ruleHelpAnimation = 0f;
        confirmDeleteVisible = false;
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

    internal void Draw(EditorWindow owner, Rect rect, Action onSaved)
    {
        //00 宿主由调用方显式传入，后台重绘时不会被 Inspector 或其他焦点窗口错误覆盖。
        ownerWindow = owner;
        //00 模态确认弹窗打开时不绘制抽屉内容，下层没有任何控件，鼠标事件不会先被抽屉吞掉。
        if (confirmDeleteVisible)
        {
            DrawConfirmDeleteDialog(owner, onSaved);
            return;
        }
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
                GUILayout.Space(8);
                //00 模块角色是低频架构选项，使用弱强调紧凑控件而不是大型导航样式。
                moduleRole = BundleModuleRoleUi.DrawSelector(moduleRole);
                //00 在配置处直接说明允许的依赖方向，避免开发者到构建阶段才理解失败原因。
                GUILayout.Label(
                    moduleRole == BundleModuleRole.Shared
                        ? "共享模块可被业务模块依赖，但不能反向引用任何业务模块。"
                        : "业务模块保持自包含，仅允许引用当前工程中唯一的共享模块。",
                    ZMBuildStyles.SettingsFieldHint,
                    GUILayout.MinHeight(22));
                GUILayout.Space(7);
                using (new EditorGUILayout.HorizontalScope(GUILayout.Height(28)))
                {
                    GUILayout.Label("远端资源", ZMBuildStyles.SettingsLabel);
                    GUILayout.FlexibleSpace();
                    addressable = DrawSwitch(addressable);
                }
                GUILayout.Label("开启后，模块资源可在首次使用时从服务器按需下载", ZMBuildStyles.SettingsFieldHint);
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
                    case RuleType.Prefab: DrawPrefabRules(); break;
                    case RuleType.RootFolder: DrawPathList(ref rootPaths, "选择文件夹子包路径"); break;
                    case RuleType.SingleBundle: DrawBundleList(); break;
                    case RuleType.SingleFile: DrawPathList(ref singleFilePaths, "选择单文件包目录"); break;
                    case RuleType.Source: DrawPathList(ref sourcePaths, "选择源文件路径"); break;
                }
                GUILayout.Space(3);
            }
            GUILayout.Space(18);
        }
    }

    /// <summary>
    /// 00 绘制控制当前模块全部 Prefab 搜索目录的统一资源加载策略和路径列表。
    /// </summary>
    private void DrawPrefabRules()
    {
        //00 策略标题与紧凑双段控件由共用方法绘制成一行，降低该辅助选项的视觉权重。
        prefabDependencyEntryMode = PrefabDependencyEntryModeUi.DrawSelector(prefabDependencyEntryMode);
        //00 说明文字随策略变化，避免开发者误以为开放依赖会改变 Bundle 物理分组。
        string hint = prefabDependencyEntryMode == PrefabDependencyEntryMode.PrefabOnly
            ? "业务代码只能直接加载 Prefab；材质、纹理等依赖由 AssetBundle 内部加载。"
            : "Prefab 与本模块拥有的递归依赖均可按路径直接加载；Bundle 分组方式保持不变。";
        //00 提示使用统一弱化文本样式，并保留足够高度支持窄窗口换行。
        GUILayout.Label(hint, ZMBuildStyles.SettingsFieldHint, GUILayout.MinHeight(24));
        //00 紧凑策略说明与路径列表只保留 5px 间隔，减少整个选项占用的垂直空间。
        GUILayout.Space(5);
        //00 现有路径编辑、选择和删除交互保持完全不变。
        DrawPathList(ref prefabPaths, "选择预制体资源文件夹");
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
        const float fullHeight = 228f;
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
        string[] titles = { "预制体包", "文件夹子包", "文件夹包", "单文件包", "源文件配置" };
        string[] descriptions =
        {
            "文件夹中的每个预制体分别生成一个独立 Bundle",
            "所选目录下的每个一级子文件夹分别生成一个 Bundle",
            "将指定文件夹整体打成一个 Bundle，并可自定义 Bundle 名称",
            "目录下的每个可打包文件分别生成一个独立 Bundle，例如：icon 图标",
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
            //00 删除是破坏性操作，置于最左侧并与保存操作保持视觉距离；新模块未保存前禁用。
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(88)))
            {
                GUILayout.Space(14);
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(originalName)))
                {
                    if (GUILayout.Button("删除模块", ZMBuildStyles.CompactSecondaryButton, GUILayout.Width(88), GUILayout.Height(34)))
                        ShowDeleteConfirm();
                }
                GUILayout.Space(18);
            }
            GUILayout.Space(10);
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

    private int RuleCount() => (prefabPaths?.Length ?? 0) + (rootPaths?.Length ?? 0) + (bundlePaths?.Length ?? 0) + (singleFilePaths?.Length ?? 0) + (sourcePaths?.Length ?? 0);

    /// <summary>
    /// 00 打开自绘删除确认弹窗，取代 Unity 原生 DisplayDialog，与 ZMAsset 暗色主题统一。
    /// </summary>
    private void ShowDeleteConfirm()
    {
        confirmDeleteVisible = true;
        RepaintOwner();
    }

    /// <summary>
    /// 00 执行持久化删除并关闭抽屉，宿主随后刷新模块列表。
    /// </summary>
    private void ConfirmDelete(Action onSaved)
    {
        confirmDeleteVisible = false;
        BuildBundleConfigura.Instance.RemoveModuleByName(originalName);
        Close();
        onSaved?.Invoke();
    }

    /// <summary>
    /// 00 在宿主窗口内绘制遮罩与居中确认卡片：点遮罩或 Esc 取消，红色按钮确认删除。
    /// </summary>
    private void DrawConfirmDeleteDialog(EditorWindow owner, Action onSaved)
    {
        Rect full = new Rect(0, 0, owner.position.width, owner.position.height);
        //00 半透明遮罩弱化下层内容，把视觉焦点集中到确认卡片。
        EditorGUI.DrawRect(full, new Color(0, 0, 0, .55f));
        //00 Esc 取消，与原生对话框的键盘习惯一致。
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            confirmDeleteVisible = false;
            Event.current.Use();
            RepaintOwner();
            return;
        }

        Rect card = new Rect(full.x + (full.width - 380) * .5f, full.y + (full.height - 152) * .5f, 380, 152);
        //00 遮罩取消区域为卡片四周，避免透明按钮覆盖卡片内的确认/取消按钮。
        //00 IMGUI 中先绘制的控件先接收鼠标事件，因此四周按钮必须在卡片及其按钮之前绘制。
        if (GUI.Button(new Rect(0, 0, full.width, card.y), GUIContent.none, GUIStyle.none) ||
            GUI.Button(new Rect(0, card.yMax, full.width, full.height - card.yMax), GUIContent.none, GUIStyle.none) ||
            GUI.Button(new Rect(0, card.y, card.x, card.height), GUIContent.none, GUIStyle.none) ||
            GUI.Button(new Rect(card.xMax, card.y, full.width - card.xMax, card.height), GUIContent.none, GUIStyle.none))
        {
            confirmDeleteVisible = false;
            Event.current.Use();
            RepaintOwner();
            return;
        }
        GUI.Box(card, GUIContent.none, ZMBuildStyles.DrawerPanel);
        GUI.Label(new Rect(card.x + 24, card.y + 18, card.width - 48, 26), "删除模块", ZMBuildStyles.CardTitle);
        //00 正文允许换行，避免窄窗口下文案被截断。
        GUIStyle hint = ConfirmHintStyle;
        GUI.Label(
            new Rect(card.x + 24, card.y + 54, card.width - 48, 40),
            $"确定删除模块“{originalName}”吗？该操作会移除模块的全部配置。",
            hint);
        Rect confirmRect = new Rect(card.x + card.width - 96 - 24, card.y + card.height - 24 - 34, 96, 34);
        if (GUI.Button(confirmRect, "确认删除", ZMBuildStyles.CompactDangerButton))
        {
            ConfirmDelete(onSaved);
            return;
        }
        Rect cancelRect = new Rect(confirmRect.x - 9 - 88, confirmRect.y, 88, 34);
        if (GUI.Button(cancelRect, "取消", ZMBuildStyles.CompactSecondaryButton))
        {
            confirmDeleteVisible = false;
            RepaintOwner();
        }
    }

    private static GUIStyle sConfirmHint;
    private static GUIStyle ConfirmHintStyle =>
        sConfirmHint ??= new GUIStyle(ZMBuildStyles.SettingsFieldHint) { wordWrap = true, alignment = TextAnchor.UpperLeft };

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
        //00 保存前执行单 Shared 门禁，失败时不修改持久化模块角色。
        if (!BundleModuleRoleUi.ValidateSingleShared(config, data, moduleRole, out string roleError))
        {
            EditorUtility.DisplayDialog("保存失败", roleError, "确定");
            return;
        }
        //00 角色仅在门禁成功后写入配置对象。
        data.moduleRole = moduleRole;
        data.isAddressableAsset = addressable;
        //00 保存一个模块级策略，统一控制 Prefab Tab 下全部目录的 Entry 开放范围。
        data.prefabDependencyEntryMode = prefabDependencyEntryMode;
        data.prefabPathArr = RemoveEmptyPaths(prefabPaths);
        data.rootFolderPathArr = RemoveEmptyPaths(rootPaths);
        data.signFolderPathArr = Array.FindAll(bundlePaths ?? Array.Empty<BundleFileInfo>(),
            item => item != null && !string.IsNullOrWhiteSpace(item.bundlePath));
        data.sourceFolderPathArr = RemoveEmptyPaths(sourcePaths);
        data.singleFilePathArr = RemoveEmptyPaths(singleFilePaths);
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

    private void RepaintOwner()
    {
        //00 优先重绘 Draw 时捕获的主窗口，DarkDropdownWindow 获得焦点时也不会丢失刷新目标。
        if (ownerWindow != null)
        {
            ownerWindow.Repaint();
            return;
        }

        //00 首次 Draw 之前的防御性回退只接受非下拉窗口，避免刷新即将销毁的 Popup。
        if (EditorWindow.focusedWindow != null && !(EditorWindow.focusedWindow is DarkDropdownWindow))
            EditorWindow.focusedWindow.Repaint();
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
