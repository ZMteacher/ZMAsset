using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[Serializable]
public class BundleBehaviour
{
    [NonSerialized] public Action<string> EditModuleRequested;
    protected List<BundleModuleData> moduleDataList;
    [SerializeField] protected Vector2 scrollPosition;
    [SerializeField] private string searchText = string.Empty;

    public int SelectedCount
    {
        get
        {
            if (moduleDataList == null) return 0;
            int count = 0;
            foreach (BundleModuleData module in moduleDataList) if (module.isBuild) count++;
            return count;
        }
    }

    protected virtual string PageTitle => "资源模块";
    protected virtual string PageSubtitle => "选择需要参与本次构建的模块";

    public virtual void Initzation(int unusedHeight = 0)
    {
        moduleDataList = BuildBundleConfigura.Instance != null
            ? BuildBundleConfigura.Instance.AssetBundleConfig
            : new List<BundleModuleData>();
    }

    public virtual void OGUI()
    {
        if (moduleDataList == null) Initzation();
        ZMBuildStyles.Ensure();
        if (Event.current.type == EventType.MouseMove) RepaintOwner();

        GUILayout.Space(28);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(34);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
            {
                GUILayout.Label(PageTitle, ZMBuildStyles.Heading, GUILayout.Height(32));
                GUILayout.Label(PageSubtitle, ZMBuildStyles.Subtitle, GUILayout.Height(22));
                GUILayout.Space(13);
                DrawSearch();
                GUILayout.Space(14);
                DrawModules();
                DrawBuildOptions();
            }
            GUILayout.Space(34);
        }

        DrawBuildButtons();
    }

    private void DrawSearch()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            float width = Mathf.Min(480, EditorGUIUtility.currentViewWidth - 300);
            Rect rect = GUILayoutUtility.GetRect(width, 36, GUILayout.Width(width), GUILayout.Height(36));
            searchText = ZMBuildStyles.DrawTextField(rect, searchText, ZMBuildStyles.Search);
            DrawSearchIcon(new Rect(rect.x + 10, rect.y + 9, 17, 17));
            if (string.IsNullOrEmpty(searchText))
                GUI.Label(new Rect(rect.x + 31, rect.y, rect.width - 40, rect.height), "搜索模块...", ZMBuildStyles.Subtitle);
            GUILayout.FlexibleSpace();
        }
    }

    private static void DrawSearchIcon(Rect rect)
    {
        Handles.BeginGUI();
        Color old = Handles.color;
        Matrix4x4 oldMatrix = Handles.matrix;
        Handles.matrix = Matrix4x4.identity;
        Handles.color = ZMBuildStyles.Muted;
        ZMBuildStyles.DrawGuiCircle(new Vector2(rect.x + 6, rect.y + 6), 5f, 1.5f);
        Handles.DrawAAPolyLine(1.5f, new Vector3(rect.x + 10, rect.y + 10), new Vector3(rect.x + 16, rect.y + 16));
        Handles.matrix = oldMatrix;
        Handles.color = old;
        Handles.EndGUI();
    }

    private void DrawModules()
    {
        using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition, GUILayout.ExpandHeight(true)))
        {
            scrollPosition = scroll.scrollPosition;
            List<BundleModuleData> visible = GetVisibleModules();
            float availableWidth = Mathf.Max(300, EditorGUIUtility.currentViewWidth - 260);
            int columns = availableWidth >= 850 ? 4 : availableWidth >= 620 ? 3 : 2;
            const float spacing = 14;
            float cardWidth = Mathf.Max(150, (availableWidth - spacing * (columns - 1)) / columns);
            const float cardHeight = 132;

            int total = visible.Count + (string.IsNullOrEmpty(searchText) ? 1 : 0);
            for (int index = 0; index < total; index += columns)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int column = 0; column < columns; column++)
                    {
                        int itemIndex = index + column;
                        if (itemIndex < visible.Count)
                            DrawModuleCard(visible[itemIndex], cardWidth, cardHeight);
                        else if (itemIndex == visible.Count && string.IsNullOrEmpty(searchText))
                            DrawAddCard(cardWidth, cardHeight);
                        else
                            GUILayout.Space(cardWidth);
                        if (column < columns - 1) GUILayout.Space(spacing);
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Space(14);
            }

            if (visible.Count == 0 && !string.IsNullOrEmpty(searchText))
                GUILayout.Label("没有找到匹配的资源模块", ZMBuildStyles.Subtitle);
        }
    }

    private List<BundleModuleData> GetVisibleModules()
    {
        if (string.IsNullOrWhiteSpace(searchText)) return moduleDataList;
        string keyword = searchText.Trim();
        return moduleDataList.FindAll(item => !string.IsNullOrEmpty(item.moduleName) && item.moduleName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void DrawModuleCard(BundleModuleData module, float width, float height)
    {
        Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
        bool selected = module.isBuild;
        bool hovered = rect.Contains(Event.current.mousePosition);
        string animationKey = string.IsNullOrEmpty(module.moduleName) ? module.bundleid.ToString() : module.moduleName;
        float editProgress = ZMEditorAnimation.Tween($"ZMAsset.Card.Edit.{animationKey}", hovered ? 1f : 0f, .03f);
        Rect visualRect = rect;
        GUI.Box(visualRect, GUIContent.none, selected ? ZMBuildStyles.CardSelectedBox : ZMBuildStyles.CardBox);

        GUI.Label(new Rect(visualRect.x + 18, visualRect.y + 16, visualRect.width - 58, 28), module.moduleName, ZMBuildStyles.CardTitle);
        Rect check = new Rect(visualRect.xMax - 35, visualRect.y + 16, 20, 20);
        GUI.Box(check, GUIContent.none, selected ? ZMBuildStyles.ToggleOn : ZMBuildStyles.ToggleOff);
        if (selected) DrawCheckmark(check);

        int bundleCount = CountRules(module);
        DrawBundleIcon(new Rect(visualRect.x + 18, visualRect.y + 60, 17, 17));
        GUI.Label(new Rect(visualRect.x + 42, visualRect.y + 57, visualRect.width - 55, 24), $"{bundleCount} Bundles", ZMBuildStyles.CardMeta);

        DrawResourceIcon(new Rect(visualRect.x + 18, visualRect.y + 91, 17, 17));
        Rect editRect = new(visualRect.xMax - 69, visualRect.yMax - 32, 54, 24);
        //00 Shared 卡片优先展示模块职责；普通业务模块继续展示既有寻址方式。
        string moduleMeta = module.moduleRole == BundleModuleRole.Shared
            ? "共享模块"
            : module.isAddressableAsset ? "远端资源" : "本地资源";
        GUI.Label(new Rect(visualRect.x + 42, visualRect.y + 88, editProgress > .15f ? visualRect.width - 118 : visualRect.width - 55, 24), moduleMeta, ZMBuildStyles.CardMeta);

        if (editProgress > .001f)
        {
            Color oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, editProgress);
            if (hovered && GUI.Button(editRect, new GUIContent("编辑", "编辑模块配置"), ZMBuildStyles.CardEditButton))
                RequestModuleEdit(module.moduleName);
            DrawEditPencil(new Rect(editRect.x + 7, editRect.y + 7, 10, 10), editProgress);
            GUI.color = oldColor;
            if (hovered) EditorGUIUtility.AddCursorRect(editRect, MouseCursor.Link);
        }

        Event evt = Event.current;
        if (evt.type == EventType.MouseDown && rect.Contains(evt.mousePosition) && evt.button == 1)
        {
            Vector2 screenPoint = GUIUtility.GUIToScreenPoint(evt.mousePosition);
            DarkDropdownWindow.Show(new Rect(screenPoint.x, screenPoint.y, 138, 0),
                new[] { "编辑配置" }, 0, _ => RequestModuleEdit(module.moduleName), false);
            evt.Use();
        }
        else if (evt.type == EventType.MouseDown && rect.Contains(evt.mousePosition) && !new Rect(editRect.x, editRect.y, editRect.width, editRect.height + 4).Contains(evt.mousePosition) && evt.button == 0)
        {
            if (evt.clickCount == 1)
                module.isBuild = !module.isBuild;
            evt.Use();
            RepaintOwner();
        }
    }

    private static void DrawEditPencil(Rect rect, float alpha)
    {
        Handles.BeginGUI();
        Color old = Handles.color;
        Matrix4x4 oldMatrix = Handles.matrix;
        Handles.matrix = Matrix4x4.identity;
        Handles.color = new Color(151f / 255f, 202f / 255f, 242f / 255f, alpha);
        Handles.DrawAAPolyLine(1.5f,
            new Vector3(rect.x + 1, rect.yMax - 1),
            new Vector3(rect.x + 3, rect.yMax - 5),
            new Vector3(rect.xMax - 2, rect.y),
            new Vector3(rect.xMax, rect.y + 2),
            new Vector3(rect.x + 5, rect.yMax - 3),
            new Vector3(rect.x + 1, rect.yMax - 1));
        Handles.matrix = oldMatrix;
        Handles.color = old;
        Handles.EndGUI();
    }

    private static void DrawCheckmark(Rect rect)
    {
        Handles.BeginGUI();
        Color old = Handles.color;
        Handles.color = new Color32(16, 49, 67, 255);
        Handles.DrawAAPolyLine(2.2f, new Vector3(rect.x + 5, rect.y + 10), new Vector3(rect.x + 9, rect.y + 14), new Vector3(rect.x + 16, rect.y + 6));
        Handles.color = old;
        Handles.EndGUI();
    }

    private static void DrawBundleIcon(Rect rect)
    {
        float size = Mathf.Floor(Mathf.Min(rect.width, rect.height));
        Rect icon = new Rect(
            Mathf.Round(rect.center.x - size * .5f) + .5f,
            Mathf.Round(rect.center.y - size * .5f) + .5f,
            size,
            size);
        Handles.BeginGUI();
        Color old = Handles.color;
        Matrix4x4 oldMatrix = Handles.matrix;
        Handles.matrix = Matrix4x4.identity;
        Handles.color = ZMBuildStyles.Muted;
        Vector3 top = new(icon.center.x, icon.y + 1);
        Vector3 left = new(icon.x + 2, icon.y + 5);
        Vector3 middle = new(icon.center.x, icon.y + 9);
        Vector3 right = new(icon.xMax - 2, icon.y + 5);
        Vector3 lowerLeft = new(icon.x + 2, icon.yMax - 4);
        Vector3 bottom = new(icon.center.x, icon.yMax - 1);
        Vector3 lowerRight = new(icon.xMax - 2, icon.yMax - 4);
        Handles.DrawAAPolyLine(1.5f, top, right, middle, left, top);
        Handles.DrawAAPolyLine(1.5f, left, lowerLeft, bottom, middle);
        Handles.DrawAAPolyLine(1.5f, middle, bottom, lowerRight, right);
        Handles.matrix = oldMatrix;
        Handles.color = old;
        Handles.EndGUI();
    }

    private static void DrawResourceIcon(Rect rect)
    {
        float size = Mathf.Floor(Mathf.Min(rect.width, rect.height));
        Rect icon = new Rect(
            Mathf.Round(rect.center.x - size * .5f) + .5f,
            Mathf.Round(rect.center.y - size * .5f) + .5f,
            size,
            size);
        Handles.BeginGUI();
        Color old = Handles.color;
        Matrix4x4 oldMatrix = Handles.matrix;
        Handles.matrix = Matrix4x4.identity;
        Handles.color = ZMBuildStyles.Muted;
        float left = icon.x + 2;
        float right = icon.xMax - 2;
        float shoulder = left + 6;
        Vector3[] tag =
        {
            new(left, icon.y + 3), new(shoulder, icon.y + 3),
            new(right, icon.center.y), new(shoulder, icon.yMax - 2),
            new(left, icon.y + 10), new(left, icon.y + 3)
        };
        Handles.DrawAAPolyLine(1.5f, tag);
        ZMBuildStyles.DrawGuiCircle(new Vector2(left + 3.5f, icon.y + 7), 1.2f, 1.2f, 12);
        Handles.matrix = oldMatrix;
        Handles.color = old;
        Handles.EndGUI();
    }

    private static int CountRules(BundleModuleData module)
    {
        int count = module.prefabPathArr?.Length ?? 0;
        count += module.rootFolderPathArr?.Length ?? 0;
        count += module.signFolderPathArr?.Length ?? 0;
        //00 修复历史遗漏：源文件复制规则未计入卡片统计；逐文件分包规则同样需要计入。
        count += module.sourceFolderPathArr?.Length ?? 0;
        count += module.singleFilePathArr?.Length ?? 0;
        return count;
    }

    private void DrawAddCard(float width, float height)
    {
        Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
        GUI.Box(rect, GUIContent.none, ZMBuildStyles.AddCardBox);
        GUI.Label(new Rect(rect.x, rect.y + 25, rect.width, 40), "+", new GUIStyle(ZMBuildStyles.Heading) { alignment = TextAnchor.MiddleCenter, fontSize = 34, normal = { textColor = ZMBuildStyles.Muted } });
        GUI.Label(new Rect(rect.x, rect.y + 74, rect.width, 25), "新建模块", new GUIStyle(ZMBuildStyles.Subtitle) { alignment = TextAnchor.MiddleCenter });
        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) RequestModuleEdit(string.Empty);
    }

    private void RequestModuleEdit(string moduleName)
    {
        if (EditModuleRequested != null) EditModuleRequested(moduleName);
        else BundleModuleConfig.ShowWindow(moduleName);
    }

    protected virtual void DrawBuildOptions() { }
    protected static void RepaintOwner() { if (EditorWindow.focusedWindow != null) EditorWindow.focusedWindow.Repaint(); }
    public virtual void DrawBuildButtons() { }
    public virtual void BuildBundle() { }
}
