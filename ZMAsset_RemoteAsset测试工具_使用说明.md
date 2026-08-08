# RemoteAsset 测试工具 使用说明

## 功能

ZMUI 运行时窗口，用于测试 ZMAsset 远端资源加载流程：

- **预下载整模块**：串行下载 `RemoteAsset` 模块全部缺失文件，展示整体进度条
- **逐项加载**：逐个加载 9 个远端 icon（`Assets/GameData/RemoteAsset/Itemicon/icon1~9.png`）并显示到列表
- **状态展示**：列表每行显示 图标 / 名称 / 状态（未下载 / 就绪）

## 使用步骤

### 1. 生成窗口预制体

打开 Unity，菜单栏执行：

```
ZM → RemoteAssetTest → 生成测试窗口预制体
```

自动完成：
- 生成 `Assets/Test/RemoteAssetTest/Resources/RemoteAssetTestWindow.prefab`（窗口）
- 生成 `Assets/Test/RemoteAssetTest/ItemPrefab/RemoteAssetTestItem.prefab`（列表行）
- 将 `Assets/Test/RemoteAssetTest/Resources` 注册到 ZMUI `UISetting.WindowPrefabFolderPathArr`（Editor 下 UIModule 初始化时自动扫描生成 WindowConfig）

### 2. 挂载启动器

在场景中创建空物体，挂 `RemoteAssetTestLauncher` 脚本。启动器会自动：
- 补齐 `UICamera` / `UIRoot` / `EventSystem`（缺失时）
- 初始化 UIModule 并弹出测试窗口

### 3. 服务器配置

- 服务器地址读取 `BundleSettings.AssetBundleDownLoadUrl`（当前 `http://192.168.2.113`）
- 本地测试：用 HFS 等工具将资源目录（含 `HotAssets/RemoteAsset/` 目录结构）挂到 `D:\HTTPServer`，并修改 `Assets/ZMPackages/ZMAsset/Resources/AssetsBundleSettings.asset` 中下载地址指向本机

### 4. 运行验证

Play 运行后窗口弹出，操作：

| 操作 | 预期 |
|------|------|
| 点击「预下载整模块」 | 进度条 0→100%，完成后列表状态变"就绪" |
| 点击「逐项加载」 | 图标逐个显示，进度条推进，最终全部"就绪" |
| 点击「关闭」 | 窗口隐藏 |

## 注意事项

- 远端文件需先完成打包并上传服务器；若 `HotAssets/RemoteAsset` 尚未构建，预下载会报错并在 Console 输出模块初始化失败原因
- 测试工具不新增任何 ZMAsset 框架公开接口，仅使用 `ZMAsset.Remote.PreDownloadAsync` / `LoadAsync`
- 模块名常量：`BundleModuleName.RemoteAsset`（已加入框架配置）
- 已加载的 9 个 Texture 由框架缓存，测试场景无需手动释放（可用 `ZMAsset.Modules.ClearAsync` 清理）
- 窗口关闭为隐藏不销毁（ZMUI 行为），再次弹出会刷新列表状态

## 文件清单

```
Assets/Test/RemoteAssetTest/
├── RemoteAssetTestLauncher.cs              # 启动器（场景挂载）
├── RemoteAssetTestWindow.cs                # 窗口逻辑（WindowBase）
├── RemoteAssetTestWindowDataComponent.cs   # 组件绑定（prefab 序列化引用）
├── RemoteAssetTestItem.cs                  # 列表行（IZMUIViewListItem）
├── RemoteAssetTestItemData.cs              # 数据（9 个 icon 硬编码）
├── Editor/
│   └── RemoteAssetTestWindowGenerator.cs   # 一键生成 prefab（MenuItem）
├── Resources/
│   └── RemoteAssetTestWindow.prefab        # 生成器输出（运行时加载）
└── ItemPrefab/
    └── RemoteAssetTestItem.prefab          # 生成器输出（列表行）
```
