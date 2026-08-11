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

using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace ZM.Asset
{
    public class BundleItem
    {
        /// <summary>
        /// 文件加载路径
        /// </summary>
        public string path;
        /// <summary>
        /// 文件加载路径crc
        /// </summary>
        public uint crc;
        /// <summary>
        /// AssetBundle名称
        /// </summary>
        public string bundleName;
        /// <summary>
        /// 资源名称
        /// </summary>
        public string assetName;
        /// <summary>
        /// 是否允许在本地缺失时从远端按需下载
        /// </summary>
        public bool isRemoteAsset;
        /// <summary>
        /// AssetBundle所属的模块
        /// </summary>
        public string bundleModuleType;
        /// <summary>
        /// AssetBundle依赖项
        /// </summary>
        public List<string> bundleDependce;
        /// <summary>
        /// 协议版本 2 的完整依赖身份；加载与释放必须优先使用模块名和 Bundle 名组合键。
        /// </summary>
        public List<ModuleBundleKey> bundleDependencies;
        /// <summary>
        /// AssetBundle
        /// </summary>
        public AssetBundle assetBundle;
        /// <summary>
        /// 通过AssetBundle加载出的对象
        /// </summary>
        public UnityEngine.Object obj;
        /// <summary>
        /// 通过AssetBundle加载出的对象数组
        /// </summary>
        public UnityEngine.Object[] objArr;
        /// <summary>
        /// 主资源缓存持有状态，仅允许 0（未持有）或 1（已持有）
        /// </summary>
        //00 GameObject 实例数量由 ResourceManager 的实例索引维护，不再混入该字段。
        public int refCount;
    }

    /// <summary>
    /// AssetBundle缓存
    /// </summary>
    public class AssetBundleCache
    {
        /// <summary>
        /// AssetBundle对象
        /// </summary>
        public AssetBundle assetBundle;
        /// <summary>
        /// AssetBundle引用计数
        /// </summary>
        public int referenceCount;

        public void Release()
        {
            assetBundle = null;
            referenceCount = 0;
        }
    }


    //加载----配置表不存在---无法查询到该资源是哪个文件----失败。
    //检测资源版本---计算需要热更的文件---初始化配置
    //初始化成功---加载资源---配置表中查询命中---加载对应AB---本地不存在---自动开启下载---下载完成---回调出去
    //初始化失败---加载本地已存在配置---配置表查询命---加载对应AB---本地不存在---自动开启下载---下载完成---回调出去

    public class AssetBundleManager : Singleton<AssetBundleManager>
    {
        /// <summary>
        /// 已经加载的资源模块
        /// </summary>
        private List<string> mAlreadyLoadBundleModuleList = new List<string>();
        /// <summary>
        /// 所有模块的AssetBundle的资源对象字典
        /// </summary>
        private Dictionary<uint, BundleItem> mAllBundleAssetDic = new Dictionary<uint, BundleItem>();

        /// <summary>
        /// 所有模块的已经加载过的AssetBundle的资源对象字典
        /// </summary>
        private readonly Dictionary<ModuleBundleKey, AssetBundleCache> mAllAlreadyLoadBundleDic =
            new Dictionary<ModuleBundleKey, AssetBundleCache>();
 
        /// <summary>
        /// AssetBundle类对象池
        /// </summary>
        public ClassObjectPool<AssetBundleCache> mBundleCachePool = new ClassObjectPool<AssetBundleCache>(100);
        /// </summary>
        /// 异步加载AssetBundle字典
        /// </summary>
        private readonly Dictionary<ModuleBundleKey, UniTaskCompletionSource> mAsyncLoadBundleActionDic =
            new Dictionary<ModuleBundleKey, UniTaskCompletionSource>();
        /// <summary>
        /// 按资源 CRC 合并 BundleItem 初始化，避免同一资源并发请求重复持有底层 Bundle。
        /// </summary>
        private readonly Dictionary<uint, UniTaskCompletionSource<BundleItem>> mAsyncLoadBundleItemActionDic =
            new Dictionary<uint, UniTaskCompletionSource<BundleItem>>();
        /// <summary>
        /// 远端资源先合并下载阶段，再进入统一的 BundleItem 初始化入口。
        /// </summary>
        private readonly Dictionary<uint, UniTaskCompletionSource<BundleItem>> mRemoteBundleLoadTasks =
            new Dictionary<uint, UniTaskCompletionSource<BundleItem>>();
      
        /// <summary>
        /// 异步锁，处理异步时多个配置同时初始化，资源竞争问题
        /// </summary>
        private readonly object mLock = new object();
        /// <summary>
        /// 配置初始化、热重载和模块清理共用一个异步写锁。
        /// </summary>
        //00 这里只重命名现有锁并扩展职责，严禁为模块清理另建第二把锁。
        private readonly SemaphoreSlim mModuleMutationLock = new SemaphoreSlim(1, 1);

        private readonly HashSet<string> mModulesBeingCleared =
            new HashSet<string>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> mModuleInFlightLoadCountDic =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        ///  消费模块到直接依赖模块的配置图；图关系在模块初始化提交时一次性替换。
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> mModuleDependencies =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>
        ///  依赖模块到当前活动消费者的租约反向图；Shared 清理依据它拒绝提前释放。
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> mActiveModuleDependents =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>
        ///  标记当前已取得全部依赖租约的模块；成功清理会停用，下一次资源操作再原子激活。
        /// </summary>
        private readonly HashSet<string> mActiveModuleLeases =
            new HashSet<string>(StringComparer.Ordinal);

        #region 资源清单配置初始化
        /// <summary>
        /// 判断模块配置是否已经加载到内存。
        /// </summary>
        public bool IsAssetModuleInitialized(string bundleModule)
        {
            lock (mLock)
            {
                return mAlreadyLoadBundleModuleList.Contains(bundleModule);
            }
        }

        /// <summary>
        /// 查询指定物理 Bundle 当前是否已经驻留内存。该方法不读取磁盘、不发起网络请求，也不改变引用计数。
        /// </summary>
        internal bool IsBundleLoaded(ModuleBundleKey bundleKey)
        {
            lock (mLock)
            {
                return mAllAlreadyLoadBundleDic.TryGetValue(bundleKey, out AssetBundleCache bundleCache) &&
                       bundleCache?.assetBundle != null;
            }
        }

        internal bool TryBeginModuleLoad(string bundleModule)
        {
            if (string.IsNullOrWhiteSpace(bundleModule)) return true;
            lock (mLock)
            {
                if (mModulesBeingCleared.Contains(bundleModule)) return false;
                // 清理成功后的业务模块仍保留配置；首次新加载必须先原子恢复全部 Shared 租约。
                if (!TryActivateModuleLeaseLocked(bundleModule, out string activationError))
                {
                    Debug.LogError($"模块 {bundleModule} 无法恢复依赖租约：{activationError}");
                    return false;
                }
                mModuleInFlightLoadCountDic.TryGetValue(bundleModule, out int count);
                mModuleInFlightLoadCountDic[bundleModule] = count + 1;
                return true;
            }
        }

        internal void EndModuleLoad(string bundleModule)
        {
            if (string.IsNullOrWhiteSpace(bundleModule)) return;
            lock (mLock)
            {
                if (!mModuleInFlightLoadCountDic.TryGetValue(bundleModule, out int count)) return;
                if (count <= 1) mModuleInFlightLoadCountDic.Remove(bundleModule);
                else mModuleInFlightLoadCountDic[bundleModule] = count - 1;
            }
        }

        /// <summary>
        /// 获取模块变更锁并原子进入 Clearing；成功后必须调用 EndModuleClear。
        /// </summary>
        internal async UniTask<ModuleClearStatus> BeginModuleClearAsync(string bundleModule)
        {
            if (string.IsNullOrWhiteSpace(bundleModule)) return ModuleClearStatus.InvalidModule;

            await mModuleMutationLock.WaitAsync();
            try
            {
                await UniTask.SwitchToMainThread();
                lock (mLock)
                {
                    if (!mAlreadyLoadBundleModuleList.Contains(bundleModule))
                    {
                        mModuleMutationLock.Release();
                        return ModuleClearStatus.NotInitialized;
                    }
                    //00 Shared 仍被活动业务模块租用时禁止清理，避免已加载材质/纹理在消费者运行中变白。
                    if (HasActiveDependentsLocked(bundleModule))
                    {
                        mModuleMutationLock.Release();
                        return ModuleClearStatus.DependencyInUse;
                    }
                    if (mModulesBeingCleared.Contains(bundleModule) ||
                        (mModuleInFlightLoadCountDic.TryGetValue(bundleModule, out int count) && count > 0) ||
                        HasModuleAsyncLoad(bundleModule))
                    {
                        mModuleMutationLock.Release();
                        return ModuleClearStatus.Busy;
                    }

                    mModulesBeingCleared.Add(bundleModule);
                    return ModuleClearStatus.Success;
                }
            }
            catch
            {
                //00 主线程切换或预检异常时必须归还同一把模块变更锁，避免后续初始化和重载永久阻塞。
                mModuleMutationLock.Release();
                throw;
            }
        }

        internal void EndModuleClear(string bundleModule)
        {
            lock (mLock)
            {
                mModulesBeingCleared.Remove(bundleModule);
            }
            mModuleMutationLock.Release();
        }

        private bool HasModuleAsyncLoad(string bundleModule)
        {
            //00 异步任务字典与加载、清理和重载流程并发读写，遍历必须持有同一把状态锁。
            lock (mLock)
            {
                foreach (uint crc in mAsyncLoadBundleItemActionDic.Keys)
                {
                    if (mAllBundleAssetDic.TryGetValue(crc, out BundleItem item) &&
                        string.Equals(item.bundleModuleType, bundleModule, StringComparison.Ordinal)) return true;
                }
                foreach (uint crc in mRemoteBundleLoadTasks.Keys)
                {
                    if (mAllBundleAssetDic.TryGetValue(crc, out BundleItem item) &&
                        string.Equals(item.bundleModuleType, bundleModule, StringComparison.Ordinal)) return true;
                }
                foreach (ModuleBundleKey bundleKey in mAsyncLoadBundleActionDic.Keys)
                {
                    //00 Bundle 异步任务已经携带模块身份，可直接判断而不再扫描全部资源配置。
                    if (string.Equals(bundleKey.ModuleName, bundleModule, StringComparison.Ordinal)) return true;
                }
                return false;
            }
        }

        /// <summary>
        ///  在 mLock 内原子取得当前模块的全部直接依赖租约。
        /// </summary>
        private bool TryActivateModuleLeaseLocked(string bundleModule, out string error)
        {
            error = string.Empty;
            //00 未初始化模块没有可恢复的依赖图；调用方可能处于 Editor 直读兼容路径，保持原行为。
            if (!mAlreadyLoadBundleModuleList.Contains(bundleModule)) return true;
            //00 已经活动时不重复增加反向依赖者，模块租约是 0/1 状态而不是计数器。
            if (mActiveModuleLeases.Contains(bundleModule)) return true;
            //00 无依赖模块同样标记活动，清理成功后才能一致地进入停用状态。
            if (!mModuleDependencies.TryGetValue(bundleModule, out HashSet<string> dependencies) ||
                dependencies.Count == 0)
            {
                mActiveModuleLeases.Add(bundleModule);
                return true;
            }

            //00 必须先验证全部依赖均已初始化且不在清理，再一次性写入反向图，禁止半取得租约。
            foreach (string dependencyModule in dependencies)
            {
                if (!mAlreadyLoadBundleModuleList.Contains(dependencyModule))
                {
                    error = $"依赖模块尚未初始化：{dependencyModule}";
                    return false;
                }
                if (mModulesBeingCleared.Contains(dependencyModule))
                {
                    error = $"依赖模块正在清理：{dependencyModule}";
                    return false;
                }
            }
            foreach (string dependencyModule in dependencies)
            {
                if (!mActiveModuleDependents.TryGetValue(
                        dependencyModule,
                        out HashSet<string> dependents))
                {
                    dependents = new HashSet<string>(StringComparer.Ordinal);
                    mActiveModuleDependents.Add(dependencyModule, dependents);
                }
                dependents.Add(bundleModule);
            }
            mActiveModuleLeases.Add(bundleModule);
            return true;
        }

        /// <summary>
        ///  在 mLock 内释放当前模块取得的全部依赖租约；重复停用不会重复减租约。
        /// </summary>
        private void DeactivateModuleLeaseLocked(string bundleModule)
        {
            if (!mActiveModuleLeases.Remove(bundleModule)) return;
            if (!mModuleDependencies.TryGetValue(bundleModule, out HashSet<string> dependencies)) return;
            foreach (string dependencyModule in dependencies)
            {
                if (!mActiveModuleDependents.TryGetValue(
                        dependencyModule,
                        out HashSet<string> dependents)) continue;
                dependents.Remove(bundleModule);
                if (dependents.Count == 0) mActiveModuleDependents.Remove(dependencyModule);
            }
        }

        /// <summary>
        ///  成功清理业务资源后停用模块租约；调用时 BeginModuleClear 仍持有模块变更锁。
        /// </summary>
        internal void DeactivateModuleAfterClear(string bundleModule)
        {
            lock (mLock)
            {
                DeactivateModuleLeaseLocked(bundleModule);
            }
        }

        /// <summary>
        ///  全局深度清理成功后停用全部模块租约；配置仍保留，下一次加载会按需重新激活。
        /// </summary>
        internal void DeactivateAllModulesAfterGlobalClear()
        {
            lock (mLock)
            {
                //00 复制集合后逐个停用，避免 Deactivate 修改 mActiveModuleLeases 时破坏枚举器。
                List<string> activeModules = new List<string>(mActiveModuleLeases);
                foreach (string moduleName in activeModules) DeactivateModuleLeaseLocked(moduleName);
            }
        }

        /// <summary>
        ///  判断目标模块当前是否仍被活动消费者租用。
        /// </summary>
        private bool HasActiveDependentsLocked(string bundleModule)
        {
            return mActiveModuleDependents.TryGetValue(bundleModule, out HashSet<string> dependents) &&
                   dependents.Count > 0;
        }

        /// <summary>
        ///  判断是否仍有已初始化模块声明依赖目标模块；模块卸载和重载使用更严格的配置级门禁。
        /// </summary>
        private bool HasInitializedDependentsLocked(string bundleModule, out string dependentModules)
        {
            List<string> dependents = new List<string>();
            foreach (KeyValuePair<string, HashSet<string>> pair in mModuleDependencies)
            {
                if (mAlreadyLoadBundleModuleList.Contains(pair.Key) && pair.Value.Contains(bundleModule))
                    dependents.Add(pair.Key);
            }
            dependents.Sort(StringComparer.Ordinal);
            dependentModules = string.Join(", ", dependents);
            return dependents.Count > 0;
        }

        /// <summary>
        ///  热更新磁盘切换前查询模块是否允许变更配置；有已初始化消费者时必须拒绝 Shared 在线升级。
        /// </summary>
        internal bool CanMutateModuleConfiguration(string bundleModule, out string reason)
        {
            lock (mLock)
            {
                if (HasInitializedDependentsLocked(bundleModule, out string dependents))
                {
                    reason = $"模块 {bundleModule} 仍被已初始化模块依赖：{dependents}";
                    return false;
                }
                reason = string.Empty;
                return true;
            }
        }

        /// <summary>
        /// 加载AssetBundle配置文件
        /// </summary>
        /// <param name="bundleModule">资源模块</param>
        /// <returns></returns>
        public async UniTask<bool> InitializeAssetModule(string bundleModule)
        {
            await EnsureWebGLActiveSnapshotLoadedAsync();
            await mModuleMutationLock.WaitAsync();
            try
            {
                // Unity AssetBundle API 必须回到主线程执行，等待写锁后显式恢复线程归属。
                await UniTask.SwitchToMainThread();
                return await InitializeAssetModuleInternal(bundleModule);
            }
            finally
            {
                mModuleMutationLock.Release();
            }
        }

        private async UniTask<bool> InitializeAssetModuleInternal(string bundleModule)
        {
            // 整个依赖闭包共享访问集合和新增模块列表，递归初始化不会再次获取 mModuleMutationLock。
            HashSet<string> visitingModules = new HashSet<string>(StringComparer.Ordinal);

            List<string> newlyInitializedModules = new List<string>();

            bool succeeded = await InitializeAssetModuleRecursive(bundleModule, visitingModules, newlyInitializedModules);

            if (succeeded) return true;
            //任一依赖或主模块失败时按相反顺序撤销本轮新提交配置和租约，保持全有或全无。
            lock (mLock)
            {
                for (int index = newlyInitializedModules.Count - 1; index >= 0; index--)
                    RemoveModuleConfigurationLocked(newlyInitializedModules[index]);
            }
            return false;
        }

        /// <summary>
        /// 在已持有模块变更锁的前提下递归初始化依赖闭包；本方法绝不再次等待同一 SemaphoreSlim。
        /// </summary>
        private async UniTask<bool> InitializeAssetModuleRecursive(string bundleModule, HashSet<string> visitingModules, List<string> newlyInitializedModules)
        {
            Debug.Log($"InitializeAssetModuleRecursive :{bundleModule}");
            AssetBundle bundleConfig = null;
            try
            {
                // 模块名称是目录、图节点和 BundleKey 的共同身份，入口统一 Trim 并拒绝空值。
                bundleModule = bundleModule?.Trim();
                if (string.IsNullOrWhiteSpace(bundleModule))
                    throw new InvalidDataException("初始化 AssetBundle 模块时模块名称不能为空。");
                lock (mLock)
                {
                    if (mAlreadyLoadBundleModuleList.Contains(bundleModule))
                    {
                        // 依赖递归和重复业务初始化都采用幂等成功，避免初始化顺序改变结果。
                        Debug.Log("该模块配置文件已经加载：" + bundleModule);
                        return true;
                    }
                }
                // visiting 集合在读取磁盘前检测循环，诊断中输出当前闭包而不是最终栈溢出。
                if (!visitingModules.Add(bundleModule))
                    throw new InvalidDataException($"模块依赖图存在循环：{string.Join(" -> ", visitingModules)} -> {bundleModule}");

                //处理异步时多个配置同时初始化，导致字段数据错乱问题
                string assetBundleName = bundleModule.ToString().ToLower() + "assetbundleconfig";
                string mBundleConfigName = bundleModule.ToString().ToLower() + "bundleconfig"+ BundleSettings.Instance.ABSUFFIX;
                string mBundleConfigPath = BundleSettings.Instance.GetHotAssetsPath(bundleModule) + mBundleConfigName;
                AssetRuntimeBackend runtimeBackend = AssetRuntimeBackendFactory.Current;

                //获取当前模块配置文件所在的路径
                AssetBundleLocation webRemoteConfiguration = default;
                bool hasWebRemoteConfiguration =
                    runtimeBackend.PlatformKind == AssetRuntimePlatformKind.WebGL &&
                    (WebGLActiveAssetRegistry.TryResolve(
                         bundleModule,
                         mBundleConfigName,
                         out webRemoteConfiguration) ||
                     RemoteAssetSystem.Instance.TryResolveRemoteLocation(
                         bundleModule,
                         mBundleConfigName,
                         out webRemoteConfiguration));
                if (!hasWebRemoteConfiguration &&
                    !GeneratorBundleConfigPath(bundleModule, mBundleConfigName, ref mBundleConfigPath))
                {
                    Debug.LogWarning("AssetBundleConfig Not find.  Load AssetBundle failed!"+ bundleModule);
                    return false;
                }


                Debug.Log($"LoadBundleManifest :{mBundleConfigPath}");
                bool isConfigurationEncrypted = BundleSettings.Instance.bundleEncrypt.isEncrypt;
                string hotConfigurationPath =
                    BundleSettings.Instance.GetHotAssetsPath(bundleModule) + mBundleConfigName;
                AssetBundleSourceKind configurationSource = string.Equals(
                    mBundleConfigPath,
                    hotConfigurationPath,
                    StringComparison.OrdinalIgnoreCase)
                    ? AssetBundleSourceKind.HotUpdate
                    : AssetBundleSourceKind.Builtin;
                AssetBundleLocation configurationLocation = hasWebRemoteConfiguration
                    ? webRemoteConfiguration
                    : new AssetBundleLocation(
                        bundleModule,
                        mBundleConfigName,
                        configurationSource,
                        mBundleConfigPath,
                        null,
                        0,
                        isConfigurationEncrypted);
                AssetBundleLoadRequest configurationRequest = new AssetBundleLoadRequest(
                    configurationLocation,
                    AssetBundleLoadPurpose.Configuration,
                    BundleSettings.Instance.bundleEncrypt.encryptKey);

                // 平台加载器只替代“如何读取 Bundle”；模块依赖闭包和配置提交仍由当前管理器维护。
                bundleConfig = await runtimeBackend.BundleLoader.LoadAsync(configurationRequest);

                if (bundleConfig == null)
                    throw new InvalidDataException($"无法加载模块配置 AssetBundle：{mBundleConfigPath}");
                TextAsset bundleConfigAsset =
                    await bundleConfig.LoadAssetAsync<TextAsset>(assetBundleName) as TextAsset;
                if (bundleConfigAsset == null)
                    throw new InvalidDataException($"模块配置中缺少 TextAsset：{assetBundleName}");

                string bundleConfigJson = bundleConfigAsset.text;
                ParsedModuleConfig parsedConfig =
                    await runtimeBackend.Scheduler.RunCpuBoundAsync(() =>
                        ParseModuleConfig(bundleConfigJson, bundleModule));

                // 先递归初始化全部依赖，再提交消费者；Shared→Business 或环会在协议/访问集合处失败。
                foreach (string dependencyModule in parsedConfig.ModuleDependencies)
                {
                    bool dependencySucceeded = await InitializeAssetModuleRecursive(
                        dependencyModule,
                        visitingModules,
                        newlyInitializedModules);
                    if (!dependencySucceeded)
                        throw new InvalidDataException(
                            $"模块 {bundleModule} 的依赖模块初始化失败：{dependencyModule}");
                }

                lock (mLock)
                {
                    // 并发初始化可能在本次解析期间先完成；同一磁盘配置视为共同成功，禁止重复写入模块标记。
                    if (mAlreadyLoadBundleModuleList.Contains(bundleModule))
                        return true;

                    //提交前先检查全部冲突，禁止旧的 first-wins 行为造成模块初始化结果依赖加载顺序。
                    foreach (KeyValuePair<uint, BundleItem> pair in parsedConfig.BundleItems)
                    {
                        if (!mAllBundleAssetDic.TryGetValue(pair.Key, out BundleItem existingItem)) continue;

                        string conflictType = string.Equals(NormalizeAssetPath(existingItem.path), NormalizeAssetPath(pair.Value.path), StringComparison.Ordinal) ? "Entry 重复归属" : "CRC32 真碰撞";

                        throw new InvalidDataException($"模块 {bundleModule} 初始化失败，检测到{conflictType}。" + $"CRC={pair.Key}，已有模块={existingItem.bundleModuleType}，已有路径={existingItem.path}，" + $"新模块={pair.Value.bundleModuleType}，新路径={pair.Value.path}");
                    }

                    // 配置只有在完整校验后才一次性提交，失败时不会留下半初始化模块。
                    List<uint> addedKeys = new List<uint>();

                    try
                    {
                        foreach (KeyValuePair<uint, BundleItem> pair in parsedConfig.BundleItems)
                        {
                            mAllBundleAssetDic.Add(pair.Key, pair.Value);
                            addedKeys.Add(pair.Key);
                        }
                        //依赖图与资源项属于同一提交；没有依赖也保存空集合，后续激活无需猜测旧协议。
                        mModuleDependencies[bundleModule] = new HashSet<string>(parsedConfig.ModuleDependencies, StringComparer.Ordinal);

                        mAlreadyLoadBundleModuleList.Add(bundleModule);

                        //初始化即激活业务模块并取得全部 Shared 租约；前置递归已保证依赖都初始化完成。
                        if (!TryActivateModuleLeaseLocked(bundleModule, out string leaseError))
                            throw new InvalidOperationException($"模块 {bundleModule} 初始化后无法取得依赖租约：{leaseError}");

                        newlyInitializedModules.Add(bundleModule);
                    }
                    catch
                    {
                        // 极端的提交异常也要撤销本次已经插入的键，保持配置字典的全有或全无语义。
                        foreach (uint crc in addedKeys)
                            mAllBundleAssetDic.Remove(crc);

                        DeactivateModuleLeaseLocked(bundleModule);

                        mModuleDependencies.Remove(bundleModule);

                        mAlreadyLoadBundleModuleList.Remove(bundleModule);

                        throw;
                    }
                }

                Debug.Log($"Init AssetModule Successes BundleModule:{bundleModule} count: {mAllBundleAssetDic.Count}" );
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError("Load AssetBundleConfig Failed, Exception:" + e +"ModuleType:"+ bundleModule);
                return false;
            }
            finally
            {
                // 无论解析或提交是否成功都释放配置 Bundle，避免失败路径泄漏原生内存。
                bundleConfig?.Unload(false);
                //00 当前递归层结束后移出访问集合；兄弟模块仍可合法依赖同一个已初始化 Shared。
                if (!string.IsNullOrWhiteSpace(bundleModule)) visitingModules.Remove(bundleModule);
            }

        }

        /// <summary>
        /// 在模块没有活动资源引用时，用磁盘上的新快照安全替换内存配置。
        /// </summary>
        public async UniTask<bool> ReloadAssetModule(string bundleModule)
        {
            await EnsureWebGLActiveSnapshotLoadedAsync();
            await mModuleMutationLock.WaitAsync();
            bool hasModuleGuard = false;
            try
            {
                // 热重载同样涉及 AssetBundle 原生对象，只允许在 Unity 主线程进入。
                await UniTask.SwitchToMainThread();
                lock (mLock)
                {
                    //00 Shared 存在已初始化消费者时禁止重载，即使当前暂时没有资源对象也不能在线更换其配置。
                    if (HasInitializedDependentsLocked(bundleModule, out string dependentModules))
                    {
                        Debug.LogError(
                            $"模块 {bundleModule} 仍被已初始化模块依赖，无法热重载：{dependentModules}");
                        return false;
                    }
                    if (mModulesBeingCleared.Contains(bundleModule) ||
                        (mModuleInFlightLoadCountDic.TryGetValue(bundleModule, out int count) && count > 0) ||
                        HasModuleAsyncLoad(bundleModule))
                    {
                        Debug.LogError($"模块 {bundleModule} 仍有加载任务或正在清理，无法热重载配置。");
                        return false;
                    }
                    //00 从通过预检到完成回滚/提交期间拒绝新加载，消除检查与替换配置之间的竞态窗口。
                    mModulesBeingCleared.Add(bundleModule);
                    hasModuleGuard = true;
                }
                return await ReloadAssetModuleInternal(bundleModule);
            }
            finally
            {
                if (hasModuleGuard)
                {
                    lock (mLock)
                    {
                        mModulesBeingCleared.Remove(bundleModule);
                    }
                }
                mModuleMutationLock.Release();
            }
        }

        private static async UniTask EnsureWebGLActiveSnapshotLoadedAsync()
        {
            AssetRuntimeBackend backend = AssetRuntimeBackendFactory.Current;
            if (backend.PlatformKind == AssetRuntimePlatformKind.WebGL &&
                backend.CommitStrategy is WebGLVersionPointerCommitStrategy pointerStrategy)
                await pointerStrategy.InitializeAsync();
        }

        private async UniTask<bool> ReloadAssetModuleInternal(string bundleModule)
        {
            Dictionary<uint, BundleItem> previousItems = new Dictionary<uint, BundleItem>();
            HashSet<string> previousDependencies = new HashSet<string>(StringComparer.Ordinal);
            bool previousLeaseWasActive = false;
            bool moduleWasInitialized;
            lock (mLock)
            {
                moduleWasInitialized = mAlreadyLoadBundleModuleList.Contains(bundleModule);
                if (!moduleWasInitialized)
                    previousItems.Clear();

                if (moduleWasInitialized)
                {
                    foreach (KeyValuePair<uint, BundleItem> pair in mAllBundleAssetDic)
                    {
                        BundleItem item = pair.Value;
                        if (!string.Equals(item.bundleModuleType, bundleModule, StringComparison.Ordinal))
                            continue;

                        // 仍有对象、Bundle 或异步加载持有旧配置时禁止热切换，调用方会回滚磁盘快照。
                        if (item.refCount > 0 ||
                            item.obj != null ||
                            item.objArr != null ||
                            item.assetBundle != null ||
                            mAsyncLoadBundleItemActionDic.ContainsKey(pair.Key) ||
                            mRemoteBundleLoadTasks.ContainsKey(pair.Key) ||
                            HasLoadedBundleReference(new ModuleBundleKey(item.bundleModuleType, item.bundleName)))
                        {
                            Debug.LogError(
                                $"模块 {bundleModule} 仍有活动资源，无法安全重载配置。Bundle：{item.bundleName}");
                            return false;
                        }
                        previousItems.Add(pair.Key, item);
                    }

                    foreach (uint crc in previousItems.Keys)
                        mAllBundleAssetDic.Remove(crc);
                    //00 保存旧模块图和激活状态，只有新配置完整成功后才丢弃回滚快照。
                    if (mModuleDependencies.TryGetValue(bundleModule, out HashSet<string> dependencies))
                        previousDependencies = new HashSet<string>(dependencies, StringComparer.Ordinal);
                    previousLeaseWasActive = mActiveModuleLeases.Contains(bundleModule);
                    DeactivateModuleLeaseLocked(bundleModule);
                    mModuleDependencies.Remove(bundleModule);
                    mAlreadyLoadBundleModuleList.Remove(bundleModule);
                }
            }

            if (!moduleWasInitialized)
                return await InitializeAssetModuleInternal(bundleModule);

            bool reloadSucceeded = await InitializeAssetModuleInternal(bundleModule);
            if (reloadSucceeded)
                return true;

            lock (mLock)
            {
                // 新配置初始化失败时恢复旧内存映射，与磁盘事务回滚保持一致。
                List<uint> failedModuleKeys = new List<uint>();
                foreach (KeyValuePair<uint, BundleItem> pair in mAllBundleAssetDic)
                {
                    if (string.Equals(
                            pair.Value.bundleModuleType,
                            bundleModule,
                            StringComparison.Ordinal))
                    {
                        failedModuleKeys.Add(pair.Key);
                    }
                }
                foreach (uint crc in failedModuleKeys)
                    mAllBundleAssetDic.Remove(crc);
                foreach (KeyValuePair<uint, BundleItem> pair in previousItems)
                    mAllBundleAssetDic[pair.Key] = pair.Value;
                //00 先恢复依赖图和模块标记，再按旧状态重新取得租约，回滚结果与重载前完全一致。
                mModuleDependencies[bundleModule] = previousDependencies;
                if (!mAlreadyLoadBundleModuleList.Contains(bundleModule))
                    mAlreadyLoadBundleModuleList.Add(bundleModule);
                if (previousLeaseWasActive &&
                    !TryActivateModuleLeaseLocked(bundleModule, out string leaseError))
                    Debug.LogError($"模块 {bundleModule} 重载回滚后恢复依赖租约失败：{leaseError}");
            }
            return false;
        }

        /// <summary>
        /// 从运行时移除一个已经没有资源缓存的模块配置；旧 ClearModuleAssetsAsync 不会隐式调用它。
        /// </summary>
        internal async UniTask<ModuleUnloadResult> UnloadAssetModuleAsync(string bundleModule)
        {
            ModuleUnloadResult result = new ModuleUnloadResult
            {
                bundleModule = bundleModule,
                status = ModuleUnloadStatus.Failed
            };
            if (string.IsNullOrWhiteSpace(bundleModule))
            {
                result.status = ModuleUnloadStatus.InvalidModule;
                result.message = "资源模块名称不能为空。";
                return result;
            }
            bundleModule = bundleModule.Trim();

            await mModuleMutationLock.WaitAsync();
            bool hasModuleGuard = false;
            try
            {
                //00 AssetBundle 原生状态检查和配置移除都固定在 Unity 主线程。
                await UniTask.SwitchToMainThread();
                lock (mLock)
                {
                    if (!mAlreadyLoadBundleModuleList.Contains(bundleModule))
                    {
                        result.status = ModuleUnloadStatus.NotInitialized;
                        result.message = $"资源模块尚未初始化：{bundleModule}";
                        return result;
                    }
                    //00 只要仍有已初始化消费者，Shared 配置就不能卸载；必须先逐个卸载业务模块。
                    if (HasInitializedDependentsLocked(bundleModule, out string dependentModules))
                    {
                        result.status = ModuleUnloadStatus.DependentModuleInitialized;
                        result.message =
                            $"模块 {bundleModule} 仍被已初始化模块依赖：{dependentModules}";
                        return result;
                    }
                    if (mModulesBeingCleared.Contains(bundleModule) ||
                        (mModuleInFlightLoadCountDic.TryGetValue(bundleModule, out int count) && count > 0) ||
                        HasModuleAsyncLoad(bundleModule))
                    {
                        result.status = ModuleUnloadStatus.Busy;
                        result.message = $"资源模块仍有加载或清理任务：{bundleModule}";
                        return result;
                    }
                    //00 预检通过后设置同一 Clearing 门禁，直到配置和依赖图全部移除。
                    mModulesBeingCleared.Add(bundleModule);
                    hasModuleGuard = true;

                    foreach (KeyValuePair<uint, BundleItem> pair in mAllBundleAssetDic)
                    {
                        BundleItem item = pair.Value;
                        if (!string.Equals(item.bundleModuleType, bundleModule, StringComparison.Ordinal)) continue;
                        ModuleBundleKey mainBundleKey =
                            new ModuleBundleKey(item.bundleModuleType, item.bundleName);
                        if (item.refCount > 0 || item.obj != null || item.objArr != null ||
                            item.assetBundle != null || HasLoadedBundleReference(mainBundleKey))
                        {
                            result.status = ModuleUnloadStatus.InUse;
                            result.message =
                                $"资源模块仍有活动资源，必须先成功清理：{bundleModule}，Bundle：{item.bundleName}";
                            return result;
                        }
                    }
                    //00 防御直接按 Bundle 名加载但没有绑定 BundleItem 的外部调用；任何缓存都阻止配置卸载。
                    foreach (KeyValuePair<ModuleBundleKey, AssetBundleCache> pair in mAllAlreadyLoadBundleDic)
                    {
                        if (!string.Equals(pair.Key.ModuleName, bundleModule, StringComparison.Ordinal)) continue;
                        if (pair.Value?.assetBundle != null || pair.Value?.referenceCount > 0)
                        {
                            result.status = ModuleUnloadStatus.InUse;
                            result.message = $"资源模块仍有活动 Bundle：{pair.Key}";
                            return result;
                        }
                    }

                    //00 资源与 Bundle 均无持有后，一次性移除 CRC Entry、模块图和初始化标记。
                    RemoveModuleConfigurationLocked(bundleModule);
                    result.status = ModuleUnloadStatus.Success;
                    result.message = $"资源模块配置已卸载：{bundleModule}";
                    return result;
                }
            }
            catch (Exception exception)
            {
                result.status = ModuleUnloadStatus.Failed;
                result.message = $"资源模块配置卸载失败：{bundleModule}";
                result.exception = exception;
                Debug.LogError($"{result.message} Exception:{exception}");
                return result;
            }
            finally
            {
                if (hasModuleGuard)
                {
                    lock (mLock)
                    {
                        mModulesBeingCleared.Remove(bundleModule);
                    }
                }
                mModuleMutationLock.Release();
            }
        }

        /// <summary>
        ///  在 mLock 内移除模块配置、依赖租约和所有公开 Entry；调用方必须已完成活动资源预检。
        /// </summary>
        private void RemoveModuleConfigurationLocked(string bundleModule)
        {
            //00 先释放消费者租约，使 Shared 的反向依赖集合及时更新。
            DeactivateModuleLeaseLocked(bundleModule);
            //00 收集后再删除，避免遍历 Dictionary 时修改集合。
            List<uint> moduleCrcs = new List<uint>();
            foreach (KeyValuePair<uint, BundleItem> pair in mAllBundleAssetDic)
            {
                if (string.Equals(pair.Value.bundleModuleType, bundleModule, StringComparison.Ordinal))
                    moduleCrcs.Add(pair.Key);
            }
            foreach (uint crc in moduleCrcs) mAllBundleAssetDic.Remove(crc);
            //00 删除正向图、可能的空反向图以及初始化/飞行状态。
            mModuleDependencies.Remove(bundleModule);
            mActiveModuleDependents.Remove(bundleModule);
            mActiveModuleLeases.Remove(bundleModule);
            mAlreadyLoadBundleModuleList.Remove(bundleModule);
            mModuleInFlightLoadCountDic.Remove(bundleModule);
        }

        /// <summary>
        ///  保存尚未提交的模块配置快照；资源项和模块依赖必须一起原子提交。
        /// </summary>
        private sealed class ParsedModuleConfig
        {
            internal readonly Dictionary<uint, BundleItem> BundleItems =
                new Dictionary<uint, BundleItem>();
            internal readonly HashSet<string> ModuleDependencies =
                new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        ///  反序列化并校验完整配置，返回值尚未写入任何运行时共享字典。
        /// </summary>
        private static ParsedModuleConfig ParseModuleConfig(
            string bundleConfigJson,
            string bundleModule)
        {
            if (string.IsNullOrWhiteSpace(bundleConfigJson))
                throw new InvalidDataException($"模块 {bundleModule} 的配置内容为空。");

            BundleConfig bundleManifest =
                JsonConvert.DeserializeObject<BundleConfig>(bundleConfigJson);
            if (bundleManifest?.bundleInfoList == null)
                throw new InvalidDataException($"模块 {bundleModule} 的配置列表为空。");

            //00 旧 JSON 反序列化会执行字段初始化器，因此不能只凭 formatVersion 默认值判断协议版本。
            bool hasVersionTwoPayload = !string.IsNullOrWhiteSpace(bundleManifest.moduleName) ||
                                        (bundleManifest.moduleDependencies != null &&
                                         bundleManifest.moduleDependencies.Count > 0);
            //00 任一 Bundle 出现完整依赖列表也明确表示这是 v2 配置。
            if (!hasVersionTwoPayload)
            {
                foreach (BundleInfo bundleInfo in bundleManifest.bundleInfoList)
                {
                    if (bundleInfo?.bundleDependencies == null) continue;
                    hasVersionTwoPayload = true;
                    break;
                }
            }
            //00 v2 必须声明自身模块；真正旧协议没有任何 v2 载荷时仍按调用方模块兼容读取。
            if (hasVersionTwoPayload && bundleManifest.formatVersion >= 2 &&
                !string.Equals(bundleManifest.moduleName?.Trim(), bundleModule, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"模块配置名称不一致：请求模块={bundleModule}，配置模块={bundleManifest.moduleName}");
            if (hasVersionTwoPayload && bundleManifest.formatVersion < 2)
                throw new InvalidDataException(
                    $"模块 {bundleModule} 包含 v2 依赖字段，但 formatVersion={bundleManifest.formatVersion}。");
            //00 传给依赖解析器的有效版本只在确认 v2 载荷后才是 2，避免旧 JSON 被默认字段值误判。
            int effectiveFormatVersion = hasVersionTwoPayload ? bundleManifest.formatVersion : 0;

            ParsedModuleConfig parsedConfig = new ParsedModuleConfig();
            //00 模块依赖先完整校验，Bundle 跨模块依赖随后必须落在这份显式声明中。
            if (bundleManifest.moduleDependencies != null)
            {
                foreach (string rawDependencyModule in bundleManifest.moduleDependencies)
                {
                    string dependencyModule = rawDependencyModule?.Trim();
                    if (string.IsNullOrWhiteSpace(dependencyModule))
                        throw new InvalidDataException($"模块 {bundleModule} 的 moduleDependencies 包含空模块名。");
                    if (string.Equals(dependencyModule, bundleModule, StringComparison.Ordinal))
                        throw new InvalidDataException($"模块 {bundleModule} 不能声明自身为模块依赖。");
                    //00 HashSet 去重后形成稳定模块图；重复 JSON 项不增加第二份租约。
                    parsedConfig.ModuleDependencies.Add(dependencyModule);
                }
            }

            foreach (BundleInfo info in bundleManifest.bundleInfoList)
            {
                if (info == null)
                    throw new InvalidDataException($"模块 {bundleModule} 的配置包含空资源项。");
                string normalizedPath = NormalizeAssetPath(info.path);
                if (string.IsNullOrWhiteSpace(normalizedPath) ||
                    !normalizedPath.StartsWith("Assets/", StringComparison.Ordinal))
                    throw new InvalidDataException($"模块 {bundleModule} 存在无效资源路径：{info.path}");
                uint calculatedCrc = Crc32.GetCrc32(normalizedPath);
                if (info.crc != calculatedCrc)
                    throw new InvalidDataException(
                        $"模块 {bundleModule} 的资源 CRC 与路径不一致：path={normalizedPath}，配置 CRC={info.crc}，计算 CRC={calculatedCrc}");
                if (!string.Equals(info.bundleModule, bundleModule, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"模块配置归属不一致：请求模块={bundleModule}，资源声明模块={info.bundleModule}，path={normalizedPath}");

                // DependencyOnly 条目只用于构建 Bundle 依赖关系，不进入全局可直接加载资源索引。
                if (!info.isLoadableEntry) continue;
                if (!AssetBundleNameValidator.TryValidateFileName(info.bundleName, out string bundleNameFailure))
                    throw new InvalidDataException(
                        $"模块 {bundleModule} 存在无效 Bundle 名称：{info.bundleName ?? "<null>"}，" +
                        $"原因：{bundleNameFailure}。");
                if (string.IsNullOrWhiteSpace(info.assetName))
                    throw new InvalidDataException($"模块 {bundleModule} 存在资源名称为空的 Entry：{normalizedPath}");

                //00 协议 v2 优先使用完整模块 Bundle 身份；旧配置自动把字符串依赖归到当前模块。
                List<ModuleBundleKey> bundleDependencies = SanitizeBundleDependencies(
                    bundleModule,
                    info.bundleName,
                    info.bundleDependce,
                    info.bundleDependencies,
                    effectiveFormatVersion,
                    parsedConfig.ModuleDependencies);

                BundleItem item = new BundleItem
                {
                    path = normalizedPath,
                    crc = calculatedCrc,
                    bundleModuleType = bundleModule,
                    assetName = info.assetName,
                    //00 旧字段继续保留同模块依赖名称，兼容诊断和历史外部只读代码。
                    bundleDependce = bundleDependencies
                        .FindAll(key => string.Equals(key.ModuleName, bundleModule, StringComparison.Ordinal))
                        .ConvertAll(key => key.BundleName),
                    //00 所有正式加载和释放路径使用完整组合键。
                    bundleDependencies = bundleDependencies,
                    bundleName = info.bundleName,
                    isRemoteAsset = info.isAddressableAsset
                };
                if (!parsedConfig.BundleItems.TryAdd(item.crc, item))
                    throw new InvalidDataException(
                        $"模块 {bundleModule} 的配置包含重复 CRC：{item.crc}。");
            }
            return parsedConfig;
        }

        /// <summary>
        ///  保留原私有解析测试入口，只返回可加载 Entry；正式初始化使用 ParseModuleConfig 同时取得模块图。
        /// </summary>
        private static Dictionary<uint, BundleItem> ParseBundleItems(
            string bundleConfigJson,
            string bundleModule)
        {
            return ParseModuleConfig(bundleConfigJson, bundleModule).BundleItems;
        }

        /// <summary>
        ///  将新旧协议依赖统一转换为 ModuleBundleKey，并验证跨模块边是否被 moduleDependencies 声明。
        /// </summary>
        private static List<ModuleBundleKey> SanitizeBundleDependencies(
            string ownerModule,
            string bundleName,
            List<string> legacyDependencies,
            List<BundleDependencyInfo> versionTwoDependencies,
            int formatVersion,
            HashSet<string> moduleDependencies)
        {
            //00 返回新列表而不是修改反序列化对象，热重载失败仍可恢复旧对象图。
            List<ModuleBundleKey> result = new List<ModuleBundleKey>();
            HashSet<ModuleBundleKey> uniqueDependencies = new HashSet<ModuleBundleKey>();
            //00 v2 配置存在完整列表时绝不从旧字符串猜测跨模块归属。
            if (formatVersion >= 2 && versionTwoDependencies != null)
            {
                foreach (BundleDependencyInfo dependency in versionTwoDependencies)
                {
                    if (dependency == null)
                        throw new InvalidDataException(
                            $"模块 {ownerModule} 的 Bundle {bundleName} 包含空依赖项。");
                    ModuleBundleKey dependencyKey = new ModuleBundleKey(
                        dependency.bundleModule,
                        dependency.bundleName);
                    if (string.IsNullOrWhiteSpace(dependencyKey.ModuleName) ||
                        string.IsNullOrWhiteSpace(dependencyKey.BundleName))
                        throw new InvalidDataException(
                            $"模块 {ownerModule} 的 Bundle {bundleName} 包含无效依赖身份。" +
                            $"Module={dependency.bundleModule}，Bundle={dependency.bundleName}");
                    if (!AssetBundleNameValidator.TryValidateFileName(
                            dependencyKey.BundleName,
                            out string dependencyNameFailure))
                        throw new InvalidDataException(
                            $"模块 {ownerModule} 的 Bundle {bundleName} 包含无效依赖文件名：" +
                            $"{dependency.bundleName}，原因：{dependencyNameFailure}。");
                    //00 主 Bundle 自依赖会重复加载和归还，必须过滤；跨模块同名仍是不同合法键。
                    ModuleBundleKey ownerKey = new ModuleBundleKey(ownerModule, bundleName);
                    if (dependencyKey == ownerKey || !uniqueDependencies.Add(dependencyKey)) continue;
                    //00 跨模块 Bundle 依赖必须同时出现在模块依赖图，否则初始化顺序和租约不完整。
                    if (!string.Equals(dependencyKey.ModuleName, ownerModule, StringComparison.Ordinal) &&
                        !moduleDependencies.Contains(dependencyKey.ModuleName))
                        throw new InvalidDataException(
                            $"模块 {ownerModule} 的 Bundle {bundleName} 依赖 " +
                            $"{dependencyKey}，但 moduleDependencies 未声明该模块。");
                    result.Add(dependencyKey);
                }
                return result;
            }

            //00 旧协议只有 Bundle 名，按历史语义全部归属于当前模块。
            if (legacyDependencies == null) return result;
            foreach (string dependencyName in legacyDependencies)
            {
                ModuleBundleKey dependencyKey = new ModuleBundleKey(ownerModule, dependencyName);
                if (string.IsNullOrWhiteSpace(dependencyKey.BundleName))
                    continue;
                if (!AssetBundleNameValidator.TryValidateFileName(
                        dependencyKey.BundleName,
                        out string dependencyNameFailure))
                    throw new InvalidDataException(
                        $"模块 {ownerModule} 的 Bundle {bundleName} 包含无效旧版依赖文件名：" +
                        $"{dependencyName}，原因：{dependencyNameFailure}。");
                if (string.Equals(bundleName, dependencyKey.BundleName, StringComparison.Ordinal) ||
                    !uniqueDependencies.Add(dependencyKey)) continue;
                result.Add(dependencyKey);
            }
            return result;
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/').TrimStart('/');
        }

        private bool HasLoadedBundleReference(ModuleBundleKey bundleKey)
        {
            return !string.IsNullOrEmpty(bundleKey.ModuleName) &&
                   !string.IsNullOrEmpty(bundleKey.BundleName) &&
                   mAllAlreadyLoadBundleDic.TryGetValue(bundleKey, out AssetBundleCache bundleCache) &&
                   (bundleCache.referenceCount > 0 || bundleCache.assetBundle != null);
        }
        
        /// <summary>
        /// 生成AssetBundleConfig配置文件路径
        /// </summary>
        /// <param name="bundleModule"></param>
        /// <returns></returns>
        public bool GeneratorBundleConfigPath(string bundleModule,string mBundleConfigName,ref string mBundleConfigPath)
        {
            if (AssetRuntimeBackendFactory.Current.PlatformKind == AssetRuntimePlatformKind.WebGL)
            {
                // WebGL 的内嵌、RemoteAsset 与活动热更快照都通过 Location 解析；浏览器 URL 不能交给 File.Exists。
                mBundleConfigPath = BundleSettings.Instance.GetAssetsBuiltinBundlePath(bundleModule) + mBundleConfigName;
                return BundleSettings.Instance.loadAssetType == LoadAssetEnum.AssetBundle;
            }

            //如果配置文件 存在，return true，如果不存，我们就直接从内嵌的资源中去加载。
            if (!File.Exists(mBundleConfigPath))
            {
                mBundleConfigPath = BundleSettings.Instance.GetAssetsBuiltinBundlePath(bundleModule) + mBundleConfigName;
                //如果是Editor加载模式，不需要加载资源清单，直接跳过
                return BundleSettings.Instance.loadAssetType == LoadAssetEnum.AssetBundle;
            }
            
            return true;
        }
        #endregion

        #region AssetBundle配置查询

        
        /// <summary>
        /// 根据AssetBundle名称查询该AssetBUndle中都有那些资源
        /// </summary>
        /// <param name="bundleName"></param>
        /// <returns></returns>
        public List<BundleItem> GetBundleItemByABName(string bundleName)
        {
            List<BundleItem> itemList = new List<BundleItem>();
            foreach (var item in mAllBundleAssetDic.Values)
            {
                if (string.Equals(item.bundleName,bundleName))
                {
                    itemList.Add(item);
                }
            }
            return itemList;
        }

        public BundleItem GetBundleItemByCrc(uint crc)
        {
            //00 配置初始化、热重载和模块清理都会改写该字典，查询必须复用同一把状态锁。
            lock (mLock)
            {
                mAllBundleAssetDic.TryGetValue(crc, out BundleItem item);
                return item;
            }
        }
        #endregion
        
        #region 异步加载AssetBundle

        /// <summary>
        /// 通过资源路径的Crc加载该资源所在AssetBundle
        /// </summary>
        /// <param name="crc"></param>
        /// <returns></returns>
        public async UniTask<BundleItem> LoadAssetBundleAsync(uint crc,bool isEncrypt = false)
        {
            //先到所有的AssetBunel资源字典中查询一下这个资源存不存在，如果存在说明该资源已经打成了AssetBundle包，这种情况下就可以直接加载了
            //如果不存在，则说明该资源 不属于AssetBUnle 给与错误提示。
            mAllBundleAssetDic.TryGetValue(crc, out var item);

            if (item != null)
            {
                //00 锁内原子检查并登记本资源的初始化任务，消除并发请求下 TryGetValue 与 Add 分离导致的重复登记竞态。
                UniTaskCompletionSource<BundleItem> ownerSource;
                bool isOwner;
                lock (mLock)
                {
                    if (mAsyncLoadBundleItemActionDic.TryGetValue(
                            crc,
                            out UniTaskCompletionSource<BundleItem> loadingSource))
                    {
                        ownerSource = loadingSource;
                        isOwner = false;
                    }
                    else
                    {
                        ownerSource = new UniTaskCompletionSource<BundleItem>();
                        mAsyncLoadBundleItemActionDic.Add(crc, ownerSource);
                        isOwner = true;
                    }
                }
                if (!isOwner)
                {
                    // 同一资源正在初始化时直接等待所有者完成，不再次增加底层 Bundle 引用。
                    return await ownerSource.Task;
                }

                //如果AssetBundle为空，说明该资源所在的AssetBundle没有加载进内存，这种情况我们就需要加载该AssetBundle
                if (item.assetBundle != null)
                {
                    //00 登记后发现资源已就绪（例如同步链路已加载），必须先完成本任务并移除登记，避免等待者挂起或残留登记阻塞模块清理。
                    ownerSource.TrySetResult(item);
                    lock (mLock)
                    {
                        mAsyncLoadBundleItemActionDic.Remove(crc);
                    }
                    return item;
                }
                //00 回滚列表保存完整模块 Bundle 身份，跨模块依赖失败时不会释放到主资源模块目录。
                List<ModuleBundleKey> acquiredBundleKeys = new List<ModuleBundleKey>();
                try
                {
                    item.assetBundle = await LoadAssetBundleAsync(
                        item.bundleName,
                        item.bundleModuleType,
                        isEncrypt,
                        item.isRemoteAsset);

                    if (item.assetBundle == null)
                    {
                        // 具体加载器已经记录平台、模块、Bundle 与真实失败原因；此层只传播失败，避免重复且错误地归因为远端资源。
                        ownerSource.TrySetResult(null);
                        return null;
                    }
                    acquiredBundleKeys.Add(new ModuleBundleKey(item.bundleModuleType, item.bundleName));

                    //需要加载这个AssetBundle依赖的其他的AssetBundle
                    foreach (ModuleBundleKey dependencyKey in
                             item.bundleDependencies ?? new List<ModuleBundleKey>())
                    {
                        AssetBundle dependencyBundle = await LoadAssetBundleAsync(
                            dependencyKey.BundleName,
                            dependencyKey.ModuleName,
                            isEncrypt,
                            item.isRemoteAsset &&
                            string.Equals(
                                dependencyKey.ModuleName,
                                item.bundleModuleType,
                                StringComparison.Ordinal));
                        if (dependencyBundle == null)
                            throw new InvalidOperationException($"依赖 Bundle 加载失败：{dependencyKey}");
                        acquiredBundleKeys.Add(dependencyKey);
                    }

                    ownerSource.TrySetResult(item);
                    return item;
                }
                catch (Exception exception)
                {
                    Debug.LogError($"资源 Bundle 初始化失败，CRC:{crc} Exception:{exception}");
                    // 初始化未完整完成时回滚本次已经取得的 Bundle 引用，避免留下半初始化资源图。
                    for (int index = acquiredBundleKeys.Count - 1; index >= 0; index--)
                        ReleaseAssetBundle(acquiredBundleKeys[index], false);
                    item.assetBundle = null;
                    ownerSource.TrySetResult(null);
                    return null;
                }
                finally
                {
                    lock (mLock)
                    {
                        mAsyncLoadBundleItemActionDic.Remove(crc);
                    }
                }
            }
            else
            {
                Debug.LogError("assets not exists AssetbundleConfig , LoadAssetBundle failed! Crc:"+crc);
                return null;
            }
        }
        /// <summary>
        /// 通过AssetBundle Name加载AssetBundle
        /// </summary>
        /// <param name="bundleName"></param>
        /// <param name="bundleModuleType"></param>
        /// <returns></returns>
        public async UniTask<AssetBundle> LoadAssetBundleAsync(
            string bundleName,
            string bundleModuleType,
            bool isEncrypt = false,
            bool preferHotPath = false)
        {
            // 所有缓存和异步去重从入口开始使用完整组合键，不再依赖 Bundle 文件名前缀约定。
            ModuleBundleKey bundleKey = new ModuleBundleKey(bundleModuleType, bundleName);
            string bundleNameFailure = null;
            if (string.IsNullOrWhiteSpace(bundleKey.ModuleName) ||
                !AssetBundleNameValidator.TryValidateFileName(bundleName, out bundleNameFailure))
            {
                Debug.LogError(
                    $"异步加载 AssetBundle 的模块或文件名无效。模块：{bundleKey.ModuleName}，" +
                    $"原因：{bundleNameFailure ?? "模块名称为空"}");
                return null;
            }
            bundleModuleType = bundleKey.ModuleName;
            bundleName = bundleKey.BundleName;
            AssetBundleCache bundle = null;
            mAllAlreadyLoadBundleDic.TryGetValue(bundleKey,out bundle);
            // 构建端启用全局加密后，所有异步加载入口必须采用同一解密策略。
            bool shouldDecrypt = isEncrypt || BundleSettings.Instance.bundleEncrypt.isEncrypt;

            if (bundle==null||(bundle!=null&&bundle.assetBundle==null))
            {
                // 锁内原子检查并登记本 Bundle 的异步任务，命中已有任务时转为等待者，避免并发请求重复加载同一 Bundle。
                UniTaskCompletionSource taskCompletionSource;
                bool isOwner;
                lock (mLock)
                {
                    if (mAsyncLoadBundleActionDic.TryGetValue(bundleKey, out taskCompletionSource))
                    {
                        isOwner = false;
                    }
                    else
                    {
                        taskCompletionSource = new UniTaskCompletionSource();
                        mAsyncLoadBundleActionDic.Add(bundleKey, taskCompletionSource);
                        isOwner = true;
                    }
                }
                if (!isOwner)
                {
                    await taskCompletionSource.Task;
                    mAllAlreadyLoadBundleDic.TryGetValue(bundleKey,out var bundleCache);
                    // 等待者同样持有一次 Bundle；缺少这次计数会在并发释放时提前卸载共享 Bundle。
                    if (bundleCache?.assetBundle != null)
                    {
                        bundleCache.referenceCount++;
                    }

                    return bundleCache?.assetBundle;
                }
                //从类对象池中取出一个AssetBundleCache
                bundle= mBundleCachePool.Spawn();
                AssetRuntimeBackend runtimeBackend = AssetRuntimeBackendFactory.Current;
                //计算出AssetBundle加载路径
                string hotFilePath = BundleSettings.Instance.GetHotAssetsPath(bundleModuleType)+bundleName;
                // RemoteAsset 已在进入本方法前完成下载和校验，因此应优先读取热更目录。
                // 普通资源仍只在全局热更模式开启时使用热更目录，避免误读遗留缓存。
                bool isHotPath = runtimeBackend.PlatformKind == AssetRuntimePlatformKind.Native &&
                                 ShouldUseHotPath(
                                     hotFilePath,
                                     preferHotPath,
                                     BundleSettings.Instance.bundleHotType == BundleHotEnum.Hot);
                // 根据已经验证过的来源选择最终 AssetBundle 文件路径。
                string bundlePath = isHotPath ? hotFilePath :  BundleSettings.Instance.GetAssetsBuiltinBundlePath(bundleModuleType) + bundleName;
                AssetBundleLocation webRemoteLocation = default;
                bool hasWebRemoteLocation =
                    runtimeBackend.PlatformKind == AssetRuntimePlatformKind.WebGL &&
                    preferHotPath &&
                    (WebGLActiveAssetRegistry.TryResolve(
                         bundleModuleType,
                         bundleName,
                         out webRemoteLocation) ||
                     RemoteAssetSystem.Instance.TryResolveRemoteLocation(
                         bundleModuleType,
                         bundleName,
                         out webRemoteLocation));
                AssetBundleLocation bundleLocation = hasWebRemoteLocation
                    ? webRemoteLocation
                    : new AssetBundleLocation(
                        bundleModuleType,
                        bundleName,
                        isHotPath ? AssetBundleSourceKind.HotUpdate : AssetBundleSourceKind.Builtin,
                        bundlePath,
                        null,
                        0,
                        shouldDecrypt);
                AssetBundleLoadRequest loadRequest = new AssetBundleLoadRequest(
                    bundleLocation,
                    AssetBundleLoadPurpose.Content,
                    BundleSettings.Instance.bundleEncrypt.encryptKey);
                try
                {
                    bundle.assetBundle = await runtimeBackend.BundleLoader.LoadAsync(loadRequest);
                }
                catch (Exception e)
                {
                    // 加载失败必须显式归还池对象并终止流程，不能依赖后续 null 分支隐式兜底。
                    mBundleCachePool.Recycl(bundle);
                    lock (mLock)
                    {
                        if (mAsyncLoadBundleActionDic.TryGetValue(bundleKey, out UniTaskCompletionSource failedSource))
                        {
                            failedSource.TrySetCanceled();
                            mAsyncLoadBundleActionDic.Remove(bundleKey);
                        }
                    }
                    Debug.LogError(
                        $"AssetBundle 异步加载异常，平台：{AssetRuntimeBackendFactory.Current.PlatformKind}，" +
                        $"模块：{bundleModuleType}，Bundle：{bundleName}，来源：{bundleLocation.SourceKind}，异常：{e}");
                    return null;
                }
                if (bundle.assetBundle==null)
                {
                    Debug.LogError("AssetBundle load failed bundlePath:"+ bundlePath);
                    // 归还 pool 对象，避免内存泄漏
                    mBundleCachePool.Recycl(bundle);
                    // 通知所有等待方加载失败
                    lock (mLock)
                    {
                        if (mAsyncLoadBundleActionDic.TryGetValue(bundleKey, out var failedSource))
                        {
                            failedSource.TrySetCanceled();
                            mAsyncLoadBundleActionDic.Remove(bundleKey);
                        }
                    }
                    return null;
                }
                bundle = RegisterLoadedBundle(bundleKey, bundle);
                //设置任务为完成状态
                lock (mLock)
                {
                    mAsyncLoadBundleActionDic[bundleKey].TrySetResult();
                    mAsyncLoadBundleActionDic.Remove(bundleKey);
                }
            }
            else
            {
                //AssetBunle已经加载过了
                bundle.referenceCount++;
            }
            return bundle.assetBundle;
        }
        #endregion

        #region  同步加载AssetBundle

        /// <summary>
        /// 通过资源路径的Crc加载该资源所在AssetBundle
        /// </summary>
        /// <param name="crc"></param>
        /// <returns></returns>
        public  BundleItem LoadAssetBundle(uint crc)
        {
            //先到所有的AssetBunel资源字典中查询一下这个资源存不存在，如果存在说明该资源已经打成了AssetBundle包，这种情况下就可以直接加载了
            //如果不存在，则说明该资源 不属于AssetBUnle 给与错误提示。
            BundleItem item;
            lock (mLock)
            {
                if (mAsyncLoadBundleItemActionDic.ContainsKey(crc))
                {
                    Debug.LogError($"资源 CRC {crc} 正在异步加载，已拒绝同步重复加载。");
                    return null;
                }
                mAllBundleAssetDic.TryGetValue(crc, out item);
            }

            if (item != null)
            {
                //如果AssetBundle为空，说明该资源所在的AssetBundle没有加载进内存，这种情况我们就需要加载该AssetBundle
                if (item.assetBundle != null)
                {
                    return item;
                }

                //00 同步失败回滚保存完整已取得键，任一依赖失败都按相反顺序归还。
                List<ModuleBundleKey> acquiredBundleKeys = new List<ModuleBundleKey>();
                item.assetBundle = LoadAssetBundle(item.bundleName,item.bundleModuleType);

                if (item.assetBundle == null)
                {
                    // 具体失败原因已经由按模块加载的底层入口记录；这里不再追加“远端资源”等错误归因。
                    return null;
                }
                acquiredBundleKeys.Add(new ModuleBundleKey(item.bundleModuleType, item.bundleName));
                //需要加载这个AssetBundle依赖的其他的AssetBundle
                foreach (ModuleBundleKey dependencyKey in
                         item.bundleDependencies ?? new List<ModuleBundleKey>())
                {
                    //00 依赖键已经过滤自依赖；按真实所属模块加载 Shared 或当前模块 Bundle。
                    if (LoadAssetBundle(dependencyKey.BundleName, dependencyKey.ModuleName) == null)
                    {
                        //00 同步路径回滚此前所有依赖和主 Bundle，不能遗留半初始化引用图。
                        for (int index = acquiredBundleKeys.Count - 1; index >= 0; index--)
                            ReleaseAssetBundle(acquiredBundleKeys[index], false);
                        item.assetBundle = null;
                        Debug.LogError($"依赖 Bundle 加载失败：{dependencyKey}");
                        return null;
                    }
                    acquiredBundleKeys.Add(dependencyKey);
                }
                return item;
            }
            else
            {
                Debug.LogError("assets not exists AssetbundleConfig , LoadAssetBundle failed! Crc:"+crc);
                return null;
            }
        }
        /// <summary>
        /// 通过AssetBundle Name加载AssetBundle
        /// </summary>
        /// <param name="bundleName"></param>
        /// <param name="bundleModuleType"></param>
        /// <returns></returns>
        public AssetBundle LoadAssetBundle(string bundleName, string bundleModuleType)
        {
            // 同步加载与异步加载使用相同的模块 Bundle 身份。
            ModuleBundleKey bundleKey = new ModuleBundleKey(bundleModuleType, bundleName);
            string bundleNameFailure = null;
            if (string.IsNullOrWhiteSpace(bundleKey.ModuleName) ||
                !AssetBundleNameValidator.TryValidateFileName(bundleName, out bundleNameFailure))
            {
                Debug.LogError(
                    $"同步加载 AssetBundle 的模块或文件名无效。模块：{bundleKey.ModuleName}，" +
                    $"原因：{bundleNameFailure ?? "模块名称为空"}");
                return null;
            }
            bundleModuleType = bundleKey.ModuleName;
            bundleName = bundleKey.BundleName;
            AssetBundleCache bundle;
            lock (mLock)
            {
                if (mAsyncLoadBundleActionDic.ContainsKey(bundleKey))
                {
                    Debug.LogError($"AssetBundle {bundleKey} 正在异步加载，已拒绝同步重复加载。");
                    return null;
                }
                mAllAlreadyLoadBundleDic.TryGetValue(bundleKey, out bundle);
            }

            if (bundle==null||(bundle!=null&&bundle.assetBundle==null))
            {
                //从类对象池中取出一个AssetBundleCache
                bundle= mBundleCachePool.Spawn();
                AssetRuntimeBackend runtimeBackend = AssetRuntimeBackendFactory.Current;
                //计算出AssetBundle加载路径
                string hotFilePath = BundleSettings.Instance.GetHotAssetsPath(bundleModuleType)+bundleName;
                // 同步入口不承担远端下载；仅在全局热更模式开启且文件存在时读取热更目录。
                bool isHotPath = runtimeBackend.PlatformKind == AssetRuntimePlatformKind.Native &&
                                 ShouldUseHotPath(
                                     hotFilePath,
                                     false,
                                     BundleSettings.Instance.bundleHotType == BundleHotEnum.Hot);
                //通过是否是热更路径 计算出AssetBundle加载的路径
                string bundlePath = isHotPath ? hotFilePath :  BundleSettings.Instance.GetAssetsBuiltinBundlePath(bundleModuleType) + bundleName;
                Debug.Log("LoadAssetBundle Path:"+bundlePath);
                AssetBundleLocation bundleLocation = new AssetBundleLocation(
                    bundleModuleType,
                    bundleName,
                    isHotPath ? AssetBundleSourceKind.HotUpdate : AssetBundleSourceKind.Builtin,
                    bundlePath,
                    null,
                    0,
                    BundleSettings.Instance.bundleEncrypt.isEncrypt);
                AssetBundleLoadRequest loadRequest = new AssetBundleLoadRequest(
                    bundleLocation,
                    AssetBundleLoadPurpose.Content,
                    BundleSettings.Instance.bundleEncrypt.encryptKey);
                try
                {
                    bundle.assetBundle = runtimeBackend.BundleLoader.Load(loadRequest);
                }
                catch (Exception exception)
                {
                    // WebGL 未驻留 Bundle 会在这里快速失败；不能忙等浏览器网络请求，也不能泄漏已经取出的池对象。
                    mBundleCachePool.Recycl(bundle);
                    Debug.LogError(
                        $"AssetBundle 同步加载失败，平台：{runtimeBackend.PlatformKind}，模块：{bundleModuleType}，" +
                        $"Bundle：{bundleName}。完整异常：{exception}");
                    return null;
                }
                if (bundle.assetBundle==null)
                {
                    Debug.LogError("AssetBundle load failed bundlePath:"+ bundlePath);
                    // 归还 pool 对象，避免内存泄漏
                    mBundleCachePool.Recycl(bundle);
                    return null;
                }
                bundle = RegisterLoadedBundle(bundleKey, bundle);
            }
            else
            {
                //AssetBunle已经加载过了
                bundle.referenceCount++;
            }
            return bundle.assetBundle;
        }

        /// <summary>
        /// 登记刚加载的 Bundle；若发生极端的重入竞态，保留已缓存实例并回收重复实例。
        /// </summary>
        private AssetBundleCache RegisterLoadedBundle(
            ModuleBundleKey bundleKey,
            AssetBundleCache loadedBundle)
        {
            AssetBundleCache existingBundle = null;
            lock (mLock)
            {
                if (mAllAlreadyLoadBundleDic.TryGetValue(bundleKey, out existingBundle) &&
                    existingBundle?.assetBundle != null)
                {
                    existingBundle.referenceCount++;
                }
                else
                {
                    loadedBundle.referenceCount++;
                    mAllAlreadyLoadBundleDic[bundleKey] = loadedBundle;
                    return loadedBundle;
                }
            }

            AssetBundle duplicateBundle = loadedBundle.assetBundle;
            loadedBundle.Release();
            mBundleCachePool.Recycl(loadedBundle);
            duplicateBundle?.Unload(false);
            Debug.LogWarning($"检测到 AssetBundle 重复加载，已保留先到缓存并回收重复实例：{bundleKey}");
            return existingBundle;
        }

        /// <summary>
        /// 判断 Bundle 是否允许从热更目录读取。
        /// 即使调用方要求优先使用热更目录，文件不存在时也必须回退内嵌目录，
        /// 避免把一个不存在的缓存路径交给 Unity AssetBundle API。
        /// </summary>
        internal static bool ShouldUseHotPath(
            string hotFilePath,
            bool preferHotPath,
            bool isGlobalHotMode)
        {
            return File.Exists(hotFilePath) && (preferHotPath || isGlobalHotMode);
        }
        #endregion

        #region 即用即下AssetBundle加载

        /// <summary>
        /// 按需准备指定资源的远端 Bundle，并复用普通 Bundle 加载链完成主包和依赖装载。
        /// </summary>
        /// <param name="crc">由规范化资源路径计算出的 CRC。</param>
        /// <param name="moduleName">调用方声明的资源归属模块；与配置不一致时拒绝加载。</param>
        /// <returns>成功时返回资源配置项；初始化、下载、校验或加载失败时返回 null。</returns>
        public async UniTask<BundleItem> LoadRemoteAssetBundleAsync(uint crc, string moduleName, Action<float> onProgress = null)
        {
            BundleItem item = null;

            //先到所有的AssetBunel资源字典中查询一下这个资源存不存在，如果存在说明该资源已经打成了AssetBundle包，这种情况下就可以直接加载了
            //如果不存在，则说明该资源 不属于AssetBUnle 给与错误提示。
            mAllBundleAssetDic.TryGetValue(crc, out item);

            if (item != null)
            {
                // 即使全局 CRC 索引已经命中，也必须核对显式模块，防止调用者误用其他模块资源。
                if (!string.Equals(item.bundleModuleType, moduleName, StringComparison.Ordinal))
                {
                    Debug.LogError(
                        $"远端资源模块不匹配，调用模块：{moduleName}，真实模块：{item.bundleModuleType}，CRC：{crc}");
                    return null;
                }

                if (!item.isRemoteAsset)
                    return await LoadAssetBundleAsync(crc);
                // 锁内原子登记远端 Bundle 下载任务，命中已有任务时转为等待者，避免并发请求重复下载。
                UniTaskCompletionSource<BundleItem> ownerSource;
                bool isOwner;
                lock (mLock)
                {
                    if (mRemoteBundleLoadTasks.TryGetValue(
                            crc,
                            out UniTaskCompletionSource<BundleItem> loadingSource))
                    {
                        ownerSource = loadingSource;
                        isOwner = false;
                    }
                    else
                    {
                        ownerSource = new UniTaskCompletionSource<BundleItem>();
                        mRemoteBundleLoadTasks.Add(crc, ownerSource);
                        isOwner = true;
                    }
                }
                // 并发加载同一资源时进度回调以首个调用者（owner）为准；等待者不重复汇报。
                if (!isOwner)
                {
                    return await ownerSource.Task;
                }
                if (item.assetBundle != null)
                {
                    //00 登记后发现资源已就绪，必须先完成本任务并移除登记，避免等待者挂起或残留登记阻塞模块清理。
                    ownerSource.TrySetResult(item);
                    lock (mLock)
                    {
                        mRemoteBundleLoadTasks.Remove(crc);
                    }
                    return item;
                }
                try
                {
                    bool downloadSucceeded = await RemoteAssetSystem.Instance.PrepareAssetAsync(
                        moduleName,
                        crc,
                        onProgress);
                    if (!downloadSucceeded)
                    {
                        Debug.LogError("远端资源 Bundle 下载失败：" + item.bundleName);
                        ownerSource.TrySetResult(null);
                        return null;
                    }

                    // 文件下载完成后复用普通 CRC 入口，统一处理主包、依赖和引用计数。
                    BundleItem loadedItem = await LoadAssetBundleAsync(crc);
                    ownerSource.TrySetResult(loadedItem);
                    return loadedItem;
                }
                catch (Exception exception)
                {
                    Debug.LogError($"远端资源初始化失败，CRC：{crc}，异常：{exception}");
                    ownerSource.TrySetResult(null);
                    return null;
                }
                finally
                {
                    lock (mLock)
                    {
                        mRemoteBundleLoadTasks.Remove(crc);
                    }
                }
            }
            else
            {
               
                if (moduleName != BundleModuleName.None &&
                    await RemoteAssetSystem.Instance.PrepareAssetAsync(moduleName, crc, onProgress))
                {
                    // PrepareAssetAsync 会初始化模块配置；必须重新走异步 CRC 入口，
                    // 才能读取刚建立的索引并将 RemoteAsset 上下文传递给文件路径选择器。
                    return await LoadAssetBundleAsync(crc);
                }
                Debug.LogError("assets not exists AssetbundleConfig , LoadAssetBundle failed! Crc:" + crc);
                return null;
            }
        }
        #endregion
        
        #region 释放AssetBundles
        /// <summary>
        /// 释放AssetBundle 并且释放AssetBundle占用的内存资源
        /// </summary>
        /// <param name="bundleItem"></param>
        /// <param name="unLoad"></param>
        public void ReleaseAssets(BundleItem bundleItem,bool unLoad)
        {
            //AssetBUndle释放策略一般有两种
            //1.第一种：
            // 以AssetBundle.UnLoad(false) 为主
            // 对于非对象资源，比如 text texture audio等 ,资源加载完成后，就可以直接通过AssetBundle.UnLoad(false)释放AssetBundle的镜像文件
            // 对于对象资源 比如Gameobject 我们需要在上层做一个引用计数的对象池，obj在加载出来之后就可以使用AssetBundle.UnLoad(false)释放AssetBundle的镜像文件
            // 因为后续我们访问的对象都是对象池中的物体了

            //2.第二种
            //以AssetBundle.UnLoad(true) 为主
            // 在加载AssetBundle 时做一个缓存，后续加载的所有的资源对象全部通过缓存的AssetBUndle进行加载
            // 在跳转场景的时候 通过 AssetBundle.UnLoad(true) 彻底释放所有的资源与内存占用

            //AssetBundle assetBundle = null;
            if (bundleItem!=null)
            {
                bundleItem.obj = null;
                bundleItem.objArr = null;

                // BundleItem 仅能归还一次它持有的主 Bundle 和依赖引用。
                if (bundleItem.assetBundle == null)
                    return;
                bundleItem.assetBundle = null;

                ReleaseAssetBundle(
                    new ModuleBundleKey(bundleItem.bundleModuleType, bundleItem.bundleName),
                    unLoad);

                if (bundleItem.bundleDependencies != null)
                {
                    foreach (ModuleBundleKey dependencyKey in bundleItem.bundleDependencies)
                    {
                        //完整键已在解析阶段去空、去重并排除主 Bundle，自此按真实模块对称归还。
                        ReleaseAssetBundle(dependencyKey, unLoad);
                    }
                }
            }
            else
            {
                Debug.LogError(" bundleItem is null, release Assets failed!");
            }
        }
        /// <summary>
        /// 释放AssetBundle所占用的资源
        /// </summary>
        /// <param name="assetitem"></param>
        /// <param name="unLoad"></param>
        /// <param name="bundleName"></param>
        public void ReleaseAssetBundle(
            BundleItem assetitem,
            bool unLoad,
            string bundleName = "",
            string bundleModuleType = "")
        {
            //00 保留旧公开方法签名兼容外部代码；新调用必须提供 BundleItem 或完整模块名。
            ModuleBundleKey bundleKey = assetitem == null
                ? new ModuleBundleKey(bundleModuleType, bundleName)
                : new ModuleBundleKey(assetitem.bundleModuleType, assetitem.bundleName);
            if (string.IsNullOrWhiteSpace(bundleKey.ModuleName))
            {
                Debug.LogError($"释放 AssetBundle 缺少模块身份，已拒绝按名称猜测：{bundleName}");
                return;
            }
            if (assetitem != null)
            {
                if (assetitem.assetBundle == null)
                    return;
                assetitem.assetBundle = null;
            }
            ReleaseAssetBundle(bundleKey, unLoad);
        }

        /// <summary>
        ///  按完整模块 Bundle 身份对称归还一次引用。
        /// </summary>
        private void ReleaseAssetBundle(ModuleBundleKey bundleKey, bool unLoad)
        {
            //00 未加载键按幂等释放处理，异常回滚可以安全重复调用。
            if (string.IsNullOrWhiteSpace(bundleKey.ModuleName) ||
                string.IsNullOrWhiteSpace(bundleKey.BundleName) ||
                !mAllAlreadyLoadBundleDic.TryGetValue(bundleKey, out AssetBundleCache bundleCacheItem)) return;
            if (bundleCacheItem.assetBundle == null) return;
            if (bundleCacheItem.referenceCount <= 0)
            {
                Debug.LogWarning($"AssetBundle 引用计数已为 0，忽略重复释放：{bundleKey}");
                return;
            }

            bundleCacheItem.referenceCount--;
            //00 引用计数不得长期为负；小于等于零时立即卸载并从组合键缓存移除。
            if (bundleCacheItem.referenceCount <= 0)
            {
                bundleCacheItem.assetBundle.Unload(unLoad);
                mAllAlreadyLoadBundleDic.Remove(bundleKey);
                bundleCacheItem.Release();
                mBundleCachePool.Recycl(bundleCacheItem);
            }
        }
        #endregion
        
    }

}
