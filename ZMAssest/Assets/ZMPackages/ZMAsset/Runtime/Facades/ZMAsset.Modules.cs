using Cysharp.Threading.Tasks;

namespace ZM.ZMAsset
{
    public partial class ZMAsset
    {
        /// <summary>
        /// 资源模块初始化、清理和卸载入口。
        /// </summary>
        public static class Modules
        {
            /// <summary>
            /// 初始化模块配置和依赖图；重复初始化的具体处理由资源管理器保证。
            /// </summary>
            public static UniTask<bool> InitializeAsync(string moduleName)
            {
                ValidateModuleName(moduleName);
                return InitializedInstance.mResource.InitAssetModule(moduleName);
            }

            /// <summary>
            /// 清理指定模块中由框架跟踪的资源，但保留模块配置。
            /// </summary>
            public static UniTask<ModuleClearResult> ClearAsync(string moduleName, ModuleClearMode mode = ModuleClearMode.PooledOnly)
            {
                ValidateModuleName(moduleName);
                return InitializedInstance.mResource.ClearModuleAssetsAsync(moduleName, mode);
            }

            /// <summary>
            /// 清理模块资源并移除模块配置、依赖图与 Shared 租约。
            /// </summary>
            /// <remarks>
            /// ForceTrackedObjects 不负责业务代码持有的裸 Texture、Sprite、AudioClip 或 TextAsset 引用。
            /// </remarks>
            public static UniTask<ModuleUnloadResult> UnloadAsync(string moduleName, ModuleClearMode mode = ModuleClearMode.PooledOnly)
            {
                ValidateModuleName(moduleName);
                return InitializedInstance.mResource.UnloadModuleAssetsAsync(moduleName, mode);
            }
        }
    }
}
