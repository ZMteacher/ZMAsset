using System.IO;
using SuperScrollView;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using ZM.UI;
using Object = UnityEngine.Object;

/// <summary>
/// RemoteAsset 测试工具：一键生成窗口与 Item 预制体，并注册 UISetting 窗口目录。
/// 使用方式：菜单 ZM/RemoteAssetTest/生成测试窗口预制体
/// </summary>
public static class RemoteAssetTestWindowGenerator
{
    private const string OutputRoot = "Assets/Test/RemoteAssetTest/Resources";
    private const string WindowPrefabPath = OutputRoot + "/RemoteAssetTestWindow.prefab";
    private const string ItemPrefabPath = OutputRoot + "/Item/RemoteAssetTestItem.prefab";
    private const string UISettingPath = "Assets/ZMPackages/ZMUI/Resources/UISetting.asset";

    [MenuItem("ZM/RemoteAssetTest/生成测试窗口预制体")]
    public static void Generate()
    {
        EnsureFolder(OutputRoot + "/Item");

        // ---------- 1. Item 预制体 ----------
        GameObject itemRoot = CreateUIGameObject("RemoteAssetTestItem", Vector2.zero, new Vector2(600, 90));
        RawImage itemIcon = itemRoot.AddComponent<RawImage>();
        itemIcon.raycastTarget = false;
        Text itemName = CreateText(itemRoot.transform, "NameText", new Vector2(-160, 0), new Vector2(200, 40));
        itemName.alignment = TextAnchor.MiddleLeft;
        Text itemStatus = CreateText(itemRoot.transform, "StatusText", new Vector2(190, 0), new Vector2(180, 40));
        itemStatus.alignment = TextAnchor.MiddleRight;
        RemoteAssetTestItem itemScript = itemRoot.AddComponent<RemoteAssetTestItem>();
        itemScript.iconRawImage = itemIcon;
        itemScript.nameText = itemName;
        itemScript.statusText = itemStatus;
        PrefabUtility.SaveAsPrefabAsset(itemRoot, ItemPrefabPath);
        Object.DestroyImmediate(itemRoot);
        GameObject itemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ItemPrefabPath);

        // ---------- 2. 窗口预制体 ----------
        GameObject root = CreateUIGameObject("RemoteAssetTestWindow", Vector2.zero, Vector2.zero);
        root.AddComponent<Canvas>().renderMode = RenderMode.ScreenSpaceCamera;
        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        root.AddComponent<GraphicRaycaster>();
        CanvasGroup rootGroup = root.AddComponent<CanvasGroup>();
        rootGroup.interactable = rootGroup.blocksRaycasts = true;

        // UIMask（半透明遮罩，带 CanvasGroup）——WindowBase.InitializeBaseComponent 要求存在名为 UIMask 的子物体
        GameObject uiMask = CreateUIGameObject("UIMask", Vector2.zero, new Vector2(1920, 1080));
        uiMask.transform.SetParent(root.transform, false);
        Image maskImage = uiMask.AddComponent<Image>();
        maskImage.color = new Color(0, 0, 0, 0.6f);
        maskImage.raycastTarget = false;
        uiMask.AddComponent<CanvasGroup>();

        // UIContent——WindowBase.InitializeBaseComponent 要求存在名为 UIContent 的子物体
        GameObject uiContent = new GameObject("UIContent", typeof(RectTransform));
        uiContent.transform.SetParent(root.transform, false);
        RectTransform contentRect = (RectTransform)uiContent.transform;
        contentRect.anchorMin = Vector2.zero;
        contentRect.anchorMax = Vector2.one;
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        // 标题
        Text title = CreateText(uiContent.transform, "TitleText", new Vector2(0, 480), new Vector2(800, 60));
        title.text = "RemoteAsset 远端资源测试";
        title.fontSize = 36;
        title.alignment = TextAnchor.MiddleCenter;

        // 服务器信息
        Text server = CreateText(uiContent.transform, "ServerText", new Vector2(0, 420), new Vector2(900, 40));
        server.alignment = TextAnchor.MiddleCenter;
        server.fontSize = 20;
        server.text = "服务器：";

        // 列表：ScrollView(ScrollRect+LoopListView2+ZMUIListView) → Viewport → Content
        GameObject scrollView = CreateUIGameObject("ItemScrollView", new Vector2(0, 100), new Vector2(900, 700));
        scrollView.transform.SetParent(uiContent.transform, false);
        Image svBg = scrollView.AddComponent<Image>();
        svBg.color = new Color(0.1f, 0.1f, 0.12f, 0.8f);
        ScrollRect scrollRect = scrollView.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;

        GameObject viewport = CreateUIGameObject("Viewport", Vector2.zero, Vector2.zero);
        viewport.transform.SetParent(scrollView.transform, false);
        RectTransform viewportRect = (RectTransform)viewport.transform;
        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(10, 10);
        viewportRect.offsetMax = new Vector2(-30, -10);
        viewport.AddComponent<RectMask2D>();

        GameObject content = CreateUIGameObject("Content", Vector2.zero, Vector2.zero);
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRect2 = (RectTransform)content.transform;
        contentRect2.anchorMin = new Vector2(0, 1);
        contentRect2.anchorMax = new Vector2(1, 1);
        contentRect2.pivot = new Vector2(0.5f, 1);
        contentRect2.sizeDelta = new Vector2(0, 100);

        scrollRect.viewport = viewportRect;
        scrollRect.content = contentRect2;

        LoopListView2 loopListView = scrollView.AddComponent<LoopListView2>();
        ZMUIListView zmListView = scrollView.AddComponent<ZMUIListView>();
        zmListView.loopListView = loopListView;
        loopListView.ItemPrefabDataList.Add(new ItemPrefabConfData
        {
            mItemPrefab = itemPrefab,
            mPadding = 10,
            mInitCreateCount = 3,
        });

        // ---------- 3. 进度条（完整 Slider 结构） ----------
        GameObject sliderGo = CreateUIGameObject("ProgressSlider", new Vector2(0, -300), new Vector2(700, 30));
        sliderGo.transform.SetParent(uiContent.transform, false);
        Image sliderBgImage = sliderGo.AddComponent<Image>();
        sliderBgImage.color = new Color(0.2f, 0.2f, 0.22f, 1f);
        Slider slider = sliderGo.AddComponent<Slider>();

        // Fill Area → Fill（fillAmount 进度，Fill 的 Image 必须是 Filled 类型）
        GameObject fillArea = CreateUIGameObject("Fill Area", Vector2.zero, new Vector2(0, 0));
        fillArea.transform.SetParent(sliderGo.transform, false);
        RectTransform fillAreaRect = (RectTransform)fillArea.transform;
        fillAreaRect.anchorMin = new Vector2(0, 0.25f);
        fillAreaRect.anchorMax = new Vector2(1, 0.75f);
        fillAreaRect.offsetMin = Vector2.zero;
        fillAreaRect.offsetMax = Vector2.zero;
        GameObject fill = CreateUIGameObject("Fill", Vector2.zero, Vector2.zero);
        fill.transform.SetParent(fillArea.transform, false);
        RectTransform fillRect = (RectTransform)fill.transform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        Image fillImage = fill.AddComponent<Image>();
        fillImage.color = new Color(0.2f, 0.75f, 0.35f, 1f);
        fillImage.raycastTarget = false;
        fillImage.type = Image.Type.Filled;
        fillImage.fillMethod = Image.FillMethod.Horizontal;

        slider.fillRect = fillRect;
        slider.targetGraphic = fillImage;
        slider.direction = Slider.Direction.LeftToRight;

        // 进度文本
        Text progressText = CreateText(uiContent.transform, "ProgressText", new Vector2(0, -350), new Vector2(200, 40));
        progressText.alignment = TextAnchor.MiddleCenter;
        progressText.text = "0%";

        // ---------- 4. 按钮区 ----------
        Button preButton = CreateButton(uiContent.transform, "PreDownloadButton", new Vector2(-300, -430), "预下载整模块");
        Button loadButton = CreateButton(uiContent.transform, "LoadAllButton", new Vector2(0, -430), "逐项加载");
        Button closeButton = CreateButton(uiContent.transform, "CloseButton", new Vector2(300, -430), "关闭");

        // ---------- 5. 窗口数据组件绑定（WindowBase 为纯 C# 类，运行时由 UIModule new 出并挂接，prefab 只挂 MonoBehaviour 数据组件） ----------
        RemoteAssetTestWindowDataComponent data = root.AddComponent<RemoteAssetTestWindowDataComponent>();
        data.titleText = title;
        data.serverText = server;
        data.itemListView = zmListView;
        data.progressSlider = slider;
        data.progressText = progressText;
        data.preDownloadButton = preButton;
        data.loadAllButton = loadButton;
        data.closeButton = closeButton;

        PrefabUtility.SaveAsPrefabAsset(root, WindowPrefabPath);
        Object.DestroyImmediate(root);

        // ---------- 6. 注册 UISetting 窗口目录 ----------
        UISetting setting = AssetDatabase.LoadAssetAtPath<UISetting>(UISettingPath);
        if (setting != null && setting.WindowPrefabFolderPathArr != null)
        {
            bool contains = System.Array.IndexOf(setting.WindowPrefabFolderPathArr, OutputRoot) >= 0;
            if (!contains)
            {
                var list = new System.Collections.Generic.List<string>(setting.WindowPrefabFolderPathArr) { OutputRoot };
                setting.WindowPrefabFolderPathArr = list.ToArray();
                EditorUtility.SetDirty(setting);
                AssetDatabase.SaveAssets();
            }
        }

        AssetDatabase.Refresh();
        Debug.Log($"[RemoteAssetTest] 生成完成：{WindowPrefabPath} / {ItemPrefabPath}");
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        string name = Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    private static GameObject CreateUIGameObject(string name, Vector2 pos, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)go.transform;
        rect.anchoredPosition = pos;
        rect.sizeDelta = size;
        return go;
    }

    private static Text CreateText(Transform parent, string name, Vector2 pos, Vector2 size)
    {
        GameObject go = CreateUIGameObject(name, pos, size);
        go.transform.SetParent(parent, false);
        go.AddComponent<CanvasRenderer>();
        Text text = go.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 24;
        text.color = Color.white;
        text.raycastTarget = false;
        return text;
    }

    private static Button CreateButton(Transform parent, string name, Vector2 pos, string label)
    {
        GameObject go = CreateUIGameObject(name, pos, new Vector2(220, 70));
        go.transform.SetParent(parent, false);
        Image image = go.AddComponent<Image>();
        image.color = new Color(0.2f, 0.45f, 0.9f, 1f);
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;
        Text text = CreateText(go.transform, "Label", Vector2.zero, new Vector2(200, 50));
        text.text = label;
        text.alignment = TextAnchor.MiddleCenter;
        return button;
    }
}
