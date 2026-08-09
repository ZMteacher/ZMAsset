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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using ZM.ZMAsset;
/// <summary>
/// AssetBundle热更模式
/// </summary>
public enum BundleHotEnum
{ 
    NoHot,
    Hot,
}
/// <summary>
/// 资源加载模式
/// </summary>
public enum LoadAssetEnum
{
    Editor,
    AssetBundle,
}

[CreateAssetMenu(menuName = "AssetsBundleSettings",fileName = "AssetsBundleSettings",order =0)]
public class BundleSettings : ScriptableObject
{
    private static BundleSettings _instance;
    public static BundleSettings Instance
    {
        get
        {

            if (_instance==null)
            {
                _instance = Resources.Load<BundleSettings>("AssetsBundleSettings");
            }
            return _instance;
        } 
    }
 



    [Tooltip("AssetBundle 下载地址")]
    public string AssetBundleDownLoadUrl;

    [Tooltip("AssetBundle 加密设置")]
    public BundleEncryptToggle bundleEncrypt = new BundleEncryptToggle();

    //AssetBundle后缀 例：.ab 建议不加后缀，防止内嵌时Unity读取出错
    [Tooltip("AssetBundle 后缀；建议不设置后缀，避免内嵌时 Unity 读取异常")]
    public string ABSUFFIX = "";

    [Tooltip("资源压缩格式")]
    public BuildAssetBundleOptions buildbundleOptions;

    [Tooltip("资源打包平台")]
    public BuildTarget buildTarget;



    [Tooltip("资源热更模式")]
    public BundleHotEnum bundleHotType;

    [Tooltip("资源加载模式")]
    public LoadAssetEnum loadAssetType;

    [Tooltip("最大下载线程数量")]
    public int MAX_THREAD_COUNT;

    [Tooltip("资源框架总路径节点（基于 Assets 目录），移动框架后需要同步修改")]
    public string ZMAssetRootPath = "ThirdParty/ZMAsset";
    //00 所有业务资源模块统一存放在该目录的同名子目录中，例如 GameOne 对应 Assets/GameData/GameOne。
    //00 该全局约定替代每个模块重复填写根目录，不改变具体打包规则和资源加载策略。
    [Tooltip("资源模块总目录；模块物理目录按‘总目录/模块名称’自动计算")]
    public string ModuleAssetRootPath = "Assets/GameData";
    [Tooltip("AssetBundle 热更文件储存路径")]
    private string HotAssetsPath { get { return Application.persistentDataPath + "/HotAssets/"; } }
    [Tooltip("AssetBundle 解压路径")]
    private string BundleDecompressPath { get { return Application.persistentDataPath + "/DecompressAssets/"; } }

    [Tooltip("AssetBundle 内嵌文件路径")]
    private string BuiltinAssetsPath { get { return Application.streamingAssetsPath + "/AssetBundle/"; } }
    /// <summary>
    /// 获取资源内嵌的路径
    /// </summary>
    /// <param name="moduleName"></param>
    /// <returns></returns>
    public string GetAssetsBuiltinBundlePath(string moduleName)
    {
        return BuiltinAssetsPath + moduleName + "/";
    }
    /// <summary>
    /// 获取解压文件路径(Unity2019 支持直接都streamingAssetsPath目录下Bundle)
    /// </summary>
    /// <param name="moduleName"></param>
    /// <returns></returns>
    public string GetAssetsDecompressPath(string moduleName)
    {
#if UNITY_2020_1_OR_NEWER
        return $"{Application.persistentDataPath}/DecompressAssets/{moduleName.ToString()}/";
#else
        return BundleDecompressPath + moduleName + "/";
#endif
        
    }
    /// <summary>
    /// 获取热更文件储存路径
    /// </summary>
    /// <param name="moduleName"></param>
    /// <returns></returns>
    public string GetHotAssetsPath(string moduleName)
    {
        return HotAssetsPath + moduleName + "/";
    }
    /// <summary>
    /// 获取配置文件名称
    /// </summary>
    /// <param name="moduleName"></param>
    /// <returns></returns>
    public string GetBundleCfgName(string moduleName)
    {
        return $"{moduleName.ToLower()}bundleconfig{ABSUFFIX}";
    }
    /// <summary>
    /// 热更清单文件名称
    /// </summary>
    /// <param name="moduleName"></param>
    /// <returns></ returns>
    public string HotManifestName(string moduleName, BuildTarget target= BuildTarget.NoTarget)
    {
        string platformName = target == BuildTarget.NoTarget ? GetPlatformName() : target.ToString();
        return $"{moduleName}AssetsHotManifest_{platformName}.json";
    }

    /// <summary>
    /// 获取当前运行平台
    /// </summary>
    /// <returns></returns>
    public string GetPlatformName()
    {
        string platformName = Application.platform.ToString();
#if UNITY_ANDROID
            platformName = "Android";
#elif UNITY_IOS
            platformName="iOS";
#elif UNITY_STANDALONE_WIN
            platformName = "Windows";
#elif UNITY_STANDALONE_OSX
            platformName = "MacOS";
#elif UNITY_WEBGL
            platformName = "WebGL";
#endif
        return platformName;
    }

    public void Save()
    {
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
        AssetDatabase.SaveAssetIfDirty(this);
#endif
    }
}

[System.Serializable]
public class BundleEncryptToggle
{
    //是否加密
    public bool isEncrypt;
    [Tooltip("加密密钥")]
    public string encryptKey;
}

public enum BuildTarget
{
    NoTarget = -2, // 0xFFFFFFFE
    //
    // 摘要:
    //     OBSOLETE: Use iOS. Build an iOS player.
    iPhone = -1,
    //
    // 摘要:
    //     Build a macOS standalone (Intel 64-bit).
    StandaloneOSX = 2,
    StandaloneOSXUniversal = 3,
    //
    // 摘要:
    //     Build an iOS player.
    iOS = 9,
    //
    // 摘要:
    //     Build an Android .apk standalone app.
    Android = 13,
    //
    // 摘要:
    //     Build a Linux standalone.
    StandaloneLinux = 17,
    //
    // 摘要:
    //     Build a Windows 64-bit standalone.
    StandaloneWindows64 = 19,
    //
    // 摘要:
    //     Build a WebGL player.
    WebGL = 20,
}

//
// 摘要:
//     Asset Bundle building options.
 
public enum BuildAssetBundleOptions
{
    //
    // 摘要:
    //     Build assetBundle without any special option.
    None = 0,
    //
    // 摘要:
    //     Don't compress the data when creating the asset bundle.
    UncompressedAssetBundle = 1,
    //
    // 摘要:
    //     Includes all dependencies.
    CollectDependencies = 2,
    //
    // 摘要:
    //     Forces inclusion of the entire asset.
    CompleteAssets = 4,
    //
    // 摘要:
    //     Do not include type information within the AssetBundle.
    DisableWriteTypeTree = 8,
    //
    // 摘要:
    //     Builds an asset bundle using a hash for the id of the object stored in the asset
    //     bundle.
    DeterministicAssetBundle = 16,
    //
    // 摘要:
    //     Force rebuild the assetBundles.
    ForceRebuildAssetBundle = 32,
    //
    // 摘要:
    //     Ignore the type tree changes when doing the incremental build check.
    IgnoreTypeTreeChanges = 64,
    //
    // 摘要:
    //     Append the hash to the assetBundle name.
    AppendHashToAssetBundleName = 128,
    //
    // 摘要:
    //     Use chunk-based LZ4 compression when creating the AssetBundle.
    ChunkBasedCompression = 256,
    //
    // 摘要:
    //     Do not allow the build to succeed if any errors are reporting during it.
    StrictMode = 512,
    //
    // 摘要:
    //     Do a dry run build.
    DryRunBuild = 1024,
    //
    // 摘要:
    //     Disables Asset Bundle LoadAsset by file name.
    DisableLoadAssetByFileName = 4096,
    //
    // 摘要:
    //     Disables Asset Bundle LoadAsset by file name with extension.
    DisableLoadAssetByFileNameWithExtension = 8192,
    //
    // 摘要:
    //     Removes the Unity Version number in the Archive File & Serialized File headers
    //     during the build.
    AssetBundleStripUnityVersion = 32768,
    EnableProtection = 65536
}
