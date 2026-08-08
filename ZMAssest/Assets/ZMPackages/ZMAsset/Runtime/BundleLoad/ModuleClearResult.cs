using System;

namespace ZM.ZMAsset
{
    public enum ModuleClearMode
    {
        PooledOnly = 0,
        ForceTrackedObjects = 1
    }

    public enum ModuleClearStatus
    {
        Success = 0,
        InvalidModule = 1,
        NotInitialized = 2,
        Busy = 3,
        InUse = 4,
        Failed = 5,
        //00 Shared 仍被一个或多个活动业务模块租用，当前清理会破坏其依赖资源。
        DependencyInUse = 6
    }

    /// <summary>
    /// 指定资源模块的清理结果。
    /// </summary>
    public sealed class ModuleClearResult
    {
        public string bundleModule;
        public ModuleClearStatus status;
        public int destroyedObjectCount;
        public int releasedAssetCount;
        public int releasedAtlasCount;
        public string message;
        public Exception exception;
    }

    /// <summary>
    /// 00 模块配置卸载状态与资源缓存清理状态分离，旧 ClearModuleAssetsAsync 语义保持不变。
    /// </summary>
    public enum ModuleUnloadStatus
    {
        Success = 0,
        InvalidModule = 1,
        NotInitialized = 2,
        Busy = 3,
        InUse = 4,
        //00 仍有已初始化业务模块声明依赖目标模块，必须先卸载这些业务模块。
        DependentModuleInitialized = 5,
        Failed = 6
    }

    /// <summary>
    /// 00 描述“清理资源缓存并移除模块配置”的完整卸载结果。
    /// </summary>
    public sealed class ModuleUnloadResult
    {
        public string bundleModule;
        public ModuleUnloadStatus status;
        public ModuleClearResult clearResult;
        public string message;
        public Exception exception;
    }
}
