using System;
using UnityEditor;
using UnityEngine;

public class BundleModuleConfig : EditorWindow
{
    private enum PathType
    {
        Prefab,
        RootFolder,
        SingleBundle,
        SingleFile,
        Source
    }

    [SerializeField] private string moduleName;
    [SerializeField] private string originalModuleName;
    [SerializeField] private bool isAddressableAsset;
    [SerializeField] private BundleModuleRole moduleRole;
    [SerializeField] private PrefabDependencyEntryMode prefabDependencyEntryMode;
    [SerializeField] private string[] prefabPathArr = { "Path..." };
    [SerializeField] private string[] rootFolderPathArr = { };
    [SerializeField] private BundleFileInfo[] signFolderPathArr = { };
    [SerializeField] private string[] sourceFolderPathArr = { };
    [SerializeField] private string[] singleFilePathArr = { };
    [SerializeField] private int selectedTab;
    [SerializeField] private Vector2 scrollPosition;

    private static readonly string[] TabNames = { "预制体分包", "子目录分包", "整目录打包", "逐文件分包", "源文件复制" };

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
            //00 新模块默认是普通业务模块，Shared 必须由开发者主动选择。
            moduleRole = BundleModuleRole.Business;
            //00 新模块默认只开放 Prefab，避免无意扩大可主动加载资源集合。
            prefabDependencyEntryMode = PrefabDependencyEntryMode.PrefabOnly;
            prefabPathArr = new[] { "Path..." };
            rootFolderPathArr = new string[0];
            signFolderPathArr = new BundleFileInfo[0];
            sourceFolderPathArr = new string[0];
            singleFilePathArr = new string[0];
        }
        else
        {
            moduleName = data.moduleName;
            isAddressableAsset = data.isAddressableAsset;
            //00 旧配置没有该字段时读取枚举零值 Business。
            moduleRole = data.moduleRole;
            //00 旧序列化配置缺少字段时自动得到枚举零值，兼容现有工程。
            prefabDependencyEntryMode = data.prefabDependencyEntryMode;
            prefabPathArr = data.prefabPathArr ?? new string[0];
            rootFolderPathArr = data.rootFolderPathArr ?? new string[0];
            signFolderPathArr = data.signFolderPathArr ?? new BundleFileInfo[0];
            sourceFolderPathArr = data.sourceFolderPathArr ?? new string[0];
            singleFilePathArr = data.singleFilePathArr ?? new string[0];
        }
    }

    private void OnGUI()
    {
        //00 独立配置窗口可能在主构建中心尚未打开时启动，必须主动初始化共享 ZMAsset 样式。
        ZMBuildStyles.Ensure();
        EditorGUILayout.Space(8);
        moduleName = EditorGUILayout.TextField("资源模块名称", moduleName);
        //00 兼容窗口复用主抽屉的紧凑角色控件，避免两个入口行为不一致。
        moduleRole = BundleModuleRoleUi.DrawSelector(moduleRole);
        //00 角色约束紧邻控件展示，不隐藏关键架构行为。
        GUILayout.Label(
            moduleRole == BundleModuleRole.Shared
                ? "共享模块只能被业务模块依赖，当前版本最多允许一个。"
                : "业务模块不能互相引用，但可以依赖唯一共享模块。",
            ZMBuildStyles.SettingsFieldHint,
            GUILayout.MinHeight(22));
        isAddressableAsset = EditorGUILayout.Toggle("是否为远端资源", isAddressableAsset);
        EditorGUILayout.HelpBox("远端资源会在首次使用时按需下载，本地文件校验有效时直接复用。", MessageType.Info);
        EditorGUILayout.Space(6);

        selectedTab = GUILayout.Toolbar(selectedTab, TabNames);
        using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition))
        {
            scrollPosition = scroll.scrollPosition;
            EditorGUILayout.Space(8);
            switch ((PathType)selectedTab)
            {
                case PathType.Prefab:
                    DrawDescription("目录中的每个 Prefab 分别生成一个独立 AssetBundle");
                    //00 一个策略统一控制当前模块 Prefab Tab 中配置的全部搜索目录。
                    DrawPrefabDependencyMode();
                    DrawPathArray(ref prefabPathArr, "预制体资源路径");
                    break;
                case PathType.RootFolder:
                    DrawDescription("所选目录下的每个一级子目录分别生成一个 AssetBundle");
                    DrawPathArray(ref rootFolderPathArr, "子目录分包路径");
                    break;
                case PathType.SingleBundle:
                    DrawDescription("将指定目录整体生成一个 AssetBundle，并可自定义 Bundle 名称");
                    DrawBundleFileArray();
                    break;
                case PathType.SingleFile:
                    DrawDescription("目录中的每个可打包文件分别生成一个独立 AssetBundle");
                    DrawPathArray(ref singleFilePathArr, "逐文件分包路径");
                    break;
                case PathType.Source:
                    DrawDescription("不生成 AssetBundle，直接将目录中的原文件复制到资源输出目录");
                    DrawPathArray(ref sourceFolderPathArr, "源文件复制路径");
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

    /// <summary>
    /// 00 在旧版独立配置窗口中复用 ZMAsset 暗色下拉控件，避免两个配置入口行为不一致。
    /// </summary>
    private void DrawPrefabDependencyMode()
    {
        //00 标题和紧凑双段控件绘制为同一行，独立窗口与主抽屉保持完全一致。
        prefabDependencyEntryMode = PrefabDependencyEntryModeUi.DrawSelector(prefabDependencyEntryMode);
        //00 动态提示说明 Entry 行为，不暗示 Bundle 分组发生变化。
        GUILayout.Label(
            prefabDependencyEntryMode == PrefabDependencyEntryMode.PrefabOnly
                ? "仅 Prefab 可被业务代码按路径直接加载，递归依赖由 Bundle 内部使用。"
                : "Prefab 与本模块拥有的递归依赖都可以按路径直接加载，Bundle 分组保持不变。",
            ZMBuildStyles.SettingsFieldHint,
            GUILayout.MinHeight(24));
        //00 与后续路径列表只保留轻量间距，避免这个辅助选项形成独立大区块。
        GUILayout.Space(5);
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
        GUILayout.Label("整目录打包路径", EditorStyles.boldLabel);
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
        //00 与主抽屉执行同一单 Shared 门禁。
        if (!BundleModuleRoleUi.ValidateSingleShared(
                BuildBundleConfigura.Instance,
                data,
                moduleRole,
                out string roleError))
        {
            EditorUtility.DisplayDialog("保存失败", roleError, "确定");
            return;
        }
        //00 门禁成功后才写入角色，保存失败不会污染内存配置。
        data.moduleRole = moduleRole;
        data.isAddressableAsset = isAddressableAsset;
        //00 保存模块级统一 Prefab 资源加载策略，旧路径数组结构保持不变。
        data.prefabDependencyEntryMode = prefabDependencyEntryMode;
        data.prefabPathArr = prefabPathArr;
        data.rootFolderPathArr = rootFolderPathArr;
        data.signFolderPathArr = signFolderPathArr;
        data.sourceFolderPathArr = sourceFolderPathArr;
        //00 与主抽屉保持一致，过滤空白路径项后再持久化逐文件分包目录。
        data.singleFilePathArr = RemoveEmptyPaths(singleFilePathArr);
        BuildBundleConfigura.Instance.SaveModuleData(data);
        CloseAndRefresh();
    }

    private void CloseAndRefresh()
    {
        Close();
        BuildWindows.ShowAssetBundleWindow();
    }

    /// <summary>
    /// 00 与主抽屉共用同一过滤语义，移除空白配置项。
    /// </summary>
    private static string[] RemoveEmptyPaths(string[] paths)
    {
        return Array.FindAll(paths ?? new string[0], path => !string.IsNullOrWhiteSpace(path));
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
