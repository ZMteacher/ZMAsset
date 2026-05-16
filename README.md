# ZMAsset

ZMAsset 是一个**面向 Unity 的高性能资源管理框架**，专为中大型游戏项目打造。

**核心特点：** 📦 可视化多模块打包 | 🔥 多模块热更新 | ⚡ 多线程下载 | 🔒 加密解密 | 🧠 内存引用计数 | 🎮 大型对象池

---

## ✨ 功能概览

| 功能 | 说明 |
|------|------|
| 📦 **可视化多模块打包** | 通过 ScriptableObject 配置，Editor 下一键打包，支持按模块独立打包 |
| 🔥 **多模块热更新** | 支持多个资源模块独立热更，互不干扰 |
| ⬇️ **多线程下载** | 可配置最大线程数，提升下载速度 |
| 🔁 **多版本热更 & 回退** | 支持多版本资源管理，异常时可回退至历史版本 |
| 🔒 **加密 / 解密** | AssetBundle 打包时可开启加密，运行时自动解密 |
| 📁 **内嵌 & 解压** | 支持资源内嵌至 StreamingAssets，首次运行自动解压 |
| 🧠 **内存引用计数** | 精确追踪资源引用，自动释放无引用资源，防止内存泄漏 |
| 🏊 **大型对象池** | 统一管理 GameObject 复用，降低 GC 压力 |
| 🗂️ **AssetBundle 加载** | 运行时从 AssetBundle 加载资源，支持同步 / 异步 |
| 🛠️ **Editor 加载** | Editor 模式下直接从工程加载资源，无需打包，开发体验流畅 |
| 🔖 **Addressable 资源系统** | 内置轻量级 Addressable 资源寻址，支持 async/await 加载 |

---

## 🚀 快速开始

### 1. 初始化框架

```csharp
using ZM.ZMAsset;

public class GameLauncher : MonoBehaviour
{
    void Awake()
    {
        ZMAsset.InitFrameWork();
    }
}
```

### 2. 加载资源

```csharp
// 同步加载 Sprite
Sprite icon = ZMAsset.LoadSprite("Assets/Art/Icons/hero.png");

// 同步加载 Texture
Texture tex = ZMAsset.LoadTexture("Assets/Art/Textures/bg");

// 同步加载音频
AudioClip clip = ZMAsset.LoadAudio("Assets/Audio/bgm.mp3");

// 可等待的异步加载 Sprite
Sprite sprite = await ZMAsset.LoadSpriteAsync("Assets/Art/Icons/hero.png");

// 可等待的异步加载 Texture
Texture texture = await ZMAsset.LoadTextureAsync("Assets/Art/Textures/bg");
```

### 3. 对象实例化 & 对象池

```csharp
// 同步实例化（自动走对象池）
GameObject hero = ZMAsset.InstantiateObject("Assets/Prefabs/Hero", parent);

// 带位置/旋转/缩放的同步实例化
GameObject hero = ZMAsset.InstantiateObject(
    "Assets/Prefabs/Hero", parent,
    Vector3.zero, Vector3.one, Quaternion.identity);

// 可等待的异步实例化
AssetsRequest req = await ZMAsset.InstantiateObjectAsync("Assets/Prefabs/Hero", parent);
GameObject obj = req.obj;

// 异步回调实例化
ZMAsset.InstantiateObjectAsync("Assets/Prefabs/Hero", parent,
    (go, p1, p2) => { /* 加载完成回调 */ });

// 释放对象（回收到对象池）
ZMAsset.Release(hero);

// 销毁对象并释放内存
ZMAsset.Release(hero, destroy: true);
```

### 4. 预加载

```csharp
// 预加载对象（填充对象池）
ZMAsset.PreLoadObjct("Assets/Prefabs/Bullet", count: 20);

// 可等待的异步预加载
await ZMAsset.PreLoadObjectAsync<GameObject>("Assets/Prefabs/Bullet", count: 20);

// 预加载资源
ZMAsset.PreLoadResource<Texture>("Assets/Art/Textures/bg");
```

### 5. Addressable 寻址加载

```csharp
using ZM.ZMAsset;

// 从 Addressable 模块异步实例化对象（对象池）
AssetsRequest asset = await ZMAddressableAsset.InstantiateAsyncFormPool(
    AssetsPathConfig.GAME_ITEM_PATH + "6013/6013",
    parent,
    BundleModuleName.AdressAsset);

// 异步加载 Texture
Texture tex = await ZMAddressableAsset.LoadResourceAsync<Texture>(
    AssetsPathConfig.GAME_ITEM_PATH + "6001/huafei.png",
    BundleModuleName.AdressAsset);
```

---

## 🔥 热更新

### 热更流程

```csharp
// 1. 检测资源版本（获取待更新大小）
ZMAsset.CheckAssetsVersion(BundleModuleName.AdressAsset, (isHot, sizeMB) =>
{
    if (isHot)
        Debug.Log($"需要更新，大小：{sizeMB:F2} MB");
});

// 2. 开始热更
ZMAsset.HotAssets(
    bundleModule:         BundleModuleName.AdressAsset,
    startHotCallBack:     (module) => Debug.Log("开始热更..."),
    hotFinish:            (module) => Debug.Log("热更完成！"),
    waiteDownLoad:        null,
    isCheckAssetsVersion: false);

// 3. 热更完成后初始化资源模块
bool success = await ZMAsset.InitAssetsModule(BundleModuleName.AdressAsset);
```

### 使用 HotUpdateManager（推荐）

```csharp
// 一行代码完成解压 + 热更 + 初始化全流程
HotUpdateManager.Instance.HotAndUnPackAssets(BundleModuleName.AdressAsset, () =>
{
    Debug.Log("资源就绪，进入游戏！");
});
```

---

## ⚙️ 配置说明

通过 **AssetsBundleSettings**（ScriptableObject）进行框架配置：

| 配置项 | 说明 |
|--------|------|
| `AssetBundleDownLoadUrl` | AssetBundle 热更下载地址 |
| `loadAssetType` | 资源加载模式：`Editor`（开发）/ `AssetBundle`（发布）|
| `bundleHotType` | 热更模式：`NoHot` / `Hot` |
| `bundleEncrypt` | 是否启用 AssetBundle 加密及密钥 |
| `buildbundleOptions` | 打包压缩格式（None / LZ4 / LZMA 等）|
| `buildTarget` | 打包目标平台（Android / iOS / Windows 等）|
| `MAX_THREAD_COUNT` | 最大并发下载线程数 |
| `ABSUFFIX` | AssetBundle 文件后缀（默认为空）|

> 在 `Assets/Resources/` 下创建 `AssetsBundleSettings.asset` 即可，框架会自动加载。

---

## 📁 目录结构

```
Assets/
└── ZMPackages/
    └── ZMAsset/
        ├── Runtime/
        │   ├── ZMAsset.cs              # 框架主入口（MonoSingleton）
        │   ├── ZMAsset.Interface.cs    # 所有公开 API
        │   ├── BundleLoad/             # AssetBundle & Editor 资源加载
        │   ├── BundleHot/              # 热更新、下载、解压
        │   ├── Addressable/            # Addressable 寻址系统
        │   ├── Helper/                 # 工具类
        │   └── OriginData/             # 数据定义
        ├── Config/
        │   ├── BundleSettings.cs       # 框架全局配置
        │   ├── BundleModuleName.cs     # 模块名称常量
        │   └── BundleModuleData.cs     # 模块数据
        ├── Editor/
        │   └── BundleBuild/            # 可视化打包工具
        └── Example/                    # 使用示例
```

---

## 📋 环境要求

| 组件 | 版本要求 |
|------|----------|
| **Unity** | 2020.1+ |
| **UniTask** | [Cysharp/UniTask](https://github.com/Cysharp/UniTask) |
| **Odin Inspector** | 用于 Editor 可视化配置（可选）|
| **Newtonsoft.Json** | 热更清单序列化 |

---

## 💬 联系与支持

- **QQ**: 975659933
- **邮箱**: zhumengxyedu@163.com
- **教学网站**: [www.yxtown.com](http://www.yxtown.com)
- **GitHub Issues**: [提交问题](../../issues)

---

## 🙏 致谢

感谢所有使用和支持 ZMAsset 的开发者！

---

### 🎉 如果 ZMAsset 对你有帮助，请给个 Star ⭐

**Built with ❤️ by 铸梦xy | Made for Unity Game Developers**

[⬆ 回到顶部](#zmAsset)
