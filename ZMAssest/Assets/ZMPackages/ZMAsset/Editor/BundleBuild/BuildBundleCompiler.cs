/*---------------------------------------------------------------------------------------------------------------------------------------------
*
* Title: ZMAsset
*
* Description: 可视化多模块打包器、多模块热更、多线程下载、多版本热更、多版本回退、加密、解密、内嵌、解压、内存引用计数、大型对象池、AssetBundle加载、Editor加载
*
* Author: ZM
*
* Date: 2023.4.13
*
* Modify: 
------------------------------------------------------------------------------------------------------------------------------------------------*/
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace ZM.ZMAsset
{
    public enum BuildType
    {
        AssetBundle,
        HotPatch, //热更补丁
    }

    public class BuildBundleCompiler
    {
        /// <summary>
        /// 自动共享依赖 Bundle 的固定名称段；最终名称只包含稳定 CRC 和分片编号。
        /// 例如 Hall_shared_1234abcd_01.uab，缩短文件名的同时保证重复构建结果稳定。
        /// </summary>
        private const string SharedDependencyBundleSuffix = "shared";

        /// <summary>
        /// 单个自动 Shared Bundle 允许容纳的源资源大小上限，当前为 32 MiB。
        /// 这里限制的是打包前源文件大小之和，不是压缩后的 uab 大小；它用于避免公共依赖无限集中到一个大包。
        /// </summary>
        private const long AutoSharedBundleSourceSizeLimit = 32L * 1024L * 1024L;
        /// <summary>
        /// 更新公告
        /// </summary>
        //00 每个编译器实例只服务一个 ModuleBuildContext，禁止任何构建状态回退到静态共享字段。
        private readonly ModuleBuildContext mContext;

        //00 以下兼容属性只用于降低 3A0 迁移噪声；实际状态全部存放在 mContext 中。
        private string mUpdateNotice { get => mContext.UpdateNotice; set => mContext.UpdateNotice = value; }
        /// <summary>
        /// 热更补丁版本
        /// </summary>
        private int mHotPatchVersion { get => mContext.HotPatchVersion; set => mContext.HotPatchVersion = value; }
        /// <summary>
        /// 热更应用版本
        /// </summary>
        private string mHotAppVersion { get => mContext.HotAppVersion; set => mContext.HotAppVersion = value; }
        /// <summary>
        /// 打包类型
        /// </summary>
        private BuildType mBuildType { get => mContext.BuildType; set => mContext.BuildType = value; }
        /// <summary>
        /// 打包模块数据
        /// </summary>
        private BundleModuleData mBuildModuleData { get => mContext.ModuleData; set => mContext.ModuleData = value; }
        /// <summary>
        /// 打包模块类型
        /// </summary>
        private string _mBundleModuleName { get => mContext.ModuleName; set => mContext.ModuleName = value; }
        /// <summary>
        /// 所有AssetBundle文件路径列表
        /// </summary>
        private List<string> mAllBundlePathList => mContext.AllBundlePaths;

        /// <summary>
        /// 所有文件夹的Bundle列表
        /// </summary>
        private Dictionary<string, List<string>> mAllFolderBundleDic => mContext.FolderBundles;

        /// <summary>
        /// 所有预制体的Budle字典
        /// </summary>
        private Dictionary<string, List<string>> mAllPrefabsBundleDic => mContext.PrefabBundles;

        /// <summary>
        /// 00 单文件包 Bundle 字典：目录下每个可打包文件独占一个 Bundle。
        /// </summary>
        private Dictionary<string, List<string>> mSingleFileBundleDic => mContext.SingleFileBundles;
        /// <summary>
        /// 要打包的Bundle资产数组
        /// </summary>
        private List<AssetBundleBuild> mBundleBuildList => mContext.BundleBuilds;

        /// <summary>
        /// 由打包规则直接选中的可加载入口路径，
        /// Entry 来源必须在规则收集阶段保留，写配置时无法再从合并后的构建列表反推出资源来源。
        /// </summary>
        private HashSet<string> mExplicitEntryPathSet => mContext.ExplicitEntryPaths;

        /// <summary>
        /// 源文件规则的稳定快照
        /// 写配置和构建后复制必须消费同一份快照，避免两次扫描之间文件变化导致配置与产物不一致。
        /// </summary>
        private List<SourceBuildEntry> mSourceEntryList => mContext.SourceEntries;

        /// <summary>
        /// AssetBundle文件输出路径
        /// </summary>
        private string mBundleOutPutPath { get => mContext.BundleOutputPath; set => mContext.BundleOutputPath = value; }

        /// <summary>
        /// 热更资源文件输出路径
        /// </summary>
        private string mHotAssetsOutPutPath { get => mContext.HotAssetsOutputPath; set => mContext.HotAssetsOutputPath = value; }
        /// <summary>
        /// 框架Resources路径
        /// </summary>
        private static string mResourcesPath { get { return Application.dataPath +"/"+BundleSettings.Instance.ZMAssetRootPath+ "/Resources/"; } }
        /// <summary>
        /// 打包平台
        /// </summary>
        private UnityEditor.BuildTarget mBuildTarget { get => mContext.BuildTarget; set => mContext.BuildTarget = value; }
        /// <summary>
        /// 配置文件路径
        /// </summary>
        private string mConfgDataPath { get => mContext.ConfigDataPath; set => mContext.ConfigDataPath = value; }

        /// <summary>
        /// 00 创建只操作指定构建上下文的编译器实例。
        /// </summary>
        internal BuildBundleCompiler(ModuleBuildContext context)
        {
            //00 空上下文会重新引入隐式全局状态，因此在边界处立即拒绝。
            mContext = context ?? throw new ArgumentNullException(nameof(context));
        }
        /// <summary>
        /// 打包AssetBundle
        /// </summary>
        /// <param name="moduleData">资源模块配置数据</param>
        /// <param name="buildType">打包类型</param>
        /// <param name="hotPatchVersion">热更补丁版本</param>
        /// <param name="updateNotice">更新公告</param>
        public static void BuildAssetBundle(BundleModuleData moduleData, BuildType buildType = BuildType.AssetBundle, int hotPatchVersion = 0,string hotAppVersion="0.0.0", string updateNotice = "",UnityEditor.BuildTarget buildTarget = UnityEditor.BuildTarget.NoTarget)
        {
            IEnumerator routine = BuildAssetBundleStaged(moduleData, buildType, hotPatchVersion, hotAppVersion, updateNotice, buildTarget);
            while (routine.MoveNext()) { }
        }

        public static IEnumerator BuildAssetBundleStaged(BundleModuleData moduleData, BuildType buildType = BuildType.AssetBundle, int hotPatchVersion = 0,string hotAppVersion="0.0.0", string updateNotice = "",UnityEditor.BuildTarget buildTarget = UnityEditor.BuildTarget.NoTarget)
        {
            //00 保留旧公开 API，但统一转交多模块编排器；单模块调用方无需修改既有调用代码。
            //00 所有调用均经过同一 BuildPipeline、临时目录与原子发布，外部 Editor 脚本不能绕回旧发布流程。
            return MultiModuleBuildOrchestrator.BuildStaged(new[] { moduleData }, buildType, hotPatchVersion, hotAppVersion, updateNotice, buildTarget);
        }

        /// <summary>
        /// 00 为统一多模块 BuildPipeline 收集一个模块的完整构建输入，但不写入发布目录也不调用 Unity 构建。
        /// </summary>
        internal void PrepareForOrchestration(
            BundleModuleData moduleData,
            ModuleOwnershipAnalysis ownershipAnalysis,
            BuildType buildType,
            int hotPatchVersion,
            string hotAppVersion,
            string updateNotice,
            UnityEditor.BuildTarget buildTarget,
            string sharedStagingPath)
        {
            //00 编排器必须先完成全局归属分析；缺失时无法安全排除外部 Shared 依赖。
            mContext.OwnershipAnalysis = ownershipAnalysis ?? throw new ArgumentNullException(nameof(ownershipAnalysis));
            //00 共享 staging 只承载 Unity 原始输出，真实模块目录要等全部校验成功后才切换。
            if (!Initlization(
                    moduleData,
                    buildType,
                    hotPatchVersion,
                    hotAppVersion,
                    updateNotice,
                    buildTarget,
                    sharedStagingPath,
                    false))
                throw new InvalidOperationException($"模块 {moduleData?.moduleName} 初始化失败。");
            //00 各规则仍按原顺序收集，保证单模块输入列表和既有算法一致。
            BuildAllFolder();
            BuildRootSubFolder();
            BuildAllPrefabs();
            BuildAllSingleFiles();
            //00 统一构建只有在 Shared 上下文接管全部外部引用后，才能真正形成跨模块 Bundle 依赖。
            BuildExternallyConsumedSharedAssets();
            CollectSourceEntries();
            //00 配置 Bundle 也必须先进入列表，之后全局映射才能包含每个模块的配置文件。
            GenerateBundleBuilder();
        }

        /// <summary>
        /// 00 使用统一资源位置映射写入当前模块配置。
        /// </summary>
        internal void WriteConfigForOrchestration(
            IReadOnlyDictionary<string, AssetBundleBuildLocation> globalAssetLocations,
            IReadOnlyList<string> moduleDependencies)
        {
            //00 配置写入只消费已经冻结的全局映射，不再自行猜测外部依赖归属。
            WriteAssetBundleConfig(globalAssetLocations, moduleDependencies);
        }

        /// <summary>
        /// 00 暴露只读上下文引用给统一编排器；集合修改仍由当前编译器方法负责。
        /// </summary>
        internal ModuleBuildContext Context => mContext;

        /// <summary>
        /// 00 从 Unity 的统一原始输出中提取当前模块拥有的 Bundle 与源文件，并在独立 staging 中完成加密。
        /// 00 该方法不会触碰正式发布目录，编排器只有在所有模块均通过校验后才执行原子切换。
        /// </summary>
        internal void MaterializeOrchestratedOutput(string rawOutputPath, string moduleStagingPath)
        {
            //00 两个路径都是构建事务边界，空路径可能导致误操作工程目录，因此立即拒绝。
            if (string.IsNullOrWhiteSpace(rawOutputPath))
                throw new ArgumentException("统一构建原始输出路径不能为空。", nameof(rawOutputPath));
            if (string.IsNullOrWhiteSpace(moduleStagingPath))
                throw new ArgumentException("模块 staging 路径不能为空。", nameof(moduleStagingPath));

            //00 规范化路径后再访问文件系统，避免相对路径随 Unity 当前目录变化。
            string normalizedRawPath = Path.GetFullPath(rawOutputPath);
            string normalizedModulePath = Path.GetFullPath(moduleStagingPath);
            //00 原始输出缺失说明 BuildPipeline 没有产生可发布结果，不能继续生成空模块。
            if (!Directory.Exists(normalizedRawPath))
                throw new DirectoryNotFoundException($"找不到统一构建原始输出：{normalizedRawPath}");

            //00 staging 由本次事务独占；清理的是经过编排器验证的精确临时目录，不涉及历史正式产物。
            if (Directory.Exists(normalizedModulePath)) Directory.Delete(normalizedModulePath, true);
            //00 每个模块使用独立目录，后续可以逐模块比较、校验并原子切换。
            Directory.CreateDirectory(normalizedModulePath);

            //00 一个 Bundle 名称只复制一次，防止异常重复 Build 项覆盖后掩盖构建输入问题。
            HashSet<string> copiedBundleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetBundleBuild bundleBuild in mBundleBuildList)
            {
                //00 Unity Bundle 名称为空属于内部构建列表错误，必须给出模块上下文。
                if (string.IsNullOrWhiteSpace(bundleBuild.assetBundleName))
                    throw new InvalidOperationException($"模块 {_mBundleModuleName} 的统一构建列表包含空 Bundle 名称。");
                //00 同名项只对应一个物理文件；重复名称会让模块边界不确定，因此拒绝而不是静默覆盖。
                if (!copiedBundleNames.Add(bundleBuild.assetBundleName))
                    throw new InvalidOperationException($"模块 {_mBundleModuleName} 重复生成 Bundle：{bundleBuild.assetBundleName}");

                //00 只提取本上下文声明的精确文件，Unity 根 Manifest 和其他模块 Bundle 不会混入。
                string sourcePath = AssetBundleNameValidator.ResolveChildPath(
                    normalizedRawPath,
                    bundleBuild.assetBundleName,
                    $"模块 {_mBundleModuleName} 的原始 Bundle 输出");
                string destinationPath = AssetBundleNameValidator.ResolveChildPath(
                    normalizedModulePath,
                    bundleBuild.assetBundleName,
                    $"模块 {_mBundleModuleName} 的 staging Bundle 输出");
                //00 缺失任何声明 Bundle 都意味着统一构建不完整，禁止发布部分结果。
                if (!File.Exists(sourcePath))
                    throw new FileNotFoundException(
                        $"模块 {_mBundleModuleName} 的 Bundle 未由 Unity 生成：{bundleBuild.assetBundleName}",
                        sourcePath);
                //00 staging 是新目录，可直接复制并保留最终文件名。
                File.Copy(sourcePath, destinationPath, false);
            }

            //00 源文件沿用收集阶段冻结的路径与名称，不重新扫描目录。
            foreach (SourceBuildEntry sourceEntry in mSourceEntryList)
            {
                //00 源文件在构建期间被删除时明确失败，避免配置仍指向不存在的产物。
                if (!File.Exists(sourceEntry.FullPath))
                    throw new FileNotFoundException(
                        $"模块 {_mBundleModuleName} 的源文件在构建期间丢失：{sourceEntry.AssetPath}",
                        sourceEntry.FullPath);
                //00 输出名称已在 CollectSourceEntries 中做忽略大小写唯一校验，可以安全复制。
                File.Copy(
                    sourceEntry.FullPath,
                    AssetBundleNameValidator.ResolveChildPath(
                        normalizedModulePath,
                        sourceEntry.OutputFileName,
                        $"模块 {_mBundleModuleName} 的源文件输出"),
                    false);
            }

            //00 后续加密与清单生成继续复用当前编译器上下文，但只指向该模块 staging。
            mBundleOutPutPath = normalizedModulePath.Replace('\\', '/').TrimEnd('/') + "/";
            //00 加密必须发生在计算 MD5 前，保证热更清单描述的是最终发布字节。
            EncryptAllBundle();
        }

        /// <summary>
        /// 00 根据当前模块元数据和指定 staging 文件生成热更清单字节，但不直接写入任何正式目录。
        /// </summary>
        internal byte[] CreateManifestBytesForOrchestration(string contentPath)
        {
            //00 编排器负责决定清单最终落点，本方法只产生可原子发布的不可变字节。
            HotAssetsManifest manifest = CreateHotAssetsManifest(contentPath);
            //00 继续使用既有缩进 JSON 格式，避免无意义的历史清单文本差异。
            string json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            //00 现有 FileHelper 写入使用 UTF-8，统一构建保持相同编码。
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// 00 返回当前模块和平台对应的固定热更清单文件名。
        /// </summary>
        internal string GetManifestFileNameForOrchestration()
        {
            return GetCurrentManifestFileName();
        }

        /// <summary>
        /// 根据冻结的 Unity 构建目标返回当前模块的热更清单名称。
        /// 统一通过映射器转换，避免将 Unity 枚举数值直接写进运行时文件名；
        /// StandaloneLinux64 的“_24.json”属于已发布的冻结兼容协议，映射器会明确保留该历史后缀。
        /// </summary>
        private string GetCurrentManifestFileName()
        {
            return BuildTargetPlatformMapper.GetHotManifestName(_mBundleModuleName, mBuildTarget);
        }
        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="moduleData"></param>
        /// <param name="buildType"></param>
        /// <param name="hotPatchVersion"></param>
        /// <param name="updateNotice"></param>
        private bool Initlization(
            BundleModuleData moduleData,
            BuildType buildType = BuildType.AssetBundle,
            int hotPatchVersion = 0,
            string hotAppVersion = "0.0.0",
            string updateNotice = "",
            UnityEditor.BuildTarget buildTarget = UnityEditor.BuildTarget.NoTarget,
            string bundleOutputPathOverride = null,
            bool cleanBundleOutput = true)
        {
            //清理数据以防下次打包时有数据残留
            //00 只清理当前上下文的扫描结果，不再触碰其他模块正在使用的数据。
            mContext.ResetCollectedData();
            
            mBuildTarget = buildTarget== UnityEditor.BuildTarget.NoTarget? EditorUserBuildSettings.activeBuildTarget: buildTarget;
            mBuildType = buildType;
            mUpdateNotice = updateNotice;
            mBuildModuleData = moduleData;
            mHotPatchVersion = hotPatchVersion;
            mHotAppVersion = hotAppVersion;
            try
            {
                _mBundleModuleName = moduleData.moduleName;
            }
            catch (Exception)
            {
                Debug.LogError($"{moduleData.moduleName} Enum Not find! Plase Gennerate Enum : Menu ZMFrame-GeneratorModuleEnum");
                return false;
            }

            mConfgDataPath = $"{Application.dataPath}/GameData/{_mBundleModuleName.Replace("Game", "")}/Data/ABData/";
            //00 单模块旧入口继续使用原目录；统一构建则显式传入共享 staging 根目录。
            mBundleOutPutPath = string.IsNullOrWhiteSpace(bundleOutputPathOverride)
                ? Application.dataPath + "/../AssetBundle/" + _mBundleModuleName + "/" + mBuildTarget + "/"
                : Path.GetFullPath(bundleOutputPathOverride).Replace('\\', '/').TrimEnd('/') + "/";
            mHotAssetsOutPutPath =Application.dataPath + "/../HotAssets/" + _mBundleModuleName + "/"+mHotAppVersion  +"/"+mHotPatchVersion+"/"+ mBuildTarget + "/";
            //00 只有兼容单模块路径允许在构建前清理；统一构建 staging 由编排器独占和管理。
            if (cleanBundleOutput) FileHelper.DeleteFolder(mBundleOutPutPath);
            Directory.CreateDirectory(mBundleOutPutPath);
            return true;    

        }
        /// <summary>
        /// 打包所有文件夹AssetBundle
        /// </summary>
        private void BuildAllFolder()
        {
            if (mBuildModuleData.signFolderPathArr == null || mBuildModuleData.signFolderPathArr.Length == 0)
            {
                return;
            }
            for (int i = 0; i < mBuildModuleData.signFolderPathArr.Length; i++)
            {
                //获取文件夹路径
                BundleFileInfo bundleFileInfo = mBuildModuleData.signFolderPathArr[i];
                if (bundleFileInfo == null || string.IsNullOrWhiteSpace(bundleFileInfo.bundlePath)) continue;
                string path = ValidateConfiguredDirectory(bundleFileInfo.bundlePath, "文件夹包目录");
                ZMBuildProgress.Report("分析文件夹包", mBuildModuleData.signFolderPathArr[i].abName, .10f + .10f * i / mBuildModuleData.signFolderPathArr.Length);
 
                DirectoryInfo info = new DirectoryInfo(path);
                FileInfo[] pathArr = info.GetFiles("*", SearchOption.AllDirectories); ;
                foreach (var fileInfo in pathArr)
                {
                    string filePath = ToAssetPath(fileInfo.FullName);
                    
                    if (filePath.EndsWith(".cs") || filePath.EndsWith(".meta")) continue;

                    RegisterExplicitEntry(filePath);
                    mAllBundlePathList.Add(filePath);
                    //获取以模块名+_+AbName的格式的AssetBundle包名
                    string bundleName = GenerateBundleName(mBuildModuleData.signFolderPathArr[i].abName);
                    if (!mAllFolderBundleDic.ContainsKey(bundleName))
                    {
                        mAllFolderBundleDic.Add(bundleName, new List<string> { filePath });
                    }
                    else
                    {
                        mAllFolderBundleDic[bundleName].Add(filePath);
                    }
                }
            }
        }

        /// <summary>
        /// 打包父文件夹下的所有子文件夹
        /// </summary>
        private void BuildRootSubFolder()
        {
            //检测父文件夹是否有配置，如果没配置就直接跳过
            if (mBuildModuleData.rootFolderPathArr == null || mBuildModuleData.rootFolderPathArr.Length == 0)
            {
                return;
            }

            for (int i = 0; i < mBuildModuleData.rootFolderPathArr.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(mBuildModuleData.rootFolderPathArr[i])) continue;
                string path = ValidateConfiguredDirectory(mBuildModuleData.rootFolderPathArr[i], "子文件夹 Bundle 根目录");
                //获取父文夹的所有的子文件夹
                string[] folderArr = Directory.GetDirectories(path);
                foreach (var item in folderArr)
                {
                    path = item.Replace(@"\", "/");
                    int nameIndex = path.LastIndexOf("/") + 1;
                    //获取文件夹同名的AssetBundle名称
                    string bundleName = GenerateBundleName(path.Substring(nameIndex, path.Length - nameIndex));
                    //处理子文件夹资源的代码
                    string[] filePathArr = Directory.GetFiles(path, "*",SearchOption.AllDirectories);
                    foreach (var filePath in filePathArr)
                    {
                        //过滤.meta文件
                        if (!filePath.EndsWith(".meta"))
                        {
                            string abFilePath = ToAssetPath(filePath);
                            if (abFilePath.EndsWith(".cs")) continue;
                            RegisterExplicitEntry(abFilePath);
                            if (!IsRepeatBundleFile(abFilePath))
                            {
                                mAllBundlePathList.Add(abFilePath);
                                if (!mAllFolderBundleDic.ContainsKey(bundleName))
                                {
                                    mAllFolderBundleDic.Add(bundleName, new List<string> { abFilePath });
                                }
                                else
                                {
                                    mAllFolderBundleDic[bundleName].Add(abFilePath);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 打包指定文件夹下的所有预制体
        /// </summary>
        private void BuildAllPrefabs()
        {
            //00 未配置 Prefab 搜索目录时，本阶段没有任何 Entry 和依赖需要分析，直接结束。
            if (mBuildModuleData.prefabPathArr == null || mBuildModuleData.prefabPathArr.Length == 0)
            {
                return;
            }
            //00 单独保存通过校验的 Unity 资源目录；不让空字符串进入 AssetDatabase 查询。
            List<string> prefabSearchFolders = new List<string>();
            foreach (string configuredPath in mBuildModuleData.prefabPathArr)
            {
                //00 配置数组可能来自旧序列化数据，空项按“未配置”处理，而不是访问文件系统。
                if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                //00 先验证真实目录，再统一转换成 Assets/... 形式，保证后续路径比较使用同一格式。
                prefabSearchFolders.Add(ToAssetPath(ValidateConfiguredDirectory(configuredPath, "Prefab 搜索目录")));
            }
            //00 全部配置项都为空时，不调用 AssetDatabase.FindAssets，避免无意扫描整个工程。
            if (prefabSearchFolders.Count == 0) return;

            //00 第一阶段：只收集并排序全部显式 Prefab Entry，暂时不决定任何依赖属于哪个 Bundle。
            //00 GUID 返回顺序不属于 Unity 稳定契约，因此必须转换为路径、去重并排序。
            List<string> prefabPaths = AssetDatabase.FindAssets("t:Prefab", prefabSearchFolders.ToArray())
                //00 GUID 只是编辑器内部标识，构建配置和 CRC 必须使用资源路径。
                .Select(AssetDatabase.GUIDToAssetPath)
                //00 把反斜杠、前导分隔符等差异统一成框架认可的资源路径。
                .Select(NormalizeAssetPath)
                //00 忽略 Unity 无法解析出的空路径，防止空字符串进入 BundleBuild.assetNames。
                .Where(path => !string.IsNullOrWhiteSpace(path))
                //00 多个搜索目录可能覆盖同一 Prefab，按完整路径去重后只生成一个 Entry。
                .Distinct(StringComparer.Ordinal)
                //00 使用稳定排序消除 FindAssets 顺序变化带来的“第一个 Prefab 占有公共依赖”问题。
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
            //00 搜索目录存在但没有 Prefab 时，本阶段安全结束。
            if (prefabPaths.Count == 0) return;

            //00 快速判断某个依赖是否本身也是显式 Prefab Entry；显式 Entry 必须拥有独立 Bundle。
            HashSet<string> prefabPathSet = new HashSet<string>(prefabPaths, StringComparer.Ordinal);
            //00 文件夹规则先执行，因此这里保存它们已经占有的资源；自动 Shared 不能抢走用户显式归属。
            HashSet<string> preassignedPaths = new HashSet<string>(mAllBundlePathList, StringComparer.Ordinal);
            //00 保存“Prefab 路径 -> 最终 Bundle 名”，第二阶段分配资源时直接复用，避免重复计算。
            Dictionary<string, string> prefabBundleNames = new Dictionary<string, string>(StringComparer.Ordinal);
            //00 保存每个 Prefab 的完整递归依赖快照；统计和最终分配必须消费同一份数据。
            Dictionary<string, List<string>> prefabDependencies = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            //00 记录“尚未归属资源 -> 使用它的 Prefab 集合”；消费者集合既决定是否共享，也决定 Shared 分组边界。
            Dictionary<string, HashSet<string>> dependencyConsumers =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            //00 最终 Bundle 名会转成小写，名称冲突检查必须忽略大小写，避免不同源码名落到同一输出文件。
            HashSet<string> occupiedBundleNames = new HashSet<string>(
                mAllFolderBundleDic.Keys,
                StringComparer.OrdinalIgnoreCase);
            //00 若文件夹规则本身已有大小写冲突，现在就失败，禁止到写磁盘时才互相覆盖。
            if (occupiedBundleNames.Count != mAllFolderBundleDic.Count)
                throw new InvalidOperationException($"模块 {_mBundleModuleName} 存在忽略大小写后重复的文件夹 Bundle 名称。");

            //00 第一阶段继续：为每个 Prefab 建立稳定 Bundle 名、依赖快照和完整消费者集合。
            foreach (string prefabPath in prefabPaths)
            {
                //00 Prefab Bundle 沿用“模块名_Prefab名”规则，保持已有资源路径和热更命名兼容。
                string bundleName = GenerateBundleName(Path.GetFileNameWithoutExtension(prefabPath));
                //00 两个同名 Prefab 即使位于不同目录，最终输出名仍相同，因此必须在构建前明确拒绝。
                if (!occupiedBundleNames.Add(bundleName))
                    throw new InvalidOperationException(
                        $"模块 {_mBundleModuleName} 存在重复 Bundle 名称：{bundleName}，请调整同名 Prefab 或文件夹 Bundle 名称。");

                //00 保存路径和 Bundle 名的稳定映射，供第二阶段生成 mAllPrefabsBundleDic。
                prefabBundleNames.Add(prefabPath, bundleName);

                //00 true 表示递归获取材质、纹理、嵌套 Prefab 等完整依赖，才能识别真正共享的传递依赖。
                List<string> dependencies = AssetDatabase.GetDependencies(prefabPath, true)
                    //00 Unity 返回的路径也统一规范化，确保相同资源能被 HashSet 正确识别。
                    .Select(NormalizeAssetPath)
                    //00 只保留 Assets 下可进入 AssetBundle 的资源，排除脚本、meta 和内置/包路径。
                    .Where(IsBundleableAssetPath)
                    //00 同一个 Prefab 对同一资源只计数一次，避免内部多次引用把使用次数虚增。
                    .Distinct(StringComparer.Ordinal)
                    //00 依赖列表排序后，生成的 AssetBundleBuild 和 JSON 配置都保持可复现。
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList();
                //00 根据 Prefab Tab 的模块级统一策略登记公开 Entry；该策略不改变任何 Bundle 物理分组。
                foreach (string loadableEntryPath in CollectPrefabLoadableEntryPaths(
                             prefabPath,
                             dependencies,
                             mBuildModuleData.prefabDependencyEntryMode))
                {
                    //00 PrefabAndDependencies 只能公开当前模块物理拥有的资源；外部 Shared 仍由 Shared 配置公开。
                    if (IsOwnedByCurrentModule(loadableEntryPath)) RegisterExplicitEntry(loadableEntryPath);
                }
                //00 固定本轮构建的依赖快照；后面不再重新调用 AssetDatabase.GetDependencies。
                prefabDependencies.Add(prefabPath, dependencies);

                foreach (string dependencyPath in dependencies)
                {
                    //00 外部 Shared 资源将在统一 BuildPipeline 的 Shared 上下文中显式分配，Business 不能再次收纳。
                    if (!IsOwnedByCurrentModule(dependencyPath)) continue;
                    //00 显式 Prefab Entry 必须保留独立 Bundle；用户文件夹规则也拥有更高归属优先级。
                    if (prefabPathSet.Contains(dependencyPath) || preassignedPaths.Contains(dependencyPath)) continue;
                    //00 第一次遇到资源时创建消费者集合，后续相同资源复用同一个集合。
                    if (!dependencyConsumers.TryGetValue(dependencyPath, out HashSet<string> consumers))
                    {
                        //00 使用 Ordinal 比较完整资源路径，使 Windows 与其他平台上的构建分组规则一致。
                        consumers = new HashSet<string>(StringComparer.Ordinal);
                        //00 保存新集合后，后续 Prefab 才能向同一个资源条目持续追加消费者。
                        dependencyConsumers.Add(dependencyPath, consumers);
                    }
                    //00 同一个 Prefab 即使通过多条依赖链到达该资源，也只能算一个消费者。
                    consumers.Add(prefabPath);
                }
            }

            //00 第二阶段：把“至少被两个 Prefab 使用”的未归属资源判定为模块共享依赖。
            HashSet<string> sharedDependencyPaths = new HashSet<string>(
                dependencyConsumers
                    //00 消费者只有一个的资源仍跟随唯一 Prefab，避免所有资源都被塞进 Shared Bundle。
                    .Where(pair => pair.Value.Count > 1)
                    .Select(pair => pair.Key),
                StringComparer.Ordinal);
            //00 没有公共依赖时不生成任何空 Shared Bundle，保持旧模块产物数量不变。
            if (sharedDependencyPaths.Count > 0)
            {
                //00 相同消费者集合的资源进入同一组；A+B 公用资源不会与 D+E 公用资源混成一个大包。
                Dictionary<string, List<string>> sharedDependenciesByConsumerSignature =
                    new Dictionary<string, List<string>>(StringComparer.Ordinal);
                //00 路径稳定排序后再分组，使字典插入顺序和后续日志在不同机器上都可复现。
                foreach (string dependencyPath in sharedDependencyPaths.OrderBy(path => path, StringComparer.Ordinal))
                {
                    //00 消费者路径先排序再连接；签名与 HashSet 的内部枚举顺序无关。
                    string consumerSignature = CreateConsumerSignature(dependencyConsumers[dependencyPath]);
                    //00 首次遇到该消费者组合时创建独立资源组。
                    if (!sharedDependenciesByConsumerSignature.TryGetValue(consumerSignature, out List<string> groupDependencies))
                    {
                        //00 每个签名拥有自己的列表，后续会独立执行 32 MiB 稳定分片。
                        groupDependencies = new List<string>();
                        //00 保存签名到资源组的映射，确保同一消费者组合汇总到一起。
                        sharedDependenciesByConsumerSignature.Add(consumerSignature, groupDependencies);
                    }
                    //00 当前依赖只加入与其消费者集合完全一致的组。
                    groupDependencies.Add(dependencyPath);
                }

                //00 签名排序后逐组生成 Bundle，避免 Dictionary 枚举顺序影响构建列表顺序。
                foreach (KeyValuePair<string, List<string>> sharedGroup in
                         sharedDependenciesByConsumerSignature.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    //00 同组资源按完整路径排序后使用贪心算法分片，输入不变时分片边界必然不变。
                    List<List<string>> shards = SplitSharedDependenciesBySourceSize(sharedGroup.Value);
                    //00 分片编号从 1 开始，并始终保留两位编号；即便目前只有一片，未来扩容也不会重命名第一片。
                    for (int shardIndex = 0; shardIndex < shards.Count; shardIndex++)
                    {
                        //00 名称只包含完整消费者签名 CRC 和稳定分片号，避免消费者数量增加导致文件名过长。
                        string sharedBundleName = CreateSharedBundleName(
                            sharedGroup.Key,
                            shardIndex + 1);
                        //00 自动名称仍需与文件夹 Bundle、Prefab Bundle 统一做忽略大小写的冲突检查。
                        if (!occupiedBundleNames.Add(sharedBundleName))
                            throw new InvalidOperationException(
                                $"模块 {_mBundleModuleName} 的自动共享 Bundle 名称冲突：{sharedBundleName}。" +
                                "请调整冲突的 Prefab 或文件夹 Bundle 名称。");

                        //00 复用文件夹 Bundle 字典进入统一构建流程；物理 Shared 分组本身不改变当前策略决定的 Entry 状态。
                        mAllFolderBundleDic.Add(sharedBundleName, shards[shardIndex]);
                        //00 当前片内每个资源都登记为已归属，Prefab Bundle 后续只建立依赖，不再重复收纳资源。
                        foreach (string dependencyPath in shards[shardIndex])
                        {
                            //00 登记到本轮快速集合，阻止后续 Prefab 再次占有该资源。
                            preassignedPaths.Add(dependencyPath);
                            //00 同步旧全局列表，保持本文件其他重复检查逻辑与新归属结果一致。
                            mAllBundlePathList.Add(dependencyPath);
                        }
                        //00 输出每一片的资源数和源大小，便于开发者直接发现异常增长的 Shared 分片。
                        Debug.Log(
                            $"模块 {_mBundleModuleName} 自动提取共享依赖：{shards[shardIndex].Count} 个，" +
                            $"源大小：{GetTotalSourceSize(shards[shardIndex]) / 1024f / 1024f:F2} MiB，" +
                            $"Bundle：{sharedBundleName}");
                    }
                }
            }

            //00 第三阶段：根据已经确定的显式、文件夹、Shared 归属，为每个 Prefab 生成最终资源列表。
            foreach (string prefabPath in prefabPaths)
            {
                //00 此列表只保存真正属于当前 Prefab Bundle 的资源，不包含跨 Bundle 依赖。
                List<string> bundleAssets = new List<string>();
                foreach (string dependencyPath in prefabDependencies[prefabPath])
                {
                    //00 物理属于 Shared 的依赖只形成跨模块 Bundle 依赖，不进入当前 Prefab 的 assetNames。
                    if (!IsOwnedByCurrentModule(dependencyPath)) continue;
                    //00 嵌套引用的另一个显式 Prefab 不能被当前 Bundle 吞并，它应通过配置形成跨 Bundle 依赖。
                    bool isOtherPrefabEntry = prefabPathSet.Contains(dependencyPath) &&
                                              !string.Equals(dependencyPath, prefabPath, StringComparison.Ordinal);
                    //00 跳过其他 Prefab、Shared 资源和文件夹规则资源；它们都已有明确且更高优先级的归属。
                    if (isOtherPrefabEntry || sharedDependencyPaths.Contains(dependencyPath) ||
                        preassignedPaths.Contains(dependencyPath)) continue;

                    //00 剩余资源只被当前 Prefab 使用，因此安全归入当前 Prefab Bundle。
                    preassignedPaths.Add(dependencyPath);
                    mAllBundlePathList.Add(dependencyPath);
                    bundleAssets.Add(dependencyPath);
                }

                //00 Prefab 自身必须保留在自己的 Bundle；其他显式 Prefab 只作为跨 Bundle 依赖引用。
                if (!bundleAssets.Contains(prefabPath))
                {
                    //00 登记自身归属，防止后续 Prefab 把它当普通依赖重复加入。
                    preassignedPaths.Add(prefabPath);
                    mAllBundlePathList.Add(prefabPath);
                    bundleAssets.Add(prefabPath);
                }
                //00 最终提交当前 Prefab Bundle；GenerateBundleBuilder 会统一追加后缀并交给 Unity 构建。
                mAllPrefabsBundleDic.Add(prefabBundleNames[prefabPath], bundleAssets);
            }
        }

        /// <summary>
        /// 00 单文件包规则：目录下每个可打包资源文件（排除 .cs/.meta/.prefab）单独成为一个 Bundle。
        /// </summary>
        private void BuildAllSingleFiles()
        {
            //00 未配置单文件包目录时跳过；旧序列化配置缺字段为 null。
            if (mBuildModuleData.singleFilePathArr == null || mBuildModuleData.singleFilePathArr.Length == 0)
            {
                return;
            }

            //00 文件夹规则和 Prefab 规则已先执行，预置它们占有的 Bundle 名用于冲突检查。
            HashSet<string> occupiedBundleNames = new HashSet<string>(
                mAllFolderBundleDic.Keys.Concat(mAllPrefabsBundleDic.Keys),
                StringComparer.OrdinalIgnoreCase);
            //00 现有字典内部若有忽略大小写冲突，也要在收集前明确失败，避免写磁盘时才互相覆盖。
            if (occupiedBundleNames.Count != mAllFolderBundleDic.Count + mAllPrefabsBundleDic.Count)
            {
                throw new InvalidOperationException($"模块 {_mBundleModuleName} 存在忽略大小写后重复的 Bundle 名称。");
            }
            //00 已分配给某个 Bundle 的资源路径不能重复归属，与 Prefab 阶段的 preassignedPaths 语义一致。
            HashSet<string> assignedPaths = new HashSet<string>(mAllBundlePathList, StringComparer.Ordinal);

            foreach (string configuredPath in mBuildModuleData.singleFilePathArr)
            {
                //00 兼容旧配置中的空数组项，不访问文件系统。
                if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                string directory = ValidateConfiguredDirectory(configuredPath, "单文件包目录");
                //00 Directory.GetFiles 的返回顺序不受 API 契约保证；固定 Ordinal 排序，避免不同机器产生不同的 Bundle 列表、配置 JSON 与热更清单顺序。
                IEnumerable<string> orderedFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal);
                //00 与文件夹规则一致，递归扫描全部文件再按后缀过滤。
                foreach (string fullPath in orderedFiles)
                {
                    string assetPath = ToAssetPath(fullPath);
                    //00 IsBundleableAssetPath 不排除 .prefab（Prefab 链路共用），这里独立过滤三类文件。
                    if (assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                        assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                        assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    //00 单文件包被显式配置就必须实际独占一个 Bundle；静默跳过会让开发者误以为配置已生效，改为在构建边界给出可操作错误。
                    if (assignedPaths.Contains(assetPath))
                    {
                        throw new InvalidOperationException(
                            $"模块 {_mBundleModuleName} 的单文件包资源已被前置规则占用：{assetPath}\n" +
                            $"现有归属：{DescribeExistingBundleOwner(assetPath)}\n" +
                            $"单文件包目录：{configuredPath}\n" +
                            "请移除重叠的文件夹/Prefab/单文件包规则，确保该资源只由一条规则分配。");
                    }

                    //00 Bundle 名沿用“模块名_文件名”规则，与 Prefab 和文件夹产物保持同构。
                    string bundleName = GenerateBundleName(Path.GetFileNameWithoutExtension(assetPath));
                    //00 同名文件跨目录或与既有规则重名都会导致输出互相覆盖，必须在收集阶段失败。
                    if (!occupiedBundleNames.Add(bundleName))
                    {
                        throw new InvalidOperationException(
                            $"模块 {_mBundleModuleName} 的单文件包存在重复 Bundle 名称：{bundleName}，" +
                            "请调整同名文件或检查与文件夹/Prefab 规则的名称冲突。");
                    }

                    //00 每个文件都是可加载入口，运行时可按路径直接加载。
                    RegisterExplicitEntry(assetPath);
                    //00 标记已分配，防止后续规则再次收集同一路径。
                    assignedPaths.Add(assetPath);
                    mAllBundlePathList.Add(assetPath);
                    //00 单个文件独占一个 Bundle，不做依赖共享分析。
                    mSingleFileBundleDic.Add(bundleName, new List<string> { assetPath });
                }
            }
        }

        /// <summary>
        /// 00 为单文件包重叠错误定位已占用该资源的 Bundle 来源；仅在构建失败路径执行，不影响正常构建性能。
        /// </summary>
        private string DescribeExistingBundleOwner(string assetPath)
        {
            //00 文件夹包、Prefab 包与前面已经扫描的单文件包都可能成为先占用者，逐一查询能让报错直接指向应删除的规则。
            foreach (KeyValuePair<string, List<string>> bundle in mAllFolderBundleDic)
            {
                if (bundle.Value.Contains(assetPath)) return $"文件夹包 {bundle.Key}";
            }

            foreach (KeyValuePair<string, List<string>> bundle in mAllPrefabsBundleDic)
            {
                if (bundle.Value.Contains(assetPath)) return $"Prefab 包 {bundle.Key}";
            }

            foreach (KeyValuePair<string, List<string>> bundle in mSingleFileBundleDic)
            {
                if (bundle.Value.Contains(assetPath)) return $"单文件包 {bundle.Key}";
            }

            //00 兜底覆盖自动 Shared 分片等不属于上述三类字典的前置分配，避免异常消息留空。
            return "前置构建规则或自动分配结果";
        }

        /// <summary>
        /// 00 判断 AssetDatabase 返回的依赖路径能否作为项目 AssetBundle 资源。
        /// </summary>
        private static bool IsBundleableAssetPath(string path)
        {
            //00 空路径、Packages/内置资源、脚本和 meta 都不应直接写进 AssetBundleBuild.assetNames。
            return !string.IsNullOrWhiteSpace(path) &&
                   path.StartsWith("Assets/", StringComparison.Ordinal) &&
                   !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                   !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 00 计算 Prefab 规则允许业务代码主动加载的路径集合，不改变依赖的 Bundle 分配结果。
        /// </summary>
        /// <param name="prefabPath">规则直接找到的 Prefab 路径。</param>
        /// <param name="dependencies">Prefab 的稳定递归依赖快照。</param>
        /// <param name="entryMode">当前模块 Prefab Tab 的统一开放策略。</param>
        /// <returns>已规范化、去重并稳定排序的公开 Entry 路径。</returns>
        internal static IReadOnlyList<string> CollectPrefabLoadableEntryPaths(
            string prefabPath,
            IEnumerable<string> dependencies,
            PrefabDependencyEntryMode entryMode)
        {
            //00 HashSet 保证 Prefab 即使也出现在 Unity 递归依赖结果中仍只登记一次。
            HashSet<string> loadablePaths = new HashSet<string>(StringComparer.Ordinal);
            //00 Prefab 在两种策略下始终是规则直接选中的公开入口。
            string normalizedPrefabPath = NormalizeAssetPath(prefabPath);
            //00 无效 Prefab 路径属于构建器调用错误，沿用现有 RegisterExplicitEntry 的强失败语义。
            if (!IsBundleableAssetPath(normalizedPrefabPath))
                throw new InvalidOperationException($"Prefab Entry 不是有效 Assets 资源路径：{prefabPath}");
            //00 先加入 Prefab，确保 PrefabOnly 能直接返回唯一入口。
            loadablePaths.Add(normalizedPrefabPath);

            //00 默认零值和显式 PrefabOnly 都不开放 Material、Texture 等递归依赖。
            if (entryMode == PrefabDependencyEntryMode.PrefabOnly)
                return loadablePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            //00 未知枚举值可能来自损坏或未来版本配置，不能静默扩大资源公开范围。
            if (entryMode != PrefabDependencyEntryMode.PrefabAndDependencies)
                throw new InvalidOperationException($"不支持的 Prefab 资源加载策略：{entryMode}。");

            //00 空依赖集合表示 Prefab 没有额外依赖，仍然只返回 Prefab 本身。
            if (dependencies != null)
            {
                foreach (string dependencyPath in dependencies)
                {
                    //00 与实际 Bundle 构建过滤保持一致，排除 Packages、脚本、meta 和空路径。
                    string normalizedDependencyPath = NormalizeAssetPath(dependencyPath);
                    //00 只把真正可能写入 AssetBundle 配置的项目资源公开为 Entry。
                    if (!IsBundleableAssetPath(normalizedDependencyPath)) continue;
                    //00 第一期校验已经保证该 Assets 路径属于当前模块；此处只负责 Entry 语义。
                    loadablePaths.Add(normalizedDependencyPath);
                }
            }

            //00 稳定排序使 JSON 配置与测试结果不依赖 AssetDatabase 返回顺序。
            return loadablePaths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        /// <summary>
        /// 00 创建与枚举顺序无关的消费者签名，作为自动 Shared 分组和 CRC 命名的唯一依据。
        /// </summary>
        private static string CreateConsumerSignature(IEnumerable<string> consumers)
        {
            //00 使用完整 Prefab 路径参与签名，避免不同目录下同名 Prefab 被误判成相同消费者集合。
            return string.Join("|", consumers.OrderBy(path => path, StringComparer.Ordinal));
        }

        /// <summary>
        /// 00 根据消费者签名和分片编号创建短小且稳定的自动 Shared Bundle 名称。
        /// </summary>
        private string CreateSharedBundleName(string consumerSignature, int shardNumber)
        {
            //00 CRC 使用完整消费者签名，八位十六进制能稳定区分同摘要但不同路径的消费者集合。
            string signatureHash = Crc32.GetCrc32(consumerSignature).ToString("x8");
            //00 分片号始终存在，避免单片组未来扩为多片时导致第一片文件名变化和无谓热更下载。
            string sharedName = $"{SharedDependencyBundleSuffix}_{signatureHash}_{shardNumber:D2}";
            //00 继续复用模块前缀规则，保持框架所有 Bundle 的命名结构一致。
            return GenerateBundleName(sharedName);
        }

        /// <summary>
        /// 00 按稳定路径顺序和源文件大小上限，将同一消费者组切分成一个或多个 Shared 分片。
        /// </summary>
        private List<List<string>> SplitSharedDependenciesBySourceSize(IEnumerable<string> dependencyPaths)
        {
            //00 分片结果按创建顺序保存，该顺序会直接映射到名称中的 01、02 等编号。
            List<List<string>> shards = new List<List<string>>();
            //00 当前分片初始为空，只有实际加入资源后才会写入结果，绝不生成空 Bundle。
            List<string> currentShard = new List<string>();
            //00 使用 long 累加，避免大型工程资源总大小超过 int 上限。
            long currentShardSize = 0L;
            //00 固定路径排序是分片稳定性的基础；不能按文件系统返回顺序或文件大小排序。
            foreach (string dependencyPath in dependencyPaths.OrderBy(path => path, StringComparer.Ordinal))
            {
                //00 每个依赖都必须对应真实源文件；缺失时立即给出可操作错误，而不是按 0 字节静默分片。
                long dependencySize = GetSourceAssetSize(dependencyPath);
                //00 当前片已有资源且加入新资源会超过 32 MiB 时，先封存当前片再创建下一片。
                if (currentShard.Count > 0 && currentShardSize + dependencySize > AutoSharedBundleSourceSizeLimit)
                {
                    //00 保存已经稳定完成的分片。
                    shards.Add(currentShard);
                    //00 新分片使用新列表，禁止后续追加意外修改已保存分片。
                    currentShard = new List<string>();
                    //00 新分片源大小从 0 重新累计。
                    currentShardSize = 0L;
                }
                //00 当前资源加入当前片；单个资源超过阈值时会自然独占一个分片。
                currentShard.Add(dependencyPath);
                //00 累加当前片源文件大小，供下一个资源判断是否需要换片。
                currentShardSize += dependencySize;
                //00 单资源超过上限无法继续拆分，因此保留独占片并输出明确警告。
                if (dependencySize > AutoSharedBundleSourceSizeLimit)
                    Debug.LogWarning(
                        $"模块 {_mBundleModuleName} 的共享资源超过 32 MiB，将独占一个 Shared 分片：" +
                        $"{dependencyPath}（{dependencySize / 1024f / 1024f:F2} MiB）");
            }
            //00 循环结束后保存最后一个非空分片。
            if (currentShard.Count > 0) shards.Add(currentShard);
            //00 sharedDependencyPaths 非空时理论上至少有一片；保留显式结果方便调用方直接迭代。
            return shards;
        }

        /// <summary>
        /// 00 获取 Assets/... 资源对应的源文件字节数，并在资源缺失时立即终止构建。
        /// </summary>
        private long GetSourceAssetSize(string assetPath)
        {
            //00 Unity 工程根目录位于 Application.dataPath 的上一级，将资源路径拼成真实磁盘路径。
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            //00 NormalizeAssetPath 已统一分隔符；Path.Combine 会按当前系统生成可访问的完整路径。
            string fullPath = Path.GetFullPath(Path.Combine(projectRoot, NormalizeAssetPath(assetPath)));
            //00 自动依赖来自 AssetDatabase，若磁盘文件不存在说明工程状态不一致，必须给出具体路径。
            if (!File.Exists(fullPath))
                throw new FileNotFoundException(
                    $"模块 {_mBundleModuleName} 计算 Shared 分片时找不到源资源：{assetPath}",
                    fullPath);
            //00 FileInfo.Length 返回字节数，使用 long 可安全覆盖大型资源文件。
            return new FileInfo(fullPath).Length;
        }

        /// <summary>
        /// 00 计算一个分片内全部源资源的字节总数，仅用于构建日志和结果审计。
        /// </summary>
        private long GetTotalSourceSize(IEnumerable<string> dependencyPaths)
        {
            //00 聚合时复用统一的存在性校验，避免日志显示与实际分片依据不一致。
            return dependencyPaths.Sum(GetSourceAssetSize);
        }
        /// <summary>
        /// 打包AssetBundle
        /// </summary>
        private void BuildAllAssetBundle()
        {
            try
            {
                //生成所有要打包的Bundle
                GenerateBundleBuilder();
                //生成一份AssetBundle配置
                ZMBuildProgress.Report("写入配置", "生成 AssetBundle 配置", .48f);
                WriteAssetBundleConfig();
                
                AssetDatabase.Refresh();

                UnityEditor.BuildTarget target= mBuildTarget == UnityEditor.BuildTarget.NoTarget ? EditorUserBuildSettings.activeBuildTarget : mBuildTarget;
                Debug.Log("BuildPipeline.BuildAssetBundles target:"+target);
                var buildAssetBundleOptions = UnityEditor.BuildAssetBundleOptions.ChunkBasedCompression;
                //调用UnityAPI打包AssetBundle
                ZMBuildProgress.Report("Unity 构建中", "BuildPipeline.BuildAssetBundles 正在执行，此阶段无法取消", .56f, false);
                AssetBundleManifest manifest= BuildPipeline.BuildAssetBundles(mBundleOutPutPath,mBundleBuildList.ToArray(), buildAssetBundleOptions,mBuildTarget== UnityEditor.BuildTarget.NoTarget?EditorUserBuildSettings.activeBuildTarget:mBuildTarget);
                if (manifest==null)
                {
                    Debug.LogError("AssetBundle Build failed!");
                    throw new InvalidOperationException("Unity BuildPipeline 构建 AssetBundle 失败");
                }
                else
                {
                    ZMBuildProgress.Report("构建后处理", "复制源文件并清理 Manifest", .80f);
                    Debug.Log("AssetBundle Build Successs!:"+ manifest);
                    BuildSourceAssetBundle();
                    DeleteAllBundleManifestFile();
                    // Manifest 必须基于最终加密文件计算，所有配置中的 Bundle 在此统一加密。
                    EncryptAllBundle();
                    if (mBuildType== BuildType.HotPatch)
                    {
                        ZMBuildProgress.Report("生成热更输出", "复制补丁文件并生成清单", .90f);
                        GeneratorHotAssets();
                    }
                    else
                    {
                        ZMBuildProgress.Report("生成资源清单", "写入热更主清单", .92f);
                        GeneratorHotAssetsManifest(mBundleOutPutPath);
                    }
                    ZMBuildProgress.Report("模块完成", _mBundleModuleName, 1f);
                }
            }
            finally
            {
            }
           
    
        }
        private void BuildSourceAssetBundle()
        {
            foreach (SourceBuildEntry sourceEntry in mSourceEntryList)
            {
                File.Copy(sourceEntry.FullPath, Path.Combine(mBundleOutPutPath, sourceEntry.OutputFileName), true);
            }
        }

        /// <summary>
        /// 00 把业务模块引用的 Shared 资源补充到 Shared 模块构建输入中。
        /// 00 资源即使不是 Shared 自身的 Entry，也必须拥有物理 Bundle，否则 Unity 会把它隐式复制回业务 Bundle。
        /// </summary>
        private void BuildExternallyConsumedSharedAssets()
        {
            //00 只有 Shared 模块承担外部资源接管职责，普通业务模块不创建额外跨模块分片。
            if (mBuildModuleData.moduleRole != BundleModuleRole.Shared) return;
            //00 归属分析在收集前已冻结；缺失表示调用链绕过了正式构建门禁。
            if (mContext.OwnershipAnalysis == null)
                throw new InvalidOperationException($"模块 {_mBundleModuleName} 缺少外部 Shared 资源分析。");

            //00 获取所有确实被业务模块依赖的物理 Shared 资源，而不是扫描整个 Shared 目录。
            IReadOnlyList<string> externalAssets =
                mContext.OwnershipAnalysis.GetExternallyConsumedAssets(_mBundleModuleName);
            //00 没有外部消费者时保持原模块产物完全不变。
            if (externalAssets.Count == 0) return;

            //00 已由 Shared 的单包、子文件夹或 Prefab 规则分配的资源不能重复加入自动分片。
            HashSet<string> assignedPaths = new HashSet<string>(mAllBundlePathList, StringComparer.OrdinalIgnoreCase);
            //00 所有既有 Bundle 名参与忽略大小写冲突检查，自动名称不能覆盖用户显式命名。
            HashSet<string> occupiedBundleNames = new HashSet<string>(
                mAllFolderBundleDic.Keys.Concat(mAllPrefabsBundleDic.Keys),
                StringComparer.OrdinalIgnoreCase);
            //00 同一消费者模块集合的资源进入同一组，避免不相关业务模块的公共资源被绑在一起更新。
            Dictionary<string, List<string>> assetsByConsumerSignature =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string assetPath in externalAssets)
            {
                //00 已有明确 Bundle 归属时只需要保留跨模块引用，不创建第二份物理资源。
                if (assignedPaths.Contains(assetPath)) continue;
                //00 外部消费资源必须仍位于当前 Shared 的物理目录中，防止分析和收集之间配置被修改。
                if (!IsOwnedByCurrentModule(assetPath))
                    throw new InvalidOperationException(
                        $"Shared 模块 {_mBundleModuleName} 无法接管不属于自身目录的资源：{assetPath}");
                //00 消费者签名来自归属分析快照，空值说明内部依赖图不完整，不能退回随机分组。
                string consumerSignature =
                    mContext.OwnershipAnalysis.GetExternalConsumerSignature(_mBundleModuleName, assetPath);
                if (string.IsNullOrWhiteSpace(consumerSignature))
                    throw new InvalidOperationException(
                        $"Shared 模块 {_mBundleModuleName} 缺少资源消费者信息：{assetPath}");
                //00 第一次遇到该消费者组合时创建独立列表。
                if (!assetsByConsumerSignature.TryGetValue(consumerSignature, out List<string> groupAssets))
                {
                    groupAssets = new List<string>();
                    assetsByConsumerSignature.Add(consumerSignature, groupAssets);
                }
                //00 外部资源路径已经由分析层去重，此处按稳定顺序追加。
                groupAssets.Add(assetPath);
            }

            //00 按消费者签名顺序执行已有 32 MiB 稳定分片算法，避免新增另一套容量规则。
            foreach (KeyValuePair<string, List<string>> group in
                     assetsByConsumerSignature.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                //00 现有分片器会验证文件存在，并按完整路径稳定切分。
                List<List<string>> shards = SplitSharedDependenciesBySourceSize(group.Value);
                for (int shardIndex = 0; shardIndex < shards.Count; shardIndex++)
                {
                    //00 名称继续保持“模块_shared_稳定CRC_分片号”，不会重新加入冗长消费者摘要。
                    string bundleName = CreateSharedBundleName(group.Key, shardIndex + 1);
                    //00 自动分片与任何已有 Bundle 重名都必须在调用 Unity 前失败。
                    if (!occupiedBundleNames.Add(bundleName))
                        throw new InvalidOperationException(
                            $"Shared 模块 {_mBundleModuleName} 的外部依赖 Bundle 名称冲突：{bundleName}");
                    //00 复用文件夹 Bundle 容器进入统一 GenerateBundleBuilder 流程。
                    mAllFolderBundleDic.Add(bundleName, shards[shardIndex]);
                    foreach (string assetPath in shards[shardIndex])
                    {
                        //00 登记物理归属，防止后续收集阶段把同一资源重复分配。
                        assignedPaths.Add(assetPath);
                        mAllBundlePathList.Add(assetPath);
                    }
                    //00 日志明确记录自动接管原因、数量、源大小和最终 Bundle，便于开发者审计 Shared 增长。
                    Debug.Log(
                        $"Shared 模块 {_mBundleModuleName} 接管跨模块依赖：{shards[shardIndex].Count} 个，" +
                        $"源大小：{GetTotalSourceSize(shards[shardIndex]) / 1024f / 1024f:F2} MiB，" +
                        $"Bundle：{bundleName}");
                }
            }
        }

        /// <summary>
        /// 收集源文件 Entry，并固定本次构建使用的文件快照。
        /// </summary>
        private void CollectSourceEntries()
        {
            if (mBuildModuleData.sourceFolderPathArr == null) return;

            Dictionary<string, string> outputNameOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string configuredPath in mBuildModuleData.sourceFolderPathArr)
            {
                if (string.IsNullOrWhiteSpace(configuredPath)) continue;
                string path = ValidateConfiguredDirectory(configuredPath, "源文件目录");
                foreach (string fullPath in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    string assetPath = ToAssetPath(fullPath);
                    if (assetPath.EndsWith(".cs") || assetPath.EndsWith(".meta")) continue;

                    string outputFileName = Path.GetFileName(fullPath);
                    if (outputNameOwners.TryGetValue(outputFileName, out string existingPath) &&
                        !string.Equals(existingPath, assetPath, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"模块 {_mBundleModuleName} 的源文件输出名称冲突：{existingPath} 与 {assetPath} 都会输出为 {outputFileName}");
                    }

                    outputNameOwners[outputFileName] = assetPath;
                    RegisterExplicitEntry(assetPath);
                    mSourceEntryList.Add(new SourceBuildEntry
                    {
                        AssetPath = assetPath,
                        FullPath = Path.GetFullPath(fullPath),
                        OutputFileName = outputFileName
                    });
                }
            }
        }

        /// <summary>
        /// 写入补丁版本
        /// </summary>
        private void BuildPatchInfo()
        {
            if (mBuildType== BuildType.HotPatch)
            {
                if (!Directory.Exists(mConfgDataPath))
                {
                    Directory.CreateDirectory(mConfgDataPath);
                }
            }
            string patchInfo= $"patchversion|{mHotPatchVersion}";
            FileHelper.WriteFile(mConfgDataPath + "patchinfo.txt", System.Text.Encoding.UTF8.GetBytes(patchInfo));
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 生成AssetBundle配置文件
        /// </summary>
        private void WriteAssetBundleConfig()
        {
            //00 单模块兼容路径没有合法跨模块依赖，因此从本模块列表构造局部全局映射即可。
            Dictionary<string, AssetBundleBuildLocation> localLocations =
                CreateLocalAssetLocations();
            //00 继续复用协议版本 2 写入器；没有 Shared 的产物只比旧 JSON 多兼容字段，不改变加载行为。
            WriteAssetBundleConfig(localLocations, Array.Empty<string>());
        }

        /// <summary>
        /// 00 使用冻结的全局资源位置映射生成当前模块配置。
        /// </summary>
        private void WriteAssetBundleConfig(
            IReadOnlyDictionary<string, AssetBundleBuildLocation> globalAssetLocations,
            IReadOnlyList<string> moduleDependencies)
        {
            //00 全局映射是跨模块依赖协议的唯一事实来源，缺失时不能退回猜测主模块目录。
            if (globalAssetLocations == null) throw new ArgumentNullException(nameof(globalAssetLocations));
            //00 协议版本由 BundleConfig 默认值固定为 2，旧运行时字段仍继续写入。
            BundleConfig config = new BundleConfig
            {
                //00 即使模块没有任何 Entry，配置仍能明确声明自身身份。
                moduleName = _mBundleModuleName,
                //00 模块依赖去空、去重并稳定排序，供运行时建立初始化顺序和租约图。
                moduleDependencies = (moduleDependencies ?? Array.Empty<string>())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList(),
                //00 BundleInfo 仍按当前模块拥有的资源逐条写入。
                bundleInfoList = new List<BundleInfo>()
            };
            
            //所有AssetBundle文件字典 key =路径 value =AssetBundleName
            Dictionary<string, string> allBundleFilePathDic = new Dictionary<string, string>();
            //遍历所有的Bundle列表，收集成字典，方便写入配置
            foreach (var item in mBundleBuildList)
            {
                foreach (var itemValue in item.assetNames)
                {
                    if (!allBundleFilePathDic.ContainsKey(itemValue))
                    {
                        allBundleFilePathDic.Add(itemValue, item.assetBundleName);
                    }
                }
            }
            foreach (SourceBuildEntry sourceEntry in mSourceEntryList)
            {
                if (!allBundleFilePathDic.TryAdd(sourceEntry.AssetPath, Path.GetFileNameWithoutExtension(sourceEntry.AssetPath)))
                {
                    throw new InvalidOperationException($"模块 {_mBundleModuleName} 的源文件与 Bundle 资源重复：{sourceEntry.AssetPath}");
                }
            }

            int index = 0;
            //计算AssetBundle数据，生成AsestBundle配置文件。
            foreach (var item in allBundleFilePathDic)
            {
                //获取文件路径
                string filePath = item.Key;
                if (!filePath.EndsWith(".cs"))
                {
                    BundleInfo info = new BundleInfo();
                    info.path = filePath;
                    info.bundleName = item.Value;
                    info.assetName = Path.GetFileName(filePath);
                    info.crc = Crc32.GetCrc32(filePath);
                    info.bundleModule = _mBundleModuleName.ToString();
                    info.isAddressableAsset = mBuildModuleData.isAddressableAsset;
                    info.isLoadableEntry = mExplicitEntryPathSet.Contains(NormalizeAssetPath(filePath));
                    info.bundleDependce = new List<string>();
                    //00 新列表保存依赖的完整模块身份；运行时优先消费它。
                    info.bundleDependencies = new List<BundleDependencyInfo>();
                    ZMBuildProgress.Report("写入配置", allBundleFilePathDic[filePath], .44f + .04f * index / Mathf.Max(1, allBundleFilePathDic.Count));
                    
                    string[] dependence= AssetDatabase.GetDependencies(filePath);
                    foreach (var dePath in dependence)
                    {
                        //如果依赖项不是当前的这个文件，以及依赖项不是cs脚本 就进行处理
                        if (!dePath.Equals(filePath) && dePath.EndsWith(".cs")==false)
                        {
                            //00 依赖可能属于当前模块或 Shared，必须从统一映射解析真实位置。
                            if (globalAssetLocations.TryGetValue(
                                    NormalizeAssetPath(dePath),
                                    out AssetBundleBuildLocation dependencyLocation))
                            {
                                //00 同一模块同一 Bundle 属于包内依赖，不能写成外部持有，否则释放时会重复归还。
                                bool isSameBundle =
                                    string.Equals(info.bundleModule, dependencyLocation.ModuleName, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(info.bundleName, dependencyLocation.BundleName, StringComparison.Ordinal);
                                if (!isSameBundle &&
                                    !info.bundleDependencies.Any(dependency =>
                                        string.Equals(dependency.bundleModule, dependencyLocation.ModuleName, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(dependency.bundleName, dependencyLocation.BundleName, StringComparison.Ordinal)))
                                {
                                    //00 新协议完整保存模块和 Bundle 名称。
                                    info.bundleDependencies.Add(new BundleDependencyInfo
                                    {
                                        bundleModule = dependencyLocation.ModuleName,
                                        bundleName = dependencyLocation.BundleName
                                    });
                                    //00 旧字段继续保存 Bundle 名；旧配置读取和无 Shared 工程保持兼容。
                                    if (!info.bundleDependce.Contains(dependencyLocation.BundleName))
                                        info.bundleDependce.Add(dependencyLocation.BundleName);
                                }
                            }
                        }
                    }
                    //00 依赖名称使用稳定排序，避免 AssetDatabase 返回顺序变化造成配置文本和热更差异抖动。
                    info.bundleDependce.Sort(StringComparer.Ordinal);
                    //00 新协议先按模块再按 Bundle 排序，避免 AssetDatabase 返回顺序引起热更配置抖动。
                    info.bundleDependencies = info.bundleDependencies
                        .OrderBy(dependency => dependency.bundleModule, StringComparer.Ordinal)
                        .ThenBy(dependency => dependency.bundleName, StringComparer.Ordinal)
                        .ToList();

                    config.bundleInfoList.Add(info);
                    index++;
                }
            }
            //生成AsestBundle配置文件
            string json = JsonConvert.SerializeObject(config,Formatting.Indented);
            string bundleConfigPath = Application.dataPath + "/" + BundleSettings.Instance.ZMAssetRootPath + "/Config/" + _mBundleModuleName.ToString().ToLowerInvariant() + "assetbundleconfig.json";
            StreamWriter writer= File.CreateText(bundleConfigPath);
            writer.Write(json);
            writer.Dispose();
            writer.Close();

            AssetDatabase.Refresh();
            
            //生成热更模块配置到Resources文件夹
            // GeneratorHotModuleCfgToResource();
        }

        /// <summary>
        /// 00 为当前上下文创建资源路径到物理 Bundle 的稳定位置映射。
        /// </summary>
        internal Dictionary<string, AssetBundleBuildLocation> CreateLocalAssetLocations()
        {
            //00 Unity 资源路径大小写在不同平台上可能产生差异，映射使用忽略大小写比较与归属校验一致。
            Dictionary<string, AssetBundleBuildLocation> locations =
                new Dictionary<string, AssetBundleBuildLocation>(StringComparer.OrdinalIgnoreCase);
            //00 所有 AssetBundleBuild 已包含配置 Bundle；逐项登记并拒绝同一模块内重复分配。
            foreach (AssetBundleBuild bundleBuild in mBundleBuildList)
            {
                foreach (string assetPath in bundleBuild.assetNames ?? Array.Empty<string>())
                {
                    //00 空路径不能进入 Unity BuildPipeline，也不能成为配置键。
                    string normalizedPath = NormalizeAssetPath(assetPath);
                    if (string.IsNullOrWhiteSpace(normalizedPath))
                        throw new InvalidOperationException($"模块 {_mBundleModuleName} 的 Bundle {bundleBuild.assetBundleName} 包含空资源路径。");
                    //00 同一资源只能由一个 Bundle 拥有；重复分配必须在调用 Unity 前失败。
                    if (!locations.TryAdd(
                            normalizedPath,
                            new AssetBundleBuildLocation(_mBundleModuleName, bundleBuild.assetBundleName)))
                        throw new InvalidOperationException($"模块 {_mBundleModuleName} 的资源被重复分配到多个 Bundle：{normalizedPath}");
                }
            }
            // 源文件不进入 Unity Bundle，但仍是当前模块的远端资源配置项，保留既有文件名身份。
            foreach (SourceBuildEntry sourceEntry in mSourceEntryList)
            {
                if (!locations.TryAdd(
                        sourceEntry.AssetPath,
                        new AssetBundleBuildLocation(
                            _mBundleModuleName,
                            Path.GetFileNameWithoutExtension(sourceEntry.AssetPath))))
                    throw new InvalidOperationException($"模块 {_mBundleModuleName} 的源文件与 Bundle 资源重复：{sourceEntry.AssetPath}");
            }
            //00 返回独立字典，统一编排器可以安全合并多个模块而不修改上下文集合。
            return locations;
        }
        /// <summary>
        /// 生成Bundle打包列表
        /// </summary>
        /// <param name="clear"></param>
        private void GenerateBundleBuilder(bool clear=false)
        {
            int i = 0;
            //收集所有要打包的文件夹Bundle
            foreach (var item in mAllFolderBundleDic)
            {
                i++;
                ZMBuildProgress.Report("生成构建列表", item.Key, .38f + .04f * i / Mathf.Max(1, mAllFolderBundleDic.Count));
                mBundleBuildList.Add(new AssetBundleBuild(){ assetBundleName = CreatePhysicalBundleFileName(item.Key) , assetNames = item.Value.ToArray() });
            }
            //收集所有要打包的预制体Bundle
            i = 0;
            foreach (var item in mAllPrefabsBundleDic)
            {
                i++;
                ZMBuildProgress.Report("生成构建列表", item.Key, .42f + .04f * i / Mathf.Max(1, mAllPrefabsBundleDic.Count));
                mBundleBuildList.Add(new AssetBundleBuild(){ assetBundleName = CreatePhysicalBundleFileName(item.Key), assetNames = item.Value.ToArray() });
            }
            //收集所有要打包的单文件包Bundle
            i = 0;
            foreach (var item in mSingleFileBundleDic)
            {
                i++;
                ZMBuildProgress.Report("生成构建列表", item.Key, .46f + .04f * i / Mathf.Max(1, mSingleFileBundleDic.Count));
                mBundleBuildList.Add(new AssetBundleBuild(){ assetBundleName = CreatePhysicalBundleFileName(item.Key), assetNames = item.Value.ToArray() });
            }
            
            //收集至Bundle打包配置文件
            string bundleConfigPath = Application.dataPath + "/" + BundleSettings.Instance.ZMAssetRootPath + "/Config/" + _mBundleModuleName.ToString().ToLowerInvariant() + "assetbundleconfig.json";
            string configBundleName = CreatePhysicalBundleFileName(
                _mBundleModuleName.ToString().ToLowerInvariant() + "bundleconfig");
            mBundleBuildList.Add(new AssetBundleBuild(){ assetBundleName = configBundleName,assetNames = new []
            {
                //00 Application.dataPath 后已带分隔符，替换为 Assets 可避免生成 Assets//... 的非规范路径。
                $"{bundleConfigPath.Replace(Application.dataPath, "Assets").Replace("\\", "/")}"
            }});

        }
        /// <summary>
        /// 是否是重复的Bundle文件
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private bool IsRepeatBundleFile(string path)
        {
            if (path.EndsWith(".cs"))
            {
                return true;
            }
            foreach (var item in mAllBundlePathList)
            {
                if (string.Equals(item, path) || item.Contains(path) || path.EndsWith(".cs"))
                {
                    return true;
                }
            }
            return false;
        }

        internal string GenerateBundleName(string abName)
        {
            string validatedName = AssetBundleNameValidator.EnsureValidFileName(
                abName,
                $"模块 {_mBundleModuleName} 的 Bundle 名称");
            return AssetBundleNameValidator.EnsureValidFileName(
                _mBundleModuleName + "_" + validatedName,
                $"模块 {_mBundleModuleName} 的最终 Bundle 名称");
        }

        private string CreatePhysicalBundleFileName(string bundleName)
        {
            return AssetBundleNameValidator.EnsureValidFileName(
                bundleName.ToLowerInvariant() + BundleSettings.Instance.ABSUFFIX,
                $"模块 {_mBundleModuleName} 的物理 Bundle 文件名");
        }

        /// <summary>
        /// 登记规则直接选中的可加载入口。
        /// </summary>
        private void RegisterExplicitEntry(string path)
        {
            string assetPath = NormalizeAssetPath(path);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException($"模块 {_mBundleModuleName} 的 Entry 不是有效 Assets 路径：{path}");
            mExplicitEntryPathSet.Add(assetPath);
        }

        private string ValidateConfiguredDirectory(string configuredPath, string ruleName)
        {
            string normalizedPath = configuredPath.Trim().Replace('\\', '/').TrimEnd('/');
            if (!Directory.Exists(normalizedPath))
                throw new DirectoryNotFoundException($"模块 {_mBundleModuleName} 的{ruleName}不存在：{normalizedPath}");
            return normalizedPath;
        }

        private string ToAssetPath(string path)
        {
            string fullPath = Path.GetFullPath(path).Replace('\\', '/');
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
            if (!fullPath.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"模块 {_mBundleModuleName} 的资源不在当前 Unity 工程内：{path}");
            return NormalizeAssetPath(fullPath.Substring(projectRoot.Length + 1));
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/').TrimStart('/');
        }

        /// <summary>
        /// 00 判断资源物理所有者是否为当前上下文模块，与 Entry 标记无关。
        /// </summary>
        private bool IsOwnedByCurrentModule(string assetPath)
        {
            //00 3A0 兼容路径也会在收集前建立分析；缺失分析属于内部编排错误，不能放行外部资源。
            if (mContext.OwnershipAnalysis == null)
                throw new InvalidOperationException($"模块 {_mBundleModuleName} 缺少资源物理归属分析，已停止收集 Bundle。");
            //00 目录门禁保证返回值唯一；当前模块名称按忽略大小写比较以匹配配置语义。
            string ownerModule = mContext.OwnershipAnalysis.GetPhysicalOwnerModule(NormalizeAssetPath(assetPath));
            return string.Equals(ownerModule, _mBundleModuleName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 删除所有AssetBundle自动生成的清单文件
        /// </summary>
        private void DeleteAllBundleManifestFile()
        {
           string[] filePathArr= Directory.GetFiles(mBundleOutPutPath);
            foreach (var path in filePathArr)
            {
                if (path.EndsWith(".manifest"))
                {
                    File.Delete(path);
                }
            }
        }
        /// <summary>
        /// 加密所有的AssetBundle
        /// </summary>
        private void EncryptAllBundle()
        {
            BundleSettings settings = BundleSettings.Instance;
            if (settings == null || settings.bundleEncrypt == null || !settings.bundleEncrypt.isEncrypt)
                return;
            if (string.IsNullOrWhiteSpace(settings.bundleEncrypt.encryptKey))
                throw new InvalidOperationException("已启用 AssetBundle 加密，但加密密钥为空。");

            // 只加密构建列表中的 AssetBundle，源文件和其他构建产物保持原始格式。
            HashSet<string> bundleFileNames = new HashSet<string>(
                mBundleBuildList.Select(bundle => bundle.assetBundleName),
                StringComparer.OrdinalIgnoreCase);
            DirectoryInfo directoryInfo = new DirectoryInfo(mBundleOutPutPath);
            FileInfo[] fileInfoArr = directoryInfo
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => bundleFileNames.Contains(file.Name))
                .ToArray();

            for (int i = 0; i < fileInfoArr.Length; i++)
            {
                ZMBuildProgress.Report(
                    "加密文件",
                    fileInfoArr[i].Name,
                    .82f + .08f * i / Mathf.Max(1, fileInfoArr.Length));
                if (!AES.AESFileEncrypt(fileInfoArr[i].FullName, settings.bundleEncrypt.encryptKey))
                    throw new InvalidOperationException($"AssetBundle 加密失败：{fileInfoArr[i].FullName}");
            }
            Debug.Log($"AssetBundle Encrypt Finish! Count:{fileInfoArr.Length}");
        }

        /// <summary>
        /// 00 保留既有单模块内嵌 API，并转交统一的临时复制、完整校验和原子发布实现。
        /// </summary>
        public static void CopyBundleToStramingAssets(BundleModuleData moduleData,bool showTips=true)
        {
            CopyBundlesToStreamingAssets(new[] { moduleData }, showTips);
        }

        /// <summary>
        /// 00 将所有选中模块作为一个 StreamingAssets 发布事务；任一模块失败时不会发布其他模块。
        /// </summary>
        internal static void CopyBundlesToStreamingAssets(
            IReadOnlyList<BundleModuleData> moduleDataList,
            bool showTips = true)
        {
            //00 先冻结当前平台，保证源路径解析期间切换 Unity 平台不会混入不同平台产物。
            UnityEditor.BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            if (moduleDataList == null || moduleDataList.Count == 0)
                throw new InvalidOperationException("没有选择任何可内嵌的资源模块。");

            List<StreamingAssetsPublisher.ModuleSource> moduleSources =
                new List<StreamingAssetsPublisher.ModuleSource>(moduleDataList.Count);
            foreach (BundleModuleData moduleData in moduleDataList)
            {
                //00 模块配置缺失时在任何文件复制前失败，避免只更新选择列表中的一部分模块。
                if (moduleData == null || string.IsNullOrWhiteSpace(moduleData.moduleName))
                    throw new ArgumentException("内嵌资源时必须提供有效的模块配置。", nameof(moduleDataList));
                string sourcePath = Path.GetFullPath(Path.Combine(
                    Application.dataPath,
                    "..",
                    "AssetBundle",
                    moduleData.moduleName,
                    buildTarget.ToString()));
                moduleSources.Add(new StreamingAssetsPublisher.ModuleSource(moduleData.moduleName, sourcePath));
            }

            string streamingAssetBundleRoot = Path.GetFullPath(Path.Combine(
                Application.streamingAssetsPath,
                "AssetBundle"));
            StreamingAssetsPublisher.Publish(
                moduleSources,
                streamingAssetBundleRoot,
                (fileName, progress) => ZMBuildProgress.Report("校验并内嵌资源", fileName, progress));

            //00 原子发布成功后再刷新 AssetDatabase，失败路径不会让 Unity 导入半成品目录。
            AssetDatabase.Refresh();
            if (showTips)
                EditorUtility.DisplayDialog(
                    "内嵌操作",
                    $"已原子内嵌 {moduleSources.Count} 个模块。\nPath：{streamingAssetBundleRoot}",
                    "确认");
            Debug.Log($"StreamingAssets 原子内嵌完成。模块数量：{moduleSources.Count}，目录：{streamingAssetBundleRoot}");
        }


        /// <summary>
        /// 生成热更模块配置到Resources文件夹
        /// </summary>
        public static void GeneratorHotModuleCfgToResource()
        {
            // string bundleModuleCfgJson = string.Empty;
            // TextAsset textAsset= Resources.Load<TextAsset>("bundlemoduleCfg");
            // if (textAsset==null)
            // {
            //     List<BundleModuleData> bundleModuleData = new List<BundleModuleData> { mBuildModuleData };
            //     bundleModuleCfgJson = JsonConvert.SerializeObject(bundleModuleData);
            //     File.WriteAllText(mResourcesPath + "bundlemoduleCfg.json", bundleModuleCfgJson);
            // }
            // else
            // {
            //     List<BundleModuleData> bundleModuleData = JsonConvert.DeserializeObject<List<BundleModuleData>>(textAsset.text);
            //     for (int i = 0; i < bundleModuleData.Count; i++)
            //     {
            //         if (string.Equals(bundleModuleData[i].moduleName,mBuildModuleData.moduleName))
            //         {
            //             bundleModuleData.Remove(bundleModuleData[i]);
            //             break;
            //         }
            //     }
            //     bundleModuleData.Add(mBuildModuleData);
            //     bundleModuleCfgJson= JsonConvert.SerializeObject(bundleModuleData);
            //     File.WriteAllText(mResourcesPath+ "bundlemoduleCfg.json", bundleModuleCfgJson);
            // }

        }
        /// <summary>
        /// 生成热更资源
        /// </summary>
        private void GeneratorHotAssets()
        {
            FileHelper.DeleteFolder(mHotAssetsOutPutPath);
            Directory.CreateDirectory(mHotAssetsOutPutPath);

            string[] bundlePatchArr= Directory.GetFiles(mBundleOutPutPath,"*");
            for (int i = 0; i < bundlePatchArr.Length; i++)
            {
                string path = bundlePatchArr[i];
                ZMBuildProgress.Report("生成热更文件", Path.GetFileName(path), .90f + .08f * i / Mathf.Max(1, bundlePatchArr.Length));
                string disPath = mHotAssetsOutPutPath + Path.GetFileName(path);

                File.Copy(path,disPath);
            }
            Debug.Log("热更文件生成成功");
            GeneratorHotAssetsManifest(mHotAssetsOutPutPath);
        }
        /// <summary>
        /// 生成热更资源配置清单
        /// </summary>
        private void GeneratorHotAssetsManifest(string outPutPath)
        {
            //00 完整构建与热更构建共用同一个清单数据生成器，防止两种发布类型的协议漂移。
            byte[] manifestBytes = CreateManifestBytesForOrchestration(outPutPath);
            string hotMainifestPath = string.Empty;
            if (!outPutPath.Contains("HotAssets"))
            {
                hotMainifestPath  = outPutPath+"/" + GetCurrentManifestFileName();
                if (File.Exists(hotMainifestPath))
                {
                    File.Decrypt(hotMainifestPath);
                }
                //生成热更清单，用来对比MD5和文件下载
                FileHelper.WriteFile(hotMainifestPath, manifestBytes);
                return;
            }
            hotMainifestPath  = Application.dataPath + "/../HotAssets/" + _mBundleModuleName +"/"+ GetCurrentManifestFileName();
            //生成热更清单，用来对比MD5和文件下载
            FileHelper.WriteFile(hotMainifestPath, manifestBytes);
            //备份热更清单，用来版本回退
            File.Copy(hotMainifestPath,outPutPath+GetCurrentManifestFileName());
        }

        /// <summary>
        /// 00 只根据指定目录内的最终文件生成当前模块清单对象，不产生任何文件系统副作用。
        /// </summary>
        private HotAssetsManifest CreateHotAssetsManifest(string contentPath)
        {
            //00 缺失目录时不能生成“空成功”清单，直接提供可操作错误。
            if (string.IsNullOrWhiteSpace(contentPath) || !Directory.Exists(contentPath))
                throw new DirectoryNotFoundException($"生成热更清单时找不到模块目录：{contentPath}");
            //00 保存应用版本、公告、下载地址和运行时保存目录，字段含义与旧实现一致。
            UnityEditor.BuildTarget manifestTarget = mBuildTarget == UnityEditor.BuildTarget.NoTarget
                ? EditorUserBuildSettings.activeBuildTarget
                : mBuildTarget;
            HotAssetsManifest assetsManifest = new HotAssetsManifest
            {
                appVersion = mHotAppVersion,
                targetPlatform = BuildTargetPlatformMapper.ToRuntimeBuildTarget(manifestTarget).ToString(),
                updateNotice = mUpdateNotice,
                downLoadURL = BundleSettings.Instance.AssetBundleDownLoadUrl + "/HotAssets/" + _mBundleModuleName + "/" +
                              mHotAppVersion + "/" + mHotPatchVersion + "/" + mBuildTarget,
                saveFolder = $"HotAssets/{_mBundleModuleName}"
            };
            //00 当前构建只产生一个补丁节点，版本号来自上下文快照。
            HotAssetsPatch hotAssetsPatch = new HotAssetsPatch
            {
                patchVersion = mHotPatchVersion
            };
            //00 文件名排序保证相同输入生成稳定 JSON，不受文件系统枚举顺序影响。
            FileInfo[] bundleInfoArr = new DirectoryInfo(contentPath)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.Name, StringComparer.Ordinal)
                .ToArray();
            foreach (FileInfo bundleInfo in bundleInfoArr)
            {
                //00 MD5 和大小都针对最终加密后的文件，下载端校验与实际发布字节一致。
                HotFileInfo info = new HotFileInfo
                {
                    abName = bundleInfo.Name,
                    md5 = MD5.GetMd5FromFile(bundleInfo.FullName),
                    size = bundleInfo.Length / 1024.0f
                };
                if (manifestTarget == UnityEditor.BuildTarget.WebGL)
                {
                    // Unity Cache 的版本键只要求稳定且随内容变化；复用最终发布字节 MD5 可避免加密/复制后失配。
                    info.bundleHash = info.md5.ToLowerInvariant();
                    if (!BuildPipeline.GetCRCForAssetBundle(bundleInfo.FullName, out info.crc))
                        throw new InvalidDataException($"无法计算 WebGL AssetBundle CRC：{bundleInfo.FullName}");
                }
                //00 每个物理文件只产生一个清单条目。
                hotAssetsPatch.hotAssetsList.Add(info);
            }
            //00 单补丁节点保持现有协议，不引入运行时反序列化迁移。
            assetsManifest.hotAssetsPatchList.Add(hotAssetsPatch);
            assetsManifest.manifestId = MD5.GetMd5FromString(string.Join(
                "|",
                hotAssetsPatch.hotAssetsList.Select(file =>
                    $"{file.abName}:{file.md5}:{file.bundleHash}:{file.crc}")));
            return assetsManifest;
        }

        [MenuItem("ZM/BundleFolder")]
        public static void OpenAssetBundleFolder()
        {
            EditorUtility.RevealInFinder(Application.dataPath + "/../AssetBundle/");
        }
        [MenuItem("ZM/HotBundleFolder")]
        public static void OpenHotBundleFolder()
        {
            EditorUtility.RevealInFinder(Application.dataPath + "/../HotAssets/");
        }

        public static void OpenHotBundleFolder(string moduleName, string appVersion, string patchVersion, UnityEditor.BuildTarget buildTarget)
        {
            string path = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "..",
                "HotAssets",
                string.IsNullOrEmpty(moduleName) ? string.Empty : moduleName,
                string.IsNullOrEmpty(appVersion) ? "0.0.0" : appVersion,
                string.IsNullOrEmpty(patchVersion) ? "0" : patchVersion,
                buildTarget.ToString()));

            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        [MenuItem("ZM/PersistentFolder")]
        public static void OpenPersistentFolder()
        { 
            EditorUtility.RevealInFinder(Application.persistentDataPath+"/");
        }
    }
}
