using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZM.Asset;

[Serializable]
public class BuildHotPatchWindow : BundleBehaviour
{
    [SerializeField] private string patchDes = "输入本次热更描述...";
    [SerializeField] private string hotAppVersion = "0.0.0";
    [SerializeField] private string hotVersion = "1";
    [SerializeField] private Vector2 noticeScrollPosition;

    protected override string PageTitle => "热更补丁";
    protected override string PageSubtitle => "选择需要生成补丁的模块，并配置补丁版本信息";

    public override void Initzation(int unusedHeight = 0)
    {
        base.Initzation(unusedHeight);
        hotVersion = EditorPrefs.GetString("PatchVersion", "1");
    }

    protected override void DrawBuildOptions()
    {
        Rect panel = GUILayoutUtility.GetRect(0, 206, GUILayout.ExpandWidth(true), GUILayout.Height(206));
        GUI.Box(panel, GUIContent.none, ZMBuildStyles.CardBox);

        GUI.Label(new Rect(panel.x + 18, panel.y + 12, panel.width - 36, 25), "补丁信息", new GUIStyle(ZMBuildStyles.CardTitle) { fontSize = 15 });

        float gap = 16;
        float columnWidth = (panel.width - 36 - gap) * .5f;
        Rect appLabel = new(panel.x + 18, panel.y + 42, columnWidth, 20);
        Rect versionLabel = new(appLabel.xMax + gap, appLabel.y, columnWidth, 20);
        GUI.Label(appLabel, "生效应用版本", ZMBuildStyles.CardMeta);
        GUI.Label(versionLabel, "热更补丁版本", ZMBuildStyles.CardMeta);

        Rect appField = new(appLabel.x, appLabel.yMax + 2, columnWidth, 34);
        Rect versionField = new(versionLabel.x, versionLabel.yMax + 2, columnWidth, 34);
        hotAppVersion = ZMBuildStyles.DrawTextField(appField, hotAppVersion, ZMBuildStyles.InputField);
        hotVersion = ZMBuildStyles.DrawTextField(versionField, hotVersion, ZMBuildStyles.InputField);

        Rect noticeLabel = new(panel.x + 18, panel.y + 104, 78, 34);
        GUI.Label(noticeLabel, "热更公告", ZMBuildStyles.CardMeta);
        Rect noticeField = new(panel.x + 96, panel.y + 102, panel.width - 114, 82);
        DrawScrollableNotice(noticeField);

        GUI.Label(new Rect(noticeField.x, noticeField.yMax + 3, noticeField.width - 76, 16),
            $"{(patchDes ?? string.Empty).Length} 字", ZMBuildStyles.SettingsFieldHint);
        Rect expandRect = new(noticeField.xMax - 70, noticeField.yMax + 1, 70, 18);
        if (GUI.Button(expandRect, "展开编辑", ZMBuildStyles.LinkButton))
            PatchDescriptionEditorWindow.ShowWindow(this, patchDes);
    }

    private void DrawScrollableNotice(Rect rect)
    {
        GUI.Box(rect, GUIContent.none, ZMBuildStyles.FieldBox);
        const float scrollbarWidth = 14f;
        float textWidth = Mathf.Max(80, rect.width - scrollbarWidth - 12);
        float contentHeight = Mathf.Max(rect.height - 10,
            ZMBuildStyles.TextArea.CalcHeight(new GUIContent(patchDes ?? string.Empty), textWidth) + 12);
        Rect viewport = new(rect.x + 5, rect.y + 5, rect.width - 10, rect.height - 10);
        Rect content = new(0, 0, viewport.width - scrollbarWidth, contentHeight);
        noticeScrollPosition = GUI.BeginScrollView(viewport, noticeScrollPosition, content, false, contentHeight > viewport.height);
        patchDes = GUI.TextArea(new Rect(0, 0, content.width, content.height), patchDes ?? string.Empty, ZMBuildStyles.TextArea);
        GUI.EndScrollView();
    }

    internal void SetPatchDescription(string value)
    {
        patchDes = value ?? string.Empty;
        noticeScrollPosition = Vector2.zero;
    }

    public override void DrawBuildButtons()
    {
        GUILayout.Space(24);
        using (new EditorGUILayout.HorizontalScope(GUILayout.Height(78)))
        {
            GUILayout.Space(34);
            GUILayout.Label("补丁输出", ZMBuildStyles.StatusLabel, GUILayout.Width(62), GUILayout.Height(48));

            float outputWidth = Mathf.Clamp(EditorGUIUtility.currentViewWidth * .28f, 240f, 360f);
            Rect outputRect = GUILayoutUtility.GetRect(outputWidth, 38, GUILayout.Width(outputWidth), GUILayout.Height(38));
            outputRect.y += 5;
            GUI.Box(outputRect, GUIContent.none, ZMBuildStyles.FieldBox);
            string moduleName = GetFirstSelectedModuleName();
            string moduleSegment = string.IsNullOrEmpty(moduleName) ? "[选择模块]" : moduleName;
            string output = $"HotAssets/{moduleSegment}/{hotAppVersion}/{hotVersion}/{EditorUserBuildSettings.activeBuildTarget}";
            GUI.Label(outputRect, new GUIContent(output, output), ZMBuildStyles.FlatField);

            Rect folderRect = new(outputRect.xMax - 35, outputRect.y + 1, 34, outputRect.height - 2);
            EditorGUI.DrawRect(new Rect(folderRect.x, folderRect.y, 1, folderRect.height), ZMBuildStyles.Border);
            Texture folder = EditorGUIUtility.IconContent("Folder Icon").image;
            if (folder != null) GUI.DrawTexture(new Rect(folderRect.x + 8, folderRect.y + 8, 19, 19), folder, ScaleMode.ScaleToFit);
            EditorGUIUtility.AddCursorRect(folderRect, MouseCursor.Link);
            if (GUI.Button(folderRect, GUIContent.none, GUIStyle.none))
                BuildBundleCompiler.OpenHotBundleFolder(moduleName, hotAppVersion, hotVersion, EditorUserBuildSettings.activeBuildTarget);

            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(true))
                GUILayout.Button("上传资源", ZMBuildStyles.SecondaryButton, GUILayout.Width(145));
            GUILayout.Space(12);
            using (new EditorGUI.DisabledScope(SelectedCount == 0))
                if (GUILayout.Button("开始构建补丁", ZMBuildStyles.PrimaryButton, GUILayout.Width(170))) BuildBundle();
            GUILayout.Space(34);
        }
    }

    private string GetFirstSelectedModuleName()
    {
        if (moduleDataList == null) return string.Empty;
        foreach (BundleModuleData module in moduleDataList)
            if (module != null && module.isBuild && !string.IsNullOrEmpty(module.moduleName))
                return module.moduleName;
        return string.Empty;
    }

    public override void BuildBundle()
    {
        if (!int.TryParse(hotVersion, out int patchVersion) || patchVersion < 0)
        {
            EditorUtility.DisplayDialog("打包失败", "热更补丁版本必须是非负整数。", "确定");
            return;
        }

        EditorPrefs.SetString("PatchVersion", hotVersion);
        //00 冻结业务模块选择；Shared 若作为依赖会自动加入只读比对，但不能直接发布热更。
        var selectedModules = new List<BundleModuleData>();
        foreach (BundleModuleData item in moduleDataList)
            if (item != null && item.isBuild) selectedModules.Add(item);
        //00 热更也使用单一事务，避免多个业务模块中途失败后只发布一部分。
        var jobs = new List<(string name, Func<System.Collections.IEnumerator> action)>();
        jobs.Add(("模块依赖闭包", () => MultiModuleBuildOrchestrator.BuildStaged(
            selectedModules,
            BuildType.HotPatch,
            patchVersion,
            hotAppVersion,
            patchDes)));
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HotAssets"));
        ZMBuildProgress.RunStaged("热更补丁", jobs, output);
    }
}

internal class PatchDescriptionEditorWindow : EditorWindow
{
    private BuildHotPatchWindow owner;
    private string value;
    private Vector2 scroll;
    private bool dragging;
    private Vector2 dragOffset;

    internal static void ShowWindow(BuildHotPatchWindow owner, string value)
    {
        var window = CreateInstance<PatchDescriptionEditorWindow>();
        window.titleContent = new GUIContent("编辑热更公告");
        window.owner = owner;
        window.value = value ?? string.Empty;
        window.minSize = new Vector2(560, 360);
        window.position = new Rect(GUIUtility.GUIToScreenPoint(Event.current.mousePosition) - new Vector2(280, 40), window.minSize);
        window.ShowPopup();
    }

    private void OnGUI()
    {
        ZMBuildStyles.Ensure();
        GUI.Box(new Rect(0, 0, position.width, position.height), GUIContent.none, ZMBuildStyles.PopupWindowBox);
        GUI.Box(new Rect(2, 2, position.width - 4, 50), GUIContent.none, ZMBuildStyles.PopupHeaderBox);
        EditorGUI.DrawRect(new Rect(2, 28, position.width - 4, 24), ZMBuildStyles.Header);
        EditorGUI.DrawRect(new Rect(0, 51, position.width, 1), ZMBuildStyles.Border);
        HandleWindowDrag(new Rect(0, 0, position.width - 52, 52));
        GUI.Label(new Rect(22, 8, position.width - 76, 25), "编辑热更公告", ZMBuildStyles.CardTitle);
        GUI.Label(new Rect(22, 30, position.width - 76, 17), "编辑本次补丁的完整更新说明", ZMBuildStyles.SettingsFieldHint);
        if (GUI.Button(new Rect(position.width - 42, 10, 28, 28), "×", ZMBuildStyles.CloseButton)) Close();

        Rect field = new(22, 70, position.width - 44, Mathf.Max(180, position.height - 142));
        GUI.Box(field, GUIContent.none, ZMBuildStyles.FieldBox);
        Rect inner = new(field.x + 8, field.y + 8, field.width - 16, field.height - 16);
        float height = Mathf.Max(inner.height, ZMBuildStyles.TextArea.CalcHeight(new GUIContent(value), inner.width - 18) + 10);
        scroll = GUI.BeginScrollView(inner, scroll, new Rect(0, 0, inner.width - 14, height));
        value = GUI.TextArea(new Rect(0, 0, inner.width - 16, height), value, ZMBuildStyles.TextArea);
        GUI.EndScrollView();

        float footerY = position.height - 58;
        GUI.Label(new Rect(22, footerY + 8, 100, 22), $"{value.Length} 字", ZMBuildStyles.SettingsFieldHint);
        Rect applyRect = new(position.width - 132, footerY, 110, 34);
        Rect cancelRect = new(applyRect.x - 100, footerY, 88, 34);
        if (GUI.Button(cancelRect, "取消", ZMBuildStyles.CompactSecondaryButton)) Close();
        if (GUI.Button(applyRect, "应用公告", ZMBuildStyles.CompactPrimaryButton))
        {
            owner?.SetPatchDescription(value);
            Close();
        }

        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape) Close();
    }

    private void HandleWindowDrag(Rect dragArea)
    {
        Event current = Event.current;
        if (current.button != 0) return;

        if (current.type == EventType.MouseDown && dragArea.Contains(current.mousePosition))
        {
            dragging = true;
            dragOffset = GUIUtility.GUIToScreenPoint(current.mousePosition) - position.position;
            current.Use();
        }
        else if (current.type == EventType.MouseDrag && dragging)
        {
            Vector2 screenMouse = GUIUtility.GUIToScreenPoint(current.mousePosition);
            Rect next = position;
            next.position = screenMouse - dragOffset;
            position = next;
            current.Use();
        }
        else if (current.type == EventType.MouseUp && dragging)
        {
            dragging = false;
            current.Use();
        }
    }
}
