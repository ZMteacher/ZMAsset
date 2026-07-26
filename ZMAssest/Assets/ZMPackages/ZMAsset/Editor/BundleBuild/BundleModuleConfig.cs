using UnityEditor;
using UnityEngine;

public class BundleModuleConfig : EditorWindow
{
    private enum PathType
    {
        Prefab,
        RootFolder,
        SingleBundle,
        Source
    }

    [SerializeField] private string moduleName;
    [SerializeField] private string originalModuleName;
    [SerializeField] private bool isAddressableAsset;
    [SerializeField] private string[] prefabPathArr = { "Path..." };
    [SerializeField] private string[] rootFolderPathArr = { };
    [SerializeField] private BundleFileInfo[] signFolderPathArr = { };
    [SerializeField] private string[] sourceFolderPathArr = { };
    [SerializeField] private int selectedTab;
    [SerializeField] private Vector2 scrollPosition;

    private static readonly string[] TabNames = { "预制体包", "文件夹子包", "单个补丁包", "源文件配置" };

    public static void ShowWindow(string targetModuleName)
    {
        BundleModuleConfig window = GetWindow<BundleModuleConfig>(true, "资源模块配置", true);
        window.minSize = new Vector2(620, 520);
        window.Load(targetModuleName);
        window.Show();
    }

    private void Load(string targetModuleName)
    {
        originalModuleName = targetModuleName;
        BundleModuleData data = BuildBundleConfigura.Instance?.GetBundleDataByName(targetModuleName);
        if (data == null)
        {
            moduleName = string.Empty;
            isAddressableAsset = false;
            prefabPathArr = new[] { "Path..." };
            rootFolderPathArr = new string[0];
            signFolderPathArr = new BundleFileInfo[0];
            sourceFolderPathArr = new string[0];
        }
        else
        {
            moduleName = data.moduleName;
            isAddressableAsset = data.isAddressableAsset;
            prefabPathArr = data.prefabPathArr ?? new string[0];
            rootFolderPathArr = data.rootFolderPathArr ?? new string[0];
            signFolderPathArr = data.signFolderPathArr ?? new BundleFileInfo[0];
            sourceFolderPathArr = data.sourceFolderPathArr ?? new string[0];
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(8);
        moduleName = EditorGUILayout.TextField("资源模块名称", moduleName);
        isAddressableAsset = EditorGUILayout.Toggle("是否可寻址资源", isAddressableAsset);
        EditorGUILayout.HelpBox("可寻址资源会在使用时下载，建议在外围模块使用，并配合 Loading 表现。", MessageType.Info);
        EditorGUILayout.Space(6);

        selectedTab = GUILayout.Toolbar(selectedTab, TabNames);
        using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition))
        {
            scrollPosition = scroll.scrollPosition;
            EditorGUILayout.Space(8);
            switch ((PathType)selectedTab)
            {
                case PathType.Prefab:
                    DrawDescription("该文件夹下的所有预制体都会单独打成一个 AssetBundle");
                    DrawPathArray(ref prefabPathArr, "预制体资源路径");
                    break;
                case PathType.RootFolder:
                    DrawDescription("该文件夹下的所有子文件夹都会单独打成一个 AssetBundle");
                    DrawPathArray(ref rootFolderPathArr, "文件夹子包路径");
                    break;
                case PathType.SingleBundle:
                    DrawDescription("指定的文件夹会单独打成一个 AssetBundle");
                    DrawBundleFileArray();
                    break;
                case PathType.Source:
                    DrawDescription("指定文件夹下的所有源文件会复制到 AssetBundle 文件夹");
                    DrawPathArray(ref sourceFolderPathArr, "源文件路径");
                    break;
            }
        }

        GUILayout.FlexibleSpace();
        DrawFooter();
    }

    private static void DrawDescription(string text)
    {
        EditorGUILayout.HelpBox(text, MessageType.None);
    }

    private static void DrawPathArray(ref string[] paths, string label)
    {
        paths ??= new string[0];
        GUILayout.Label(label, EditorStyles.boldLabel);
        for (int i = 0; i < paths.Length; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                paths[i] = EditorGUILayout.TextField(paths[i]);
                if (GUILayout.Button("选择", GUILayout.Width(48)))
                {
                    string selected = EditorUtility.OpenFolderPanel(label, ToAbsoluteFolder(paths[i]), string.Empty);
                    if (!string.IsNullOrEmpty(selected))
                        paths[i] = ToProjectPath(selected);
                }
                if (GUILayout.Button("−", GUILayout.Width(28)))
                {
                    ArrayUtility.RemoveAt(ref paths, i);
                    GUIUtility.ExitGUI();
                }
            }
        }
        if (GUILayout.Button("+ 添加路径", GUILayout.Height(26)))
            ArrayUtility.Add(ref paths, string.Empty);
    }

    private void DrawBundleFileArray()
    {
        signFolderPathArr ??= new BundleFileInfo[0];
        GUILayout.Label("单个补丁包路径", EditorStyles.boldLabel);
        for (int i = 0; i < signFolderPathArr.Length; i++)
        {
            signFolderPathArr[i] ??= new BundleFileInfo();
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    signFolderPathArr[i].abName = EditorGUILayout.TextField("Bundle 名称", signFolderPathArr[i].abName);
                    if (GUILayout.Button("删除", GUILayout.Width(48)))
                    {
                        ArrayUtility.RemoveAt(ref signFolderPathArr, i);
                        GUIUtility.ExitGUI();
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    signFolderPathArr[i].bundlePath = EditorGUILayout.TextField("文件夹路径", signFolderPathArr[i].bundlePath);
                    if (GUILayout.Button("选择", GUILayout.Width(48)))
                    {
                        string selected = EditorUtility.OpenFolderPanel("选择 Bundle 文件夹", ToAbsoluteFolder(signFolderPathArr[i].bundlePath), string.Empty);
                        if (!string.IsNullOrEmpty(selected))
                            signFolderPathArr[i].bundlePath = ToProjectPath(selected);
                    }
                }
            }
        }
        if (GUILayout.Button("+ 添加补丁包", GUILayout.Height(26)))
            ArrayUtility.Add(ref signFolderPathArr, new BundleFileInfo());
    }

    private void DrawFooter()
    {
        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(originalModuleName)))
            {
                if (GUILayout.Button("删除配置", GUILayout.Height(38)))
                    DeleteConfiguration();
            }
            if (GUILayout.Button("保存配置", GUILayout.Height(38)))
                SaveConfiguration();
        }
        EditorGUILayout.Space(6);
    }

    private void DeleteConfiguration()
    {
        if (!EditorUtility.DisplayDialog("删除配置", $"确定删除模块“{originalModuleName}”吗？", "删除", "取消"))
            return;
        BuildBundleConfigura.Instance.RemoveModuleByName(originalModuleName);
        CloseAndRefresh();
    }

    private void SaveConfiguration()
    {
        moduleName = moduleName?.Trim();
        if (string.IsNullOrEmpty(moduleName))
        {
            EditorUtility.DisplayDialog("保存失败", "模块名称不能为空。", "确定");
            return;
        }

        BundleModuleData duplicate = BuildBundleConfigura.Instance.GetBundleDataByName(moduleName);
        if (duplicate != null && moduleName != originalModuleName)
        {
            EditorUtility.DisplayDialog("保存失败", "已存在同名模块。", "确定");
            return;
        }

        BundleModuleData data = BuildBundleConfigura.Instance.GetBundleDataByName(originalModuleName) ?? new BundleModuleData();
        data.moduleName = moduleName;
        data.isAddressableAsset = isAddressableAsset;
        data.prefabPathArr = prefabPathArr;
        data.rootFolderPathArr = rootFolderPathArr;
        data.signFolderPathArr = signFolderPathArr;
        data.sourceFolderPathArr = sourceFolderPathArr;
        BuildBundleConfigura.Instance.SaveModuleData(data);
        CloseAndRefresh();
    }

    private void CloseAndRefresh()
    {
        Close();
        BuildWindows.ShowAssetBundleWindow();
    }

    private static string ToAbsoluteFolder(string path)
    {
        if (string.IsNullOrEmpty(path))
            return Application.dataPath;
        if (path == "Assets")
            return Application.dataPath;
        if (path.StartsWith("Assets/"))
            return Application.dataPath + path.Substring("Assets".Length);
        return path;
    }

    private static string ToProjectPath(string absolutePath)
    {
        absolutePath = absolutePath.Replace('\\', '/');
        string dataPath = Application.dataPath.Replace('\\', '/');
        return absolutePath.StartsWith(dataPath) ? "Assets" + absolutePath.Substring(dataPath.Length) : absolutePath;
    }
}
