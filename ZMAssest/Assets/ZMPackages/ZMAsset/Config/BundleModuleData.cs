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
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
///  控制一个资源模块中 Prefab Tab 的统一可主动加载范围。
///  枚举零值保持旧工程行为，旧序列化配置无需迁移即可默认只开放 Prefab。
/// </summary>
public enum PrefabDependencyEntryMode
{
    //00 只有规则直接找到的 Prefab 是公开 Entry，材质和纹理等依赖仅由 Bundle 内部使用。
    PrefabOnly = 0,
    //00 Prefab 及其当前模块内的全部可打包递归依赖都登记为公开 Entry。
    PrefabAndDependencies = 1
}

/// <summary>
///  定义资源模块在模块依赖图中的职责；默认业务模块保持旧工程序列化兼容。
/// </summary>
public enum BundleModuleRole
{
    //00 业务模块可以依赖 Shared 模块，但不能依赖其他业务模块。
    Business = 0,
    //00 Shared 模块只提供公共资源，本身不能反向依赖任何业务模块。
    Shared = 1
}

[System.Serializable]
public class BundleModuleData  
{
    //AssetBundle模块id
    public long bundleid;
    //模块名称
    public string moduleName;
    //是否寻址资源
    public bool isAddressableAsset;
    //是否打包
    public bool isBuild;
    //00 模块角色用于构建期依赖矩阵校验；旧资产缺少字段时自然回落为 Business。
    public BundleModuleRole moduleRole = BundleModuleRole.Business;
   
#if UNITY_EDITOR
    //是否添加模块按钮
    [JsonIgnore]
    public bool isAddModule;
#endif
    
    //上一次点击按钮的时间
    public float lastClickBtnTime;


 
    public string[] prefabPathArr ;

    //00 一个策略统一控制当前模块 Prefab Tab 下的所有搜索目录，不增加逐路径配置复杂度。
    public PrefabDependencyEntryMode prefabDependencyEntryMode = PrefabDependencyEntryMode.PrefabOnly;


    public string[] rootFolderPathArr;

    public BundleFileInfo[] signFolderPathArr;
    
    public string[] sourceFolderPathArr;
    //00 逐文件分包目录配置；目录下每个可打包文件（排除 .prefab/.cs/.meta）独立成为一个 Bundle。
    public string[] singleFilePathArr;
}
[System.Serializable]
public class BundleFileInfo
{
    public string abName="AB Name";
    public string bundlePath="BundlePath...";
}
