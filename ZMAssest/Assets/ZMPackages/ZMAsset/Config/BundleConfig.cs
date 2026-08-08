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
using UnityEngine;

[System.Serializable]
public class BundleConfig  
{
    /// <summary>
    /// 00 配置协议版本 2 首次加入带模块信息的 Bundle 依赖。
    /// 00 字段初始化器会让缺失该字段的旧 JSON 在部分反序列化器中得到 2，因此运行时还会检查 v2 载荷字段，不能只看版本号。
    /// </summary>
    public int formatVersion = 2;
    /// <summary>
    /// 00 当前配置所属模块，便于运行时在空配置和诊断场景中仍能确定所有者。
    /// </summary>
    public string moduleName;
    /// <summary>
    /// 00 当前模块依赖的其他资源模块；新运行时用它建立初始化顺序和租约图。
    /// </summary>
    public List<string> moduleDependencies = new List<string>();
    /// <summary>
    /// 所有AssetBundle的信息列表
    /// </summary>
    public List<BundleInfo> bundleInfoList;
}
[System.Serializable]
/// <summary>
/// AssetBundle信息
/// </summary>
public class BundleInfo
{
    /// <summary>
    /// 文件路径
    /// </summary>
    public string path;
    /// <summary>
    /// Crc
    /// </summary>
    public uint crc;
    /// <summary>
    /// AssetBundle名称
    /// </summary>
    public string bundleName;
    /// <summary>
    /// 资源名字
    /// </summary>
    public string assetName;
    /// <summary>
    /// AB模块
    /// </summary>
    public string bundleModule;
    /// <summary>
    /// 是否寻址资源
    /// </summary>
    public bool isAddressableAsset;
    /// <summary>
    /// 是否允许通过资源路径直接加载
    /// </summary>
    //00 旧版本配置没有该字段，默认 true 可保持旧配置的加载兼容性；新配置会明确区分 Entry 与仅依赖资源。
    public bool isLoadableEntry = true;
    /// <summary>
    /// 依赖项
    /// </summary>
    public List<string> bundleDependce;
    /// <summary>
    /// 00 协议版本 2 的完整 Bundle 依赖身份，跨模块依赖不再依赖“主资源模块目录”这一隐含假设。
    /// </summary>
    public List<BundleDependencyInfo> bundleDependencies;
}

/// <summary>
/// 00 使用“模块 + Bundle 名称”描述一个物理 Bundle 依赖。
/// </summary>
[System.Serializable]
public class BundleDependencyInfo
{
    /// <summary>
    /// 00 依赖 Bundle 的真实所属模块。
    /// </summary>
    public string bundleModule;
    /// <summary>
    /// 00 依赖 Bundle 的文件名，包含框架配置的 AssetBundle 后缀。
    /// </summary>
    public string bundleName;
}
/// <summary>
/// 内嵌的AssetBundle的信息
/// </summary>
public class BuiltinBundleInfo
{
    public string fileName;

    public string md5;//校验本地以解压文件是否与包内文件一致，如果不一致，说明本地文件被篡改，
                      //我们需要进行重新解压（需要进行校验的前提是 当前解压的模块没有开启热更）

    public float size;//文件大小 用来计算文件解压进度显示
}
