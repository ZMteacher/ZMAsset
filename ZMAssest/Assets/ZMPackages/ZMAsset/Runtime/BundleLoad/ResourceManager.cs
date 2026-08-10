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
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 缓存对象
    /// </summary>
    public class CacheObejct
    {
        public uint crc;
        public string path;
        public int insid;
        public GameObject obj;
        public OriginData originData;
        //00 模块归属只从成功解析的 BundleItem 反向写入，业务调用方无需也不得传入模块名。
        public string bundleModuleType;
        //00 实例是否已进入对象池必须独立记录，避免重复 Release 把同一对象加入池多次。
        public bool isInPool;
        public void Release()
        {
            crc = 0;
            insid = 0;
            path = "";
            bundleModuleType = string.Empty;
            isInPool = false;
            if (obj != null)
            {
                GameObject.Destroy(obj);
            }
            originData=null;
            obj = null;
        }
    }
    /// <summary>
    /// 加载对象回调
    /// </summary>
    public class LoadObjectCallBack
    {
        public string path;
        public uint crc;
        public object param1;
        public object param2;
        public System.Action<GameObject, object, object> loadResult;
    }
    /// <summary>
    /// 资源请求
    /// </summary>
    public class AssetsRequest
    {
        public GameObject obj;
        public object param1;
        public object param2;
        public object param3;

        private ResourceManager mOwner;
        private bool mIsReleased = true;

        internal void Initialize(
            ResourceManager owner,
            object parameter1,
            object parameter2,
            object parameter3)
        {
            mOwner = owner ?? throw new ArgumentNullException(nameof(owner));
            mIsReleased = false;
            obj = null;
            param1 = parameter1;
            param2 = parameter2;
            param3 = parameter3;
        }

        internal bool TryBeginRelease(out GameObject instance)
        {
            instance = null;
            if (mIsReleased)
                return false;

            mIsReleased = true;
            instance = obj;
            obj = null;
            param1 = null;
            param2 = null;
            param3 = null;
            mOwner = null;
            return true;
        }

        /// <summary>
        /// 归还该请求持有的实例和请求对象。重复调用是安全的。
        /// </summary>
        public void Release()
        {
            mOwner?.Release(this);
        }
    }

    public class ResourceManager : IResourceInterface, IRemoteAssetLoader, IAssetHandleOwner
    {
        private enum AssetHandleReleasePolicy
        {
            RetainUntilAssetEviction,
            DestroyOnHandleRelease
        }

        private sealed class AssetHandleRecord
        {
            public long handleId;
            public uint crc;
            public string path;
            public string bundleModuleType;
            public UnityEngine.Object asset;
            public AssetHandleState state;
            public AssetHandleReleasePolicy releasePolicy;
        }

        private sealed class ModuleResourceState
        {
            public readonly HashSet<uint> loadedAssetCrcs = new HashSet<uint>();
            public readonly HashSet<int> objectInstanceIds = new HashSet<int>();
            public readonly HashSet<string> atlasPaths = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<long> asyncTaskIds = new HashSet<long>();
        }

        /// <summary>
        /// 已经加载过的资源字典 key为资源路径Crc vluae 为资源对象
        /// </summary>
        private Dictionary<uint, BundleItem> mAlreayLoadAssetsDic = new Dictionary<uint, BundleItem>();
        /// <summary>
        /// 对象池字典
        /// </summary>
        private Dictionary<uint, List<CacheObejct>> mObjectPoolDic = new Dictionary<uint, List<CacheObejct>>();
        /// <summary>
        /// 所有对象字典
        /// </summary>
        private Dictionary<int, CacheObejct> mAllObjectDic = new Dictionary<int, CacheObejct>();
        // 每次业务加载都拥有独立 HandleId；CRC 反向集合用于模块门禁和最后一个句柄释放判定。
        private readonly Dictionary<long, AssetHandleRecord> mAssetHandleRecords =
            new Dictionary<long, AssetHandleRecord>();
        private readonly Dictionary<uint, HashSet<long>> mAssetHandleIdsByCrc =
            new Dictionary<uint, HashSet<long>>();
        // 异步句柄加载完成到句柄交付之间仍属于活动消费者；延迟回滚必须等全部消费者退出。
        private readonly Dictionary<uint, int> mAssetLoadConsumerCountByCrc =
            new Dictionary<uint, int>();
        private readonly HashSet<uint> mDeferredAssetRollbackCrcs = new HashSet<uint>();
        private readonly int mAssetHandleMainThreadId = Thread.CurrentThread.ManagedThreadId;
        private long mAssetHandleGuid;
        //00 反向索引只保存主缓存键，不复制 BundleItem 或 UnityEngine.Object。
        private readonly Dictionary<string, ModuleResourceState> mModuleResourceStateDic =
            new Dictionary<string, ModuleResourceState>(StringComparer.Ordinal);
        /// <summary>
        /// 缓存对象类对象池
        /// </summary>
        private ClassObjectPool<CacheObejct> mCacheObejctPool = new ClassObjectPool<CacheObejct>(150);
        /// <summary>
        /// 异步加载任务列表
        /// </summary>
        private List<long> mAsyncLoadingTaskList = new List<long>();
        /// <summary>
        /// 异步加载任务唯一id
        /// </summary>
        private long asyncGuid;
        /// <summary>
        ///  异步加载任务唯一id
        /// </summary>
        private long mAsyncTaskGuid
        {
            get
            {
                //00 原判断 asyncGuid > long.MaxValue 永远不成立（long 不可能大于其最大值），改为检测真实溢出（自增环绕为负数）并在接近上限前预防性重置。
                if (asyncGuid < 0 || asyncGuid >= long.MaxValue - 1000000) asyncGuid = 0;
                long result = asyncGuid++;
                //00 重置后跳过仍被引用中的任务 ID，保证唯一性；正常路径下该循环只做一次条件判断。
                while (result < 0 || mLoadObjectCallBackDic.ContainsKey(result) || mAsyncLoadingTaskList.Contains(result))
                    result = asyncGuid++;
                return result;
            }
        }

        /// <summary>
        /// 加载对象回调
        /// </summary>
        private Dictionary<long, LoadObjectCallBack> mLoadObjectCallBackDic = new Dictionary<long, LoadObjectCallBack>();

        /// <summary>
        /// 等待加载的资源列表
        /// </summary>
        private List<HotFileInfo> mWaitLoadAssetsList = new List<HotFileInfo>();
        /// <summary>
        /// 所有图集图片的集合
        /// </summary>
        protected readonly Dictionary<string, UnityEngine.Object[]> mAllAssetObjectDic = new Dictionary<string, UnityEngine.Object[]>();
        public void Initlizate()
        {
            HotAssetsManager.DownLoadBundleFinish += AssetsDownLoadFinish;
        }

        #region 对象加载
        /// <summary>
        /// AssetBundle资源下载完成回调
        /// </summary>
        /// <param name="info"></param>
        private void AssetsDownLoadFinish(HotFileInfo info)
        {
            if (mWaitLoadAssetsList.Count==0) return;
            // Debug.Log("ResourceManager   AssetsDownLoadFinish:" + info.abName);
            //处理比AssetBunle配置文件先下载下来的AssetBunle的加载
            if (info.abName.Contains("bundleconfig"))
            {
                // Debug.Log("Handler waitLoadLsit Count:" + mWaitLoadAssetsList.Count);
                HotFileInfo[] hotFileArray = mWaitLoadAssetsList.ToArray();
                mWaitLoadAssetsList.Clear();
                foreach (var item in hotFileArray)
                {
                    AssetsDownLoadFinish(item);
                }
                return;
            }
            //如果回调字典长度大于0 才需要去处理回调
            if (mLoadObjectCallBackDic.Count > 0)
            {
                //根据对象的路径查找对象所在的AB包，以及这个AB下的所有的资源
                List<BundleItem> assetsItemList = AssetBundleManager.Instance.GetBundleItemByABName(info.abName);
                //如果assetsItemList.Count==0 则说明配置文件未加载，资源下载是多线程下，
                //有可能会出现 AssetBundle下载速度比AssetBundleConfig配置文件快，这种情况我们的AB配置文件就处于未加载的状态
                if (assetsItemList.Count == 0)
                {
                    for (int i = 0; i < mWaitLoadAssetsList.Count; i++)
                    {
                        //去重
                        if (mWaitLoadAssetsList[i].abName == info.abName)
                        {
                            return;
                        }
                    }
                    mWaitLoadAssetsList.Add(info);
                    return;
                }

                List<long> removeList = new List<long>();
                //遍历对象加载回调，触发资源加载
                foreach (var item in mLoadObjectCallBackDic)
                {
                    if (ListContainsAsset(assetsItemList, item.Value.crc))
                    {
                        Debug.Log("ResourceManager AssetsDownLoadFinish Load Obj path:" + item.Value.path);
                        item.Value.loadResult?.Invoke(Instantiate(item.Value.path, null, Vector3.zero, Vector3.one, Quaternion.identity),
                            item.Value.param1, item.Value.param2);
                        removeList.Add(item.Key);
                    }
                }
                //移除字典中的回调
                for (int i = 0; i < removeList.Count; i++)
                {
                    mLoadObjectCallBackDic.Remove(removeList[i]);
                    UntrackAsyncTask(removeList[i]);
                }
            }
        }
        public bool ListContainsAsset(List<BundleItem> assetsItemList, uint crc)
        {
            foreach (var item in assetsItemList)
            {
                if (item.crc == crc)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 预加载对象
        /// </summary>
        /// <param name="path"></param>
        /// <param name="count"></param>
        public void PreLoadObj(string path, int count = 1)
        {
            List<GameObject> preLoadObjList = new List<GameObject>();
            for (int i = 0; i < count; i++)
            {
                preLoadObjList.Add(Instantiate(path, null, Vector3.zero, Vector3.one, Quaternion.identity));
            }
            //回收对象到对象池
            foreach (var obj in preLoadObjList)
            {
                Release(obj);
            }
        }
        public async UniTask PreLoadObjAsync(string path, int count = 1)
        {
            for (int i = 0; i < count; i++)
            {
                AssetsRequest request = await InstantiateAsync(path,null);
                if (request == null)
                {
                    Debug.LogWarning($"预加载实例未完成，已停止后续预加载：{path}");
                    return;
                }
                request.Release();
            }
        }

        private AssetsRequest CreateAssetsRequest(
            object parameter1,
            object parameter2,
            object parameter3)
        {
            // AssetsRequest 会暴露给业务层，无法防止旧引用在对象被池化复用后误释放新请求。
            // 因此这里刻意为每次加载创建独立请求，实例资源仍由 ResourceManager 对象池管理。
            AssetsRequest request = new AssetsRequest();
            request.Initialize(this, parameter1, parameter2, parameter3);
            return request;
        }
        /// <summary>
        /// 同步克隆物体
        /// </summary>
        /// <param name="path"></param>
        /// <param name="parent"></param>
        /// <param name="localPoition"></param>
        /// <param name="localScale"></param>
        /// <param name="quaternion"></param>
        /// <returns></returns>
        public GameObject Instantiate(string path, Transform parent, Vector3 localPoition, Vector3 localScale, Quaternion quaternion)
        {
            path = path.EndsWith(".prefab") ? path : path + ".prefab";
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(Instantiate), out string operationModule)) return null;
            try
            {
            //先从对象池中查询这个对象，如果存在就直接使用
            CacheObejct cacheObj = GetCacheObjFromPools(crc);
            if (cacheObj != null && cacheObj.obj != null)
            {
                GameObject poolObject = cacheObj.obj;
                poolObject.transform.SetParent(parent);
                //重置数据
                SetObjectTransData(poolObject, localPoition, localScale, quaternion);
                //尝试使用原始数据
                TryUseOriginData(cacheObj);
                return cacheObj.obj;
            }
            //加载该对象
            GameObject obj = LoadResourceInternal<GameObject>(path);
            if (obj != null)
            {
                CacheObejct nObj = InstantiateObject(path, obj, parent);
                
                if (ReferenceEquals(nObj.originData, null) )
                {
                    //重置数据
                    //00 原代码对共享源对象 obj 设置 transform，会永久污染源 Prefab；改为对实例 nObj.obj 设置，与对象池路径一致。
                    SetObjectTransData(nObj.obj, localPoition, localScale, quaternion);
                }
                return nObj.obj;
            }
            else
            {
                Debug.LogError("GameObject load failed,path is null...");
                return null;
            }
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        private void SetObjectTransData(GameObject obj,Vector3 localPoition, Vector3 localScale, Quaternion quaternion)
        {
            obj.transform.localPosition = localPoition;
            obj.transform.localScale = localScale;
            obj.transform.rotation = quaternion;
        }
        private void TryUseOriginData(CacheObejct obj)
        {
            //重置数据
            if (!ReferenceEquals(obj.originData,null))
            {
                obj.originData.ResetOriginData();
            }
        }
        /// <summary>
        /// 克隆一个对象
        /// </summary>
        /// <param name="path"></param>
        /// <param name="obj"></param>
        /// <param name="parent"></param>
        /// <returns></returns>
        private CacheObejct InstantiateObject(string path, GameObject obj, Transform parent)
        {
            if (obj == null)
            {
                return mCacheObejctPool.Spawn();
            }
            obj = GameObject.Instantiate(obj, parent, false);
            CacheObejct cacheObejct = mCacheObejctPool.Spawn();
            cacheObejct.obj = obj;
            cacheObejct.path = path;
            cacheObejct.crc = Crc32.GetCrc32(path);
            cacheObejct.isInPool = false;
            if (obj !=null)
            {
                cacheObejct. insid= obj.GetInstanceID();
                cacheObejct.originData = obj.GetComponent<OriginData>();
                //重置原始数据
                TryUseOriginData(cacheObejct);
            }
            mAllObjectDic.TryAdd(cacheObejct.insid, cacheObejct);
            TrackInstance(cacheObejct);
            return cacheObejct;
        }
        private async UniTask<CacheObejct> InstantiateObjectAsync(string path, GameObject obj, Transform parent)
        {
            if (obj == null)
            {
                return null;
            }
          
        
            var request = GameObject.InstantiateAsync(obj, parent);
            // 等待实例化过程完成
            while (!request.isDone)
            {
                await UniTask.Yield(); // 异步地等待下一帧
            }

            if (request.Result == null || request.Result.Length == 0 || request.Result[0] == null)
                return null;

            obj = request.Result[0];
            CacheObejct cacheObejct = mCacheObejctPool.Spawn();
            cacheObejct.obj = obj;
            cacheObejct.path = path;
            cacheObejct.crc = Crc32.GetCrc32(path);
            cacheObejct.isInPool = false;
            if (obj !=null)
            {
                cacheObejct. insid= obj.GetInstanceID();
                cacheObejct.originData = obj.GetComponent<OriginData>();
                //重置原始数据
                TryUseOriginData(cacheObejct);
            }
            mAllObjectDic.TryAdd(cacheObejct.insid, cacheObejct);
            TrackInstance(cacheObejct);
            return cacheObejct;
        }
    
        /// <summary>
        /// 异步克隆对象
        /// </summary>
        /// <param name="path">路径</param>
        /// <param name="loadAsync">异步加载回调</param>
        /// <param name="param1">异步加载参数1</param>
        /// <param name="param2">异步加载参数2</param>
        public async void InstantiateAsync(string path,Transform parent, System.Action<GameObject, object> loadAsync, object param1 = null)
        {
            path = path.EndsWith(".prefab") ? path : path + ".prefab";
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(InstantiateAsync), out string operationModule))
            {
                loadAsync?.Invoke(null, param1);
                return;
            }
            bool wasAlreadyCached = mAlreayLoadAssetsDic.ContainsKey(crc);
            long loadTaskId = -1;
            bool ownershipDelivered = false;
            BeginAssetLoadConsumer(crc);
            try
            {
            //先从对象池中查询这个对象，如果存在就直接使用
            CacheObejct cacheObj = GetCacheObjFromPools(crc);
            if (cacheObj != null && cacheObj.obj != null)
            {
                cacheObj.obj.transform.SetParent(parent);
                //尝试使用原始数据
                TryUseOriginData(cacheObj);
                ownershipDelivered = true;
                loadAsync?.Invoke(cacheObj.obj, param1);
                return;
            }
            //获取异步加载任务唯一id
            loadTaskId = mAsyncTaskGuid;
            RegisterAsyncTask(loadTaskId, crc);
            //开始异步加载资源
            GameObject obj = await LoadResourceAsyncInternal<GameObject>(path, false);
            
            //异步加载完成
            if (obj != null)
            {
                if (mAsyncLoadingTaskList.Contains(loadTaskId))
                {
                    CacheObejct nObj = await InstantiateObjectAsync(path, obj, parent);
                    if (nObj?.obj == null && !wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    ownershipDelivered = nObj?.obj != null;
                    loadAsync?.Invoke(nObj?.obj, param1);
                }
                else
                {
                    // 任务已被模块清理取消：归还加载结果的所有权，避免缓存与 Bundle 泄漏
                    if (!wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    Debug.Log("Async Task already Cancel, release loaded asset. Path:" + path);
                    loadAsync?.Invoke(null, param1);
                }
            }
            else
            {
                if (!wasAlreadyCached)
                    RequestDeferredAssetRollback(crc);
                Debug.LogError("Async Load GameObject is Null Path:" + path);
                loadAsync?.Invoke(null, param1);
            }
            }
            finally
            {
                if (loadTaskId != -1)
                    CompleteAsyncTask(loadTaskId);
                if (!ownershipDelivered)
                    RequestDeferredAssetRollback(crc);
                EndAssetLoadConsumer(crc, true);
                EndModuleOperation(operationModule);
            }
        }
        /// <summary>
        /// 异步克隆对象 可通过await进行等待
        /// </summary>
        /// <param name="path">路径</param>
        /// <param name="loadAsync">异步加载回调</param>
        /// <param name="param1">异步加载参数1</param>
        /// <param name="param2">异步加载参数2</param>
        public async UniTask<AssetsRequest> InstantiateAsync(string path, Transform parent, object param1 = null, object param2 = null,object param3=null)
        {
            path = path.EndsWith(".prefab") ? path : path + ".prefab";
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(InstantiateAsync), out string operationModule)) return null;
            AssetsRequest request = null;
            long loadTaskId = -1;
            bool wasAlreadyCached = mAlreayLoadAssetsDic.ContainsKey(crc);
            bool ownershipDelivered = false;
            BeginAssetLoadConsumer(crc);
            try
            {
            request = CreateAssetsRequest(param1, param2, param3);
            //先从对象池中查询这个对象，如果存在就直接使用
            CacheObejct cacheObj = GetCacheObjFromPools(crc);
            if (cacheObj != null && cacheObj.obj != null)
            {
                cacheObj.obj.transform.SetParent(parent);
                //尝试使用原始数据
                TryUseOriginData(cacheObj);
                request.obj = cacheObj.obj;
                ownershipDelivered = true;
                return request;
            }
            //获取异步加载任务唯一id
            loadTaskId = mAsyncTaskGuid;
            RegisterAsyncTask(loadTaskId, crc);
            //开始异步加载资源
            GameObject loadObj = await LoadResourceAsyncInternal<GameObject>(path, false);
            if (loadObj == null)
            {
                if (!wasAlreadyCached)
                    RequestDeferredAssetRollback(crc);
                Debug.LogError("Load GameObject Failed Path：" + path);
                return null;
            }
            if (mAsyncLoadingTaskList.Contains(loadTaskId))
            {
                CacheObejct nObj = await InstantiateObjectAsync(path,loadObj, parent);
                if (nObj?.obj == null)
                {
                    if (!wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    return null;
                }
                request.obj = nObj.obj;
                ownershipDelivered = true;
                return request;
            }
            else
            {
                // 任务已被模块清理取消：归还加载结果的所有权，避免缓存与 Bundle 泄漏
                if (!wasAlreadyCached)
                    RequestDeferredAssetRollback(crc);
                Debug.LogError("Async Task already Cancel, release loaded asset. Path:" + path);
                return null;
            }
            }
            finally
            {
                if (loadTaskId != -1)
                    CompleteAsyncTask(loadTaskId);
                if (request != null && request.obj == null)
                    Release(request);
                if (!ownershipDelivered)
                    RequestDeferredAssetRollback(crc);
                EndAssetLoadConsumer(crc, true);
                EndModuleOperation(operationModule);
            }
        }
        /// <summary>
        /// 按需下载并异步实例化远端资源对象。
        /// </summary>
        /// <param name="path">Prefab 的项目相对资源路径；没有扩展名时自动补充 .prefab。</param>
        /// <param name="parent">实例化对象的父节点。</param>
        /// <param name="moduleName">资源物理归属模块，用于远端清单和模块生命周期门禁。</param>
        /// <param name="param1">随 AssetsRequest 返回的业务参数一。</param>
        /// <param name="param2">随 AssetsRequest 返回的业务参数二。</param>
        /// <param name="param3">随 AssetsRequest 返回的业务参数三。</param>
        /// <returns>成功时返回可释放的请求对象；下载、加载、取消或实例化失败时返回 null。</returns>
        public async UniTask<AssetsRequest> InstantiateRemoteAsync(string path, Transform parent,string moduleName, object param1, object param2, object param3, Action<float> onProgress = null)
        {
            path = path.EndsWith(".prefab") ? path : path + ".prefab";
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(InstantiateRemoteAsync), out string operationModule, moduleName)) return null;
            AssetsRequest request = null;
            long loadTaskId = -1;
            bool wasAlreadyCached = mAlreayLoadAssetsDic.ContainsKey(crc);
            bool ownershipDelivered = false;
            BeginAssetLoadConsumer(crc);
            try
            {
                request = CreateAssetsRequest(param1, param2, param3);

                CacheObejct cacheObj = GetCacheObjFromPools(crc);
                if (cacheObj != null && cacheObj.obj != null)
                {
                    cacheObj.obj.transform.SetParent(parent);
                    TryUseOriginData(cacheObj);
                    request.obj = cacheObj.obj;
                    ownershipDelivered = true;
                    return request;
                }

                loadTaskId = mAsyncTaskGuid;
                RegisterAsyncTask(loadTaskId, crc);
                GameObject loadedPrefab = await LoadRemoteAsyncInternal<GameObject>(
                    path,
                    moduleName,
                    onProgress);
                if (loadedPrefab == null)
                {
                    if (!wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    return null;
                }

                if (!mAsyncLoadingTaskList.Contains(loadTaskId))
                {
                    Debug.LogWarning($"远端资源实例化已取消，路径：{path}");
                    // 归还加载结果的所有权，避免缓存与 Bundle 泄漏
                    if (!wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    return null;
                }

                CacheObejct instantiatedObject = await InstantiateObjectAsync(path, loadedPrefab, parent);
                if (instantiatedObject?.obj == null)
                {
                    if (!wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    return null;
                }

                request.obj = instantiatedObject.obj;
                ownershipDelivered = true;
                return request;
            }
            finally
            {
                if (loadTaskId != -1)
                    CompleteAsyncTask(loadTaskId);
                if (request != null && request.obj == null)
                    Release(request);
                if (!ownershipDelivered)
                    RequestDeferredAssetRollback(crc);
                EndAssetLoadConsumer(crc, true);
                EndModuleOperation(operationModule);
            }
        }

        /// <summary>
        /// 查询远端资源当前是否具备无需下载即可加载的主 Bundle 与依赖闭包。
        /// </summary>
        public async UniTask<RemoteAssetLocalResult> GetRemoteLocalStatusAsync<T>(
            string path,
            string moduleName,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            ZMAsset.ValidateAssetPath(path);
            ZMAsset.ValidateModuleName(moduleName);
            cancellationToken.ThrowIfCancellationRequested();

#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                T editorAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
                return new RemoteAssetLocalResult(
                    path,
                    moduleName,
                    editorAsset == null
                        ? RemoteAssetLocalStatus.AssetNotConfigured
                        : RemoteAssetLocalStatus.Ready,
                    editorAsset == null ? 0 : 1,
                    null);
            }
#endif

            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(
                    crc,
                    nameof(GetRemoteLocalStatusAsync),
                    out string operationModule,
                    moduleName))
            {
                return new RemoteAssetLocalResult(
                    path,
                    moduleName,
                    RemoteAssetLocalStatus.Invalid,
                    0,
                    null);
            }

            try
            {
                if (mAlreayLoadAssetsDic.TryGetValue(crc, out BundleItem loadedItem) &&
                    loadedItem?.obj != null)
                {
                    if (!string.Equals(
                            loadedItem.bundleModuleType,
                            moduleName,
                            StringComparison.Ordinal))
                    {
                        return new RemoteAssetLocalResult(
                            path,
                            moduleName,
                            RemoteAssetLocalStatus.AssetNotConfigured,
                            0,
                            null);
                    }

                    List<ModuleBundleKey> requiredBundles =
                        RemoteAssetSystem.CollectRequiredBundleKeys(loadedItem, moduleName);
                    return new RemoteAssetLocalResult(
                        path,
                        moduleName,
                        loadedItem.obj is T
                            ? RemoteAssetLocalStatus.Ready
                            : RemoteAssetLocalStatus.Invalid,
                        requiredBundles.Count,
                        null);
                }

                return await RemoteAssetSystem.Instance.GetLocalStatusAsync(
                    path,
                    moduleName,
                    crc,
                    cancellationToken);
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        /// <summary>
        /// 闲时预下载整个模块的远端文件；Editor 加载模式下资源全在本地，直接返回空成功结果。
        /// </summary>
        public async UniTask<RemotePreDownloadResult> PreDownloadModuleAsync(string moduleName, Action<float> onProgress = null, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(moduleName) || moduleName == BundleModuleName.None)
            {
                Debug.LogError("远端资源预下载必须指定真实模块");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                Debug.Log($"Editor 加载模式下无需远端预下载，模块：{moduleName}");
                return new RemotePreDownloadResult(moduleName, true, 0, 0, null);
            }
#endif
            return await RemoteAssetSystem.Instance.PreDownloadModuleAsync(moduleName, onProgress, cancellationToken);
        }

        /// <summary>
        /// 闲时预下载指定资源的主 Bundle 及其同模块依赖；Editor 加载模式下直接返回空成功结果。
        /// </summary>
        public async UniTask<RemotePreDownloadResult> PreDownloadAssetAsync(string path, string moduleName, Action<float> onProgress = null, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

            if (string.IsNullOrWhiteSpace(moduleName) || moduleName == BundleModuleName.None)
            {
                Debug.LogError($"远端资源预下载必须指定真实模块，路径：{path}");
                return new RemotePreDownloadResult(moduleName, false, 0, 0, null);
            }

#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                Debug.Log($"Editor 加载模式下无需远端预下载，模块：{moduleName}，路径：{path}");
                return new RemotePreDownloadResult(moduleName, true, 0, 0, null);
            }
#endif
            uint crc = Crc32.GetCrc32(path);
            return await RemoteAssetSystem.Instance.PreDownloadAssetAsync(moduleName, crc, onProgress, cancellationToken);
        }
        /// <summary>
        /// 克隆并且等待资源下载完成克隆
        /// </summary>
        /// <param name="path"></param>
        /// <param name="loadAsync"></param>
        /// <param name="loading"></param>
        /// <param name="param1"></param>
        /// <param name="param2"></param>
        /// <returns></returns>
        public long InstantiateAndLoad(string path, Transform parent, System.Action<GameObject, object, object> loadAsync, System.Action loading, object param1 = null, object param2 = null)
        {
            path = path.EndsWith(".prefab") ? path : path + ".prefab";
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(InstantiateAndLoad), out string operationModule)) return -1;
            try
            {
            //先从对象池中查询这个对象，如果存在就直接使用
            CacheObejct cacheObj = GetCacheObjFromPools(crc);
            long loadid = -1;
            if (cacheObj != null && cacheObj.obj != null)
            {
                cacheObj.obj.transform.SetParent(parent);
                //尝试使用原始数据
                TryUseOriginData(cacheObj);
                
                loadAsync?.Invoke(cacheObj.obj, param1, param2);
                return loadid;
            }

            GameObject obj = Instantiate(path, parent, Vector3.zero, Vector3.one, Quaternion.identity);

            if (obj != null)
            {
                loadAsync?.Invoke(obj, param1, param2);
            }
            else
            {
                //资源没有下载完成，本地没有这个资源
                loadid = mAsyncTaskGuid;
                loading?.Invoke();
                mLoadObjectCallBackDic.Add(loadid, new LoadObjectCallBack
                {
                    path = path,
                    crc = Crc32.GetCrc32(path),
                    loadResult = loadAsync,
                    param1 = param1,
                    param2 = param2
                });
                TrackAsyncTask(loadid, Crc32.GetCrc32(path));
            }
            return loadid;
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        /// <summary>
        /// 从对象池中取出对象
        /// </summary>
        /// <param name="crc"></param>
        /// <returns></returns>
        private CacheObejct GetCacheObjFromPools(uint crc)
        {
            mObjectPoolDic.TryGetValue(crc, out var objList);
            if (objList != null && objList.Count > 0)
            {
                //直接取对象池中的第0个对象
                CacheObejct obj = objList[^1];
                objList.Remove(obj);
                obj.isInPool = false;
                if (objList.Count == 0) mObjectPoolDic.Remove(crc);
                return obj;
            }
            return null;
        }

   



        #endregion

        #region 资源加载
        /// <summary>
        /// 预加载资源
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="path"></param>
        public void PreLoadResource<T>(string path) where T : UnityEngine.Object
        {
            LoadResourceInternal<T>(path);
        }

        /// <summary>
        /// 异步准备场景 Bundle，并把它纳入现有资源缓存和模块释放生命周期。
        /// 该两阶段接口避免 WebGL 在 LoadSceceAsync 的同步返回点阻塞浏览器主线程。
        /// </summary>
        public async UniTask<bool> PrepareSceneAsync(string path)
        {
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                path += ".unity";

#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
                return UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEditor.SceneAsset>(path) != null;
#endif

            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(PrepareSceneAsync), out string operationModule))
                return false;

            try
            {
                BundleItem cachedItem = GetCacheItemFormAssetDic(crc);
                if (cachedItem?.assetBundle != null)
                    return true;

                BundleItem loadedItem = await AssetBundleManager.Instance.LoadAssetBundleAsync(crc);
                if (loadedItem?.assetBundle == null)
                {
                    Debug.LogError($"异步准备场景 Bundle 失败：{path}");
                    return false;
                }

                loadedItem.path = path;
                loadedItem.crc = crc;
                TrackLoadedAsset(crc, loadedItem, path);
                return true;
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        public   AsyncOperation LoadSceceAsync(string path,LoadSceneMode loadSceneMode= LoadSceneMode.Additive)
        {
            if (!path.EndsWith(".unity")) path += ".unity";
            string sceneName= System.IO.Path.GetFileNameWithoutExtension(path);
#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                bool isContain = false;
                foreach (UnityEditor.EditorBuildSettingsScene sceneItem in UnityEditor.EditorBuildSettings.scenes)
                {
                    if (sceneItem.path.Contains(sceneName))
                    {
                        isContain = true;
                        break;
                    }
                }
                if (isContain == false)
                {
                    Debug.Log($"BuildSetting In Scene list not Find {sceneName} Scence,Plase Add {sceneName} to scene list!");
                    return null;
                }
                else
                {
                    return SceneManager.LoadSceneAsync(sceneName, loadSceneMode);
                }
            }
#endif
                uint crc = Crc32.GetCrc32(path);
                if (!TryBeginModuleOperation(crc, nameof(LoadSceceAsync), out string operationModule)) return null;
                try
                {
                //从缓存中获取我们Bundleitem
                BundleItem item = GetCacheItemFormAssetDic(crc);
                if (item == null || item.assetBundle ==null)
                {
                    item= AssetBundleManager.Instance.LoadAssetBundle(crc);
                    if (item != null)
                    {
                        item.path = path;
                        item.crc = crc;
                        TrackLoadedAsset(crc, item, path);
                    }
                }
                if (item?.assetBundle == null)
                {
                    Debug.LogError(
                        $"场景 Bundle 尚未准备完成：{path}。WebGL 请先 await ZMAsset.Resources.PrepareSceneAsync(path)。");
                    EndModuleOperation(operationModule);
                    return null;
                }
                AsyncOperation sceneOperation = SceneManager.LoadSceneAsync(sceneName, loadSceneMode);
                if (sceneOperation == null)
                {
                    EndModuleOperation(operationModule);
                }
                else
                {
                    //00 场景加载的 Busy 生命周期必须覆盖到 AsyncOperation 真正完成。
                    sceneOperation.completed += _ => EndModuleOperation(operationModule);
                }
                return sceneOperation;
                }
                catch
                {
                    EndModuleOperation(operationModule);
                    throw;
                }
         

        }
 

        public AssetHandle<T> LoadScriptableObject<T>(string path) where T : UnityEngine.Object
        {
            if (!path.EndsWith(".asset")) path += ".asset";
            return LoadResource<T>(path);
        }

        /// <summary>
        /// 同步加载资源并返回一次独立所有权句柄。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="path"></param>
        /// <returns></returns>
        public AssetHandle<T> LoadResource<T>(string path) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }

            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadResource), out string operationModule))
                return null;
            try
            {
                T asset = LoadResourceInternal<T>(path);
                return CreateAssetHandle(crc, asset, path);
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        private T LoadResourceInternal<T>(string path) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadResource), out string operationModule)) return null;
            try
            {
            //从缓存中获取我们Bundleitem
            BundleItem item = GetCacheItemFormAssetDic(crc);

            //如果BundleItem中的对象已经加载过，就直接返回该对象
            if (item.obj != null)
            {
                return item.obj as T;
            }
            if (HasAssetLoadConsumer(crc))
            {
                Debug.LogError($"资源正在异步加载，已拒绝同 CRC 的同步重复加载。Path:{path}");
                return null;
            }

            //声明新对象
            T obj = null;
#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                obj = LoadAssetsFormEditor<T>(path);
            }
#endif
            if (obj == null)
            {
                //加载该路径对应的AssetBundle
                item = AssetBundleManager.Instance.LoadAssetBundle(crc);
                if (item != null)
                {
                    if (item.assetBundle != null)
                    {
                        obj = item.obj != null ? item.obj as T : item.assetBundle.LoadAsset<T>(item.assetName);
                    }
                    else
                    {
                        Debug.LogError("item.AssetBundle Is Null!");
                    }
                }
                else
                {
                    // AssetBundleManager 已记录模块、Bundle 和平台上下文，此处只传播失败，避免重复且不可操作的空对象日志。
                    return null;
                }
            }

            item.obj = obj;
            item.path = path;
            if (obj != null)
            {
                TrackLoadedAsset(crc, item, path);
            }
            else if (item.assetBundle != null)
            {
                AssetBundleManager.Instance.ReleaseAssets(item, true);
            }
            
            return obj;
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        /// <summary>
        /// 同步加载指定文件中的全部资源，仅供 TexturePacker 图集内部使用。
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="path"></param>
        /// <returns></returns>
        private T[] LoadAllResourceInternal<T>(string path) where T : UnityEngine.Object
        {

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadAllResourceInternal), out string operationModule)) return null;
            try
            {
            //从缓存中获取我们Bundleitem
            BundleItem item = GetCacheItemFormAssetDic(crc);

            //如果BundleItem中的对象已经加载过，就直接返回该对象
            if (item.objArr != null)
            {
                return item.objArr as T[];
            }
            if (HasAssetLoadConsumer(crc))
            {
                Debug.LogError($"资源正在异步加载，已拒绝同 CRC 的同步批量加载。Path:{path}");
                return null;
            }

            //声明新对象
            UnityEngine.Object[] objArr = null;
#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                objArr = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path);
            }
#endif
            if (objArr == null)
            {
                //加载该路径对应的AssetBundle
                item = AssetBundleManager.Instance.LoadAssetBundle(crc);
                if (item != null)
                {
                    if (item.assetBundle != null)
                    {
                        objArr = item.objArr != null ? item.objArr : item.assetBundle.LoadAllAssets<T>();
                    }
                    else
                    {
                        Debug.LogError("item.AssetBundle Is Null!");
                    }
                }
                else
                {
                    // AssetBundleManager 已记录真正失败原因；批量同步入口不再用泛化的 item null 覆盖诊断重点。
                    return null;
                }
            }

            item.objArr = objArr;
            item.path = path;
            if (objArr != null)
            {
                TrackLoadedAsset(crc, item, path);
            }
            else if (item.assetBundle != null)
            {
                AssetBundleManager.Instance.ReleaseAssets(item, true);
            }

            T[] loadedAssets = objArr as T[];
            return loadedAssets;
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }
        /// <summary>
        /// 异步加载指定文件中的全部资源，仅供 TexturePacker 图集内部使用。
        /// </summary>
        /// <param name="path">参与 CRC 查询的项目相对资源路径。</param>
        /// <param name="isEncrypt">是否强制使用加密加载。</param>
        /// <returns>文件包含的全部 Unity 资源；加载失败时返回 null。</returns>
        private async UniTask<UnityEngine.Object[]> LoadAllResourceAsyncInternal(
            string path,
            bool isEncrypt = false)
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }

            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadAllResourceAsyncInternal), out string operationModule))
                return null;
            try
            {
                BundleItem item = GetCacheItemFormAssetDic(crc);
                if (item.objArr != null)
                    return item.objArr;

                UnityEngine.Object[] loadedAssets = null;
#if UNITY_EDITOR
                if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
                    loadedAssets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path);
#endif
                if (loadedAssets == null)
                {
                    item = await AssetBundleManager.Instance.LoadAssetBundleAsync(crc, isEncrypt);
                    if (item == null)
                        return null;
                    if (item.objArr != null)
                        return item.objArr;
                    if (item.assetBundle == null)
                    {
                        Debug.LogError($"AssetBundle is null while loading atlas assets. Path:{path}");
                        return null;
                    }

                    item.path = path;
                    item.crc = crc;
                    TrackLoadedAsset(crc, item, path);
                    AssetBundleRequest request = item.assetBundle.LoadAllAssetsAsync<UnityEngine.Object>();
                    await request;
                    loadedAssets = request.allAssets;
                }

                item.objArr = loadedAssets;
                item.path = path;
                item.crc = crc;
                TrackLoadedAsset(crc, item, path);
                if (loadedAssets == null)
                    RequestDeferredAssetRollback(crc);
                return loadedAssets;
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        /// <summary>
        /// 按需下载并异步加载不需要实例化的远端资源。
        /// </summary>
        /// <typeparam name="T">期望加载的 Unity 资源类型。</typeparam>
        /// <param name="path">参与 CRC 查询的项目相对资源路径。</param>
        /// <param name="moduleName">资源物理归属模块。</param>
        /// <param name="onProgress">下载与加载进度回调。</param>
        /// <param name="cancellationToken">取消只中止本次消费者请求；共享下载完成后会自动回滚本次未交付的资源所有权。</param>
        /// <returns>加载成功的资源句柄；校验、下载或类型转换失败时返回 null。</returns>
        public async UniTask<AssetHandle<T>> LoadRemoteAsync<T>(
            string path,
            string moduleName,
            Action<float> onProgress = null,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }

            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadRemoteAsync), out string operationModule, moduleName))
                return null;
            bool wasAlreadyCached = mAlreayLoadAssetsDic.ContainsKey(crc);
            bool ownershipDelivered = false;
            BeginAssetLoadConsumer(crc);
            try
            {
                T asset = await LoadRemoteAsyncInternal<T>(path, moduleName, onProgress);
                if (cancellationToken.IsCancellationRequested)
                {
                    if (!wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (asset == null && !wasAlreadyCached)
                    RequestDeferredAssetRollback(crc);
                AssetHandle<T> handle = CreateAssetHandle(crc, asset, path);
                ownershipDelivered = handle != null;
                return handle;
            }
            finally
            {
                if (!ownershipDelivered)
                    RequestDeferredAssetRollback(crc);
                EndAssetLoadConsumer(crc, true);
                EndModuleOperation(operationModule);
            }
        }

        private async UniTask<T> LoadRemoteAsyncInternal<T>(
            string path,
            string moduleName,
            Action<float> onProgress) where T : UnityEngine.Object
        {

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }
            if (string.IsNullOrWhiteSpace(moduleName) || moduleName == BundleModuleName.None)
            {
                Debug.LogError($"远端资源加载必须指定真实模块，路径：{path}");
                return null;
            }
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadRemoteAsync), out string operationModule, moduleName)) return null;
            try
            {
            //从缓存中获取我们Bundleitem
            BundleItem item = GetCacheItemFormAssetDic(crc);

            //如果BundleItem中的对象已经加载过，就直接返回该对象
            if (item.obj != null)
            {
                return item.obj as T;
            }

            //声明新对象
            T obj = null;
#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                obj = LoadAssetsFormEditor<T>(path);
                if (obj==null)
                {
                    Debug.LogError("Load Object is null, Path:"+ path);
                    return null;
                }
                item.obj = obj;
                item.path = path;
                TrackLoadedAsset(crc, item, path);
                return obj;
            }
#endif
            if (obj == null)
            {
                // RemoteAsset 始终使用显式模块查找缺失 Bundle；普通本地加载由 Resources 门面负责。
                // 下载进度由 RemoteAssetSystem.PrepareAssetAsync 按"主包+同模块依赖"口径直接汇报。
                item = await AssetBundleManager.Instance.LoadRemoteAssetBundleAsync(crc, moduleName, onProgress);

                if (item == null)
                {
                    Debug.LogError("item is null ...Path:" + path);
                    return null;
                }
                
                if (item.obj != null)
                {
                    item.path = path;
                    item.crc = crc;
                    TrackLoadedAsset(crc, item, path);
                    return item.obj as T;
                }
                if (item.assetBundle == null)
                {
                    Debug.LogError($"AssetBundle is null while loading remote asset. Path:{path}");
                    return null;
                }
                item.path = path;
                item.crc = crc;
                TrackLoadedAsset(crc, item, path);
                //通过异步方式加载AssetBudnle
                T loadObj = await item.assetBundle.LoadAssetAsync<T>(item.assetName) as T;
                item.path = path;
                item.crc = crc;
                if (loadObj != null)
                    item.obj = loadObj;
                TrackLoadedAsset(crc, item, path);
                if (loadObj == null)
                    RequestDeferredAssetRollback(crc);
                return loadObj;
            }
            return null;
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        public async UniTask<AssetHandle<T>> LoadResourceAsync<T>(
            string path,
            CancellationToken cancellationToken = default) where T : UnityEngine.Object
        {
            return await LoadResourceHandleAsync<T>(path, false, cancellationToken);
        }
        /// <summary>
        /// 异步加载加密资源
        /// </summary>
        /// <param name="path"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public async UniTask<AssetHandle<T>> LoadResourceAsync<T>(
            string path,
            bool isEncrypt,
            CancellationToken cancellationToken = default) where T : Object
        {
            return await LoadResourceHandleAsync<T>(path, isEncrypt, cancellationToken);
        }

        private async UniTask<AssetHandle<T>> LoadResourceHandleAsync<T>(
            string path,
            bool isEncrypt,
            CancellationToken cancellationToken) where T : Object
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }

            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadResourceAsync), out string operationModule))
                return null;
            bool wasAlreadyCached = mAlreayLoadAssetsDic.ContainsKey(crc);
            bool ownershipDelivered = false;
            BeginAssetLoadConsumer(crc);
            try
            {
                T asset = await LoadResourceAsyncInternal<T>(path, isEncrypt);
                if (cancellationToken.IsCancellationRequested)
                {
                    if (!wasAlreadyCached)
                        RequestDeferredAssetRollback(crc);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (asset == null && !wasAlreadyCached)
                    RequestDeferredAssetRollback(crc);
                AssetHandle<T> handle = CreateAssetHandle(crc, asset, path);
                ownershipDelivered = handle != null;
                return handle;
            }
            finally
            {
                if (!ownershipDelivered)
                    RequestDeferredAssetRollback(crc);
                EndAssetLoadConsumer(crc, true);
                EndModuleOperation(operationModule);
            }
        }

        private async UniTask<T> LoadResourceAsyncInternal<T>(
            string path,
            bool isEncrypt) where T : Object
        {
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("path is Null , return null!");
                return null;
            }
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadResourceAsync), out string operationModule)) return null;
            try
            {
            //从缓存中获取我们Bundleitem
            BundleItem item = GetCacheItemFormAssetDic(crc);

            //如果BundleItem中的对象已经加载过，就直接返回该对象
            if (item.obj != null)
            {
                return item.obj as T;
            }

            //声明新对象
            T obj = null;
#if UNITY_EDITOR
            if (BundleSettings.Instance.loadAssetType == LoadAssetEnum.Editor)
            {
                obj = LoadAssetsFormEditor<T>(path);
                if (obj==null)
                {
                    Debug.LogError("Load Object is null, Path:"+ path);
                    return null;
                }
                item.obj = obj;
                item.path = path;
                TrackLoadedAsset(crc, item, path);
                return obj;
            }
#endif
            if (obj == null)
            {
                //加载该路径对应的AssetBundle
                item = await AssetBundleManager.Instance.LoadAssetBundleAsync(crc,isEncrypt);
  
                if (item == null)
                {
                    Debug.LogError("item is null ...Path:" + path);
                    return null;
                }
                
                if (item.obj != null)
                {
                    item.path = path;
                    item.crc = crc;
                    TrackLoadedAsset(crc, item, path);
                    return item.obj as T;
                }
                if (item.assetBundle == null)
                {
                    Debug.LogError($"AssetBundle is null while loading asset. Path:{path}");
                    return null;
                }
                item.path = path;
                item.crc = crc;
                TrackLoadedAsset(crc, item, path);
                //通过异步方式加载AssetBudnle
                T loadObj = await item.assetBundle.LoadAssetAsync<T>(item.assetName) as T;
                item.path = path;
                item.crc = crc;
                if (loadObj != null)
                    item.obj = loadObj;
                TrackLoadedAsset(crc, item, path);
                if (loadObj == null)
                    RequestDeferredAssetRollback(crc);
                return loadObj;
            }
            return null;
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }

        /// <summary>
        /// 从缓存中获取我们Bundleitem
        /// </summary>
        /// <param name="crc"></param>
        /// <returns></returns>
        private BundleItem GetCacheItemFormAssetDic(uint crc)
        {
            mAlreayLoadAssetsDic.TryGetValue(crc, out var item);
            if (item==null)
            {
                //00 查询缓存不得产生引用副作用；成功加载后由 TrackLoadedAsset 统一建立 0/1 缓存持有状态。
                return new BundleItem { crc = crc, refCount = 0 };
            }
            AssertLoadedAssetInvariant(crc, item, "GetCacheItemFormAssetDic");
            return item;
        }

        /// <summary>
        /// 将已成功加载的资源登记为主缓存持有状态。
        /// </summary>
        //00 refCount 不再表达实例数量，只表达资源是否被主缓存持有，因此合法值只能为 0 或 1。
        private void TrackLoadedAsset(uint crc, BundleItem item, string path)
        {
            if (item == null) return;

            item.crc = crc;
            item.path = path;
            if (mAlreayLoadAssetsDic.TryGetValue(crc, out BundleItem existingItem))
            {
                existingItem.refCount = 1;
                TrackModuleLoadedAsset(existingItem);
                AssertLoadedAssetInvariant(crc, existingItem, "TrackLoadedAsset-hit");
                return;
            }

            item.refCount = 1;
            mAlreayLoadAssetsDic.Add(crc, item);
            TrackModuleLoadedAsset(item);
            AssertLoadedAssetInvariant(crc, item, "TrackLoadedAsset-add");
        }

        private bool EvictLoadedAsset(uint crc, bool unloadLoadedObjects)
        {
            return EvictLoadedAssetCore(crc, unloadLoadedObjects, out _);
        }

        private bool EvictLoadedAssetCore(uint crc, bool unloadLoadedObjects, out bool removedAtlasCache)
        {
            removedAtlasCache = false;
            if (!mAlreayLoadAssetsDic.TryGetValue(crc, out BundleItem item)) return false;

            //00 先移除主缓存所有权再释放底层 Bundle，使重入查询不会观察到正在卸载的缓存项。
            mAlreayLoadAssetsDic.Remove(crc);
            InvalidateAssetHandles(crc);
            // TexturePacker additionally retains an Object[]; it must share the main cache eviction boundary.
            removedAtlasCache = RemoveAtlasCache(item.path, item.bundleModuleType);
            if (!string.IsNullOrWhiteSpace(item.bundleModuleType) &&
                mModuleResourceStateDic.TryGetValue(item.bundleModuleType, out ModuleResourceState moduleState))
                moduleState.loadedAssetCrcs.Remove(crc);
            item.refCount = 0;
            AssetBundleManager.Instance.ReleaseAssets(item, unloadLoadedObjects);
            AssertLoadedAssetInvariant(crc, item, "EvictLoadedAsset");
            return true;
        }

        private bool TryEvictLoadedAssetIfUnused(uint crc, bool unloadLoadedObjects)
        {
            if (HasTrackedObject(crc) || HasActiveAssetHandle(crc))
                return false;
            if (HasAssetLoadConsumer(crc))
            {
                RequestDeferredAssetRollback(crc);
                return false;
            }
            return EvictLoadedAsset(crc, unloadLoadedObjects);
        }

        private void BeginAssetLoadConsumer(uint crc)
        {
            mAssetLoadConsumerCountByCrc.TryGetValue(crc, out int count);
            mAssetLoadConsumerCountByCrc[crc] = count + 1;
        }

        private void RequestDeferredAssetRollback(uint crc)
        {
            mDeferredAssetRollbackCrcs.Add(crc);
        }

        private void EndAssetLoadConsumer(uint crc, bool unloadLoadedObjects)
        {
            if (!mAssetLoadConsumerCountByCrc.TryGetValue(crc, out int count))
                return;

            if (count > 1)
            {
                mAssetLoadConsumerCountByCrc[crc] = count - 1;
                return;
            }

            mAssetLoadConsumerCountByCrc.Remove(crc);
            if (!mDeferredAssetRollbackCrcs.Remove(crc))
                return;

            TryEvictLoadedAssetIfUnused(crc, unloadLoadedObjects);
        }

        private bool HasAssetLoadConsumer(uint crc)
        {
            return mAssetLoadConsumerCountByCrc.TryGetValue(crc, out int count) && count > 0;
        }

        /// <summary>
        /// 为返回给业务层的 Unity 资源创建一次独立句柄。
        /// 实例化内部使用的 Prefab 源对象不走该入口，其生命周期由实例索引管理。
        /// </summary>
        private AssetHandle<T> CreateAssetHandle<T>(
            uint crc,
            T asset,
            string path,
            AssetHandleReleasePolicy releasePolicy = AssetHandleReleasePolicy.RetainUntilAssetEviction)
            where T : UnityEngine.Object
        {
            if (asset == null) return null;

            long handleId = GetNextAssetHandleId();
            AssetHandleState state = new AssetHandleState(handleId);
            string bundleModuleType = string.Empty;
            if (mAlreayLoadAssetsDic.TryGetValue(crc, out BundleItem item) && item != null)
                bundleModuleType = item.bundleModuleType;

            AssetHandleRecord record = new AssetHandleRecord
            {
                handleId = handleId,
                crc = crc,
                path = path ?? string.Empty,
                bundleModuleType = bundleModuleType ?? string.Empty,
                asset = asset,
                state = state,
                releasePolicy = releasePolicy
            };
            mAssetHandleRecords.Add(handleId, record);
            if (!mAssetHandleIdsByCrc.TryGetValue(crc, out HashSet<long> handleIds))
            {
                handleIds = new HashSet<long>();
                mAssetHandleIdsByCrc.Add(crc, handleIds);
            }
            handleIds.Add(handleId);

            return new AssetHandle<T>(this, state, asset, path);
        }

        private long GetNextAssetHandleId()
        {
            do
            {
                if (mAssetHandleGuid <= 0 || mAssetHandleGuid == long.MaxValue)
                    mAssetHandleGuid = 1;
                else
                    mAssetHandleGuid++;
            } while (mAssetHandleRecords.ContainsKey(mAssetHandleGuid));

            return mAssetHandleGuid;
        }

        bool IAssetHandleOwner.IsAssetHandleMainThread =>
            Thread.CurrentThread.ManagedThreadId == mAssetHandleMainThreadId;

        bool IAssetHandleOwner.ReleaseAssetHandle(long handleId)
        {
            return ReleaseAssetHandle(handleId);
        }

        private bool ReleaseAssetHandle(long handleId)
        {
            if (!mAssetHandleRecords.TryGetValue(handleId, out AssetHandleRecord record))
                return false;

            mAssetHandleRecords.Remove(handleId);
            if (mAssetHandleIdsByCrc.TryGetValue(record.crc, out HashSet<long> handleIds))
            {
                handleIds.Remove(handleId);
                if (handleIds.Count == 0)
                    mAssetHandleIdsByCrc.Remove(record.crc);
            }

            record.state?.Invalidate();
            ReleaseTransientHandleAsset(record);
            TryEvictLoadedAssetIfUnused(record.crc, true);
            return true;
        }

        private bool HasActiveAssetHandle(uint crc)
        {
            return mAssetHandleIdsByCrc.TryGetValue(crc, out HashSet<long> handleIds) &&
                   handleIds.Count > 0;
        }

        private int GetActiveAssetHandleCount(uint crc)
        {
            return mAssetHandleIdsByCrc.TryGetValue(crc, out HashSet<long> handleIds)
                ? handleIds.Count
                : 0;
        }

        private void InvalidateAssetHandles(uint crc)
        {
            if (!mAssetHandleIdsByCrc.TryGetValue(crc, out HashSet<long> handleIds))
                return;

            HashSet<int> destroyedTransientInstanceIds = null;
            foreach (long handleId in new List<long>(handleIds))
            {
                if (!mAssetHandleRecords.TryGetValue(handleId, out AssetHandleRecord record))
                    continue;

                record.state?.Invalidate();
                if (record.releasePolicy == AssetHandleReleasePolicy.DestroyOnHandleRelease &&
                    record.asset != null)
                {
                    destroyedTransientInstanceIds ??= new HashSet<int>();
                    if (destroyedTransientInstanceIds.Add(record.asset.GetInstanceID()))
                        UnityEngine.Object.Destroy(record.asset);
                }
                record.asset = null;
                mAssetHandleRecords.Remove(handleId);
            }

            mAssetHandleIdsByCrc.Remove(crc);
        }

        private static void ReleaseTransientHandleAsset(AssetHandleRecord record)
        {
            if (record == null)
                return;
            if (record.releasePolicy == AssetHandleReleasePolicy.DestroyOnHandleRelease && record.asset != null)
                UnityEngine.Object.Destroy(record.asset);
            record.asset = null;
        }

        private bool HasTrackedObject(uint crc)
        {
            foreach (CacheObejct cacheObject in mAllObjectDic.Values)
            {
                if (cacheObject != null && cacheObject.crc == crc) return true;
            }
            return false;
        }

        private ModuleResourceState GetOrCreateModuleState(string bundleModule)
        {
            if (!mModuleResourceStateDic.TryGetValue(bundleModule, out ModuleResourceState state))
            {
                state = new ModuleResourceState();
                mModuleResourceStateDic.Add(bundleModule, state);
            }
            return state;
        }

        private string ResolveBundleModule(uint crc)
        {
            if (mAlreayLoadAssetsDic.TryGetValue(crc, out BundleItem loadedItem) &&
                !string.IsNullOrWhiteSpace(loadedItem.bundleModuleType)) return loadedItem.bundleModuleType;
            BundleItem configuredItem = AssetBundleManager.Instance.GetBundleItemByCrc(crc);
            return configuredItem == null ? string.Empty : configuredItem.bundleModuleType;
        }

        private void TrackModuleLoadedAsset(BundleItem item)
        {
            if (item == null) return;

            //00 Editor 直读资源会合成不带模块名的 BundleItem，必须从已加载配置按 CRC 回补真实归属。
            string bundleModule = string.IsNullOrWhiteSpace(item.bundleModuleType)
                ? ResolveBundleModule(item.crc)
                : item.bundleModuleType;
            if (string.IsNullOrWhiteSpace(bundleModule)) return;

            item.bundleModuleType = bundleModule;
            GetOrCreateModuleState(bundleModule).loadedAssetCrcs.Add(item.crc);
        }

        private void TrackInstance(CacheObejct cacheObject)
        {
            if (cacheObject == null) return;
            cacheObject.bundleModuleType = ResolveBundleModule(cacheObject.crc);
            if (string.IsNullOrWhiteSpace(cacheObject.bundleModuleType)) return;
            GetOrCreateModuleState(cacheObject.bundleModuleType).objectInstanceIds.Add(cacheObject.insid);
        }

        private void UntrackInstance(CacheObejct cacheObject)
        {
            if (cacheObject == null || string.IsNullOrWhiteSpace(cacheObject.bundleModuleType)) return;
            if (mModuleResourceStateDic.TryGetValue(cacheObject.bundleModuleType, out ModuleResourceState state))
                state.objectInstanceIds.Remove(cacheObject.insid);
        }

        private void TrackAtlas(string path)
        {
            string bundleModule = ResolveBundleModule(Crc32.GetCrc32(path));
            if (!string.IsNullOrWhiteSpace(bundleModule)) GetOrCreateModuleState(bundleModule).atlasPaths.Add(path);
        }

        private bool RemoveAtlasCache(string path, string bundleModule = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            bool removed = mAllAssetObjectDic.Remove(path);
            string ownerModule = string.IsNullOrWhiteSpace(bundleModule)
                ? ResolveBundleModule(Crc32.GetCrc32(path))
                : bundleModule;
            if (!string.IsNullOrWhiteSpace(ownerModule) &&
                mModuleResourceStateDic.TryGetValue(ownerModule, out ModuleResourceState state))
            {
                state.atlasPaths.Remove(path);
            }

            return removed;
        }

        private UnityEngine.Object[] CacheAtlasObjects(string path, UnityEngine.Object[] objects)
        {
            if (objects == null)
                return null;
            if (mAllAssetObjectDic.TryGetValue(path, out UnityEngine.Object[] cachedObjects) &&
                cachedObjects != null)
            {
                return cachedObjects;
            }

            mAllAssetObjectDic[path] = objects;
            TrackAtlas(path);
            return objects;
        }

        private bool TryGetCachedAtlasObjects(string path, uint crc, out UnityEngine.Object[] objects)
        {
            if (!mAllAssetObjectDic.TryGetValue(path, out objects))
                return false;
            if (objects != null && mAlreayLoadAssetsDic.ContainsKey(crc))
                return true;

            RemoveAtlasCache(path);
            objects = null;
            return false;
        }

        private void RollbackAtlasLoad(uint crc, string path)
        {
            if (HasAssetLoadConsumer(crc))
            {
                RequestDeferredAssetRollback(crc);
                return;
            }

            if (TryEvictLoadedAssetIfUnused(crc, true))
                return;

            // Clean up an orphaned TexturePacker cache left by an older runtime implementation.
            if (!mAlreayLoadAssetsDic.ContainsKey(crc) &&
                !HasActiveAssetHandle(crc) &&
                !HasTrackedObject(crc))
            {
                RemoveAtlasCache(path);
            }
        }

        private void TrackAsyncTask(long taskId, uint crc)
        {
            string bundleModule = ResolveBundleModule(crc);
            if (!string.IsNullOrWhiteSpace(bundleModule)) GetOrCreateModuleState(bundleModule).asyncTaskIds.Add(taskId);
        }

        private void RegisterAsyncTask(long taskId, uint crc)
        {
            mAsyncLoadingTaskList.Add(taskId);
            TrackAsyncTask(taskId, crc);
        }

        private void CompleteAsyncTask(long taskId)
        {
            mAsyncLoadingTaskList.Remove(taskId);
            UntrackAsyncTask(taskId);
        }

        private void UntrackAsyncTask(long taskId)
        {
            foreach (ModuleResourceState state in mModuleResourceStateDic.Values)
                state.asyncTaskIds.Remove(taskId);
        }

        private bool TryBeginModuleOperation(
            uint crc,
            string operation,
            out string bundleModule,
            string fallbackModule = null)
        {
            bundleModule = ResolveBundleModule(crc);
            if (string.IsNullOrWhiteSpace(bundleModule) &&
                !string.IsNullOrWhiteSpace(fallbackModule) &&
                !string.Equals(fallbackModule, BundleModuleName.None, StringComparison.Ordinal))
                bundleModule = fallbackModule;
            if (string.IsNullOrWhiteSpace(bundleModule)) return true;
            if (AssetBundleManager.Instance.TryBeginModuleLoad(bundleModule)) return true;
            //00 失败既可能是本模块正在清理，也可能是 Shared 依赖未初始化/正在清理；底层已输出精确原因。
            Debug.LogError($"模块 {bundleModule} 当前不可加载，拒绝资源操作：{operation}，CRC:{crc}");
            return false;
        }

        private static void EndModuleOperation(string bundleModule)
        {
            AssetBundleManager.Instance.EndModuleLoad(bundleModule);
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void AssertLoadedAssetInvariant(uint crc, BundleItem item, string operation)
        {
            bool isCached = mAlreayLoadAssetsDic.TryGetValue(crc, out BundleItem cachedItem);
            int expectedRefCount = isCached ? 1 : 0;
            Debug.Assert(
                item != null && item.refCount == expectedRefCount && (!isCached || ReferenceEquals(item, cachedItem)),
                $"资源缓存不变量失败：operation={operation}，crc={crc}，cached={isCached}，refCount={item?.refCount}");
        }

#if UNITY_EDITOR
        private T LoadAssetsFormEditor<T>(string path) where T : UnityEngine.Object
        {
            return UnityEditor.AssetDatabase.LoadAssetAtPath<T>(path);
        }
#endif
        /// <summary>
        /// 移除对象加载回调
        /// </summary>
        /// <param name="loadid"></param>
        public void RemoveObjectLoadCallBack(long loadid)
        {
            if (loadid == -1)
            {
                return;
            }
            if (mLoadObjectCallBackDic.ContainsKey(loadid))
            {
                mLoadObjectCallBackDic.Remove(loadid);
                UntrackAsyncTask(loadid);
            }
        }
        /// <summary>
        /// 释放对象占用内存
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="destroyCache"></param>
        public void Release(GameObject obj, bool destroyCache = false)
        {
            if (obj==null)
            {
                return;
            }
            int insid = obj.GetInstanceID();
            mAllObjectDic.TryGetValue(insid, out var cacheObejct);
            //通过Gameobject.Instantiate 不支持回收，因为对象池中没有记录
            if (cacheObejct == null)
            {
                Debug.LogError("Recycl Obj failed,obj is Gameobject.Instantiate...");
                return;
            }
            if (destroyCache)
            {
                DestroyTrackedHierarchy(obj);
            }
            else
            {
                if (cacheObejct.isInPool)
                {
                    Debug.LogWarning($"对象已经在资源池中，忽略重复回收：{cacheObejct.path} InstanceID:{insid}");
                    return;
                }
                //回收到对象池
                List<CacheObejct> objList = null;
                mObjectPoolDic.TryGetValue(cacheObejct.crc, out objList);
                //字典中没有该对象池
                if (objList == null)
                {
                    //创建对象池
                    objList = new List<CacheObejct>();
                    objList.Add(cacheObejct);
                    mObjectPoolDic.Add(cacheObejct.crc, objList);
                }
                else
                {
                    //回收到对象池
                    objList.Add(cacheObejct);
                }
                cacheObejct.isInPool = true;
                //会受到对象回收节点下
                if (cacheObejct.obj != null)
                {
                    cacheObejct.obj?.transform.SetParent(ZMAsset.RecyclObjPool);
                }
                else
                {
                    Debug.LogError("cacheObejct.obj is Null Release Failed!");
                }
            }
        }

        /// <summary>
        /// 注销根对象及所有由框架独立实例化的子对象，避免 Unity 级联销毁后留下失效实例索引。
        /// </summary>
        private void DestroyTrackedHierarchy(GameObject rootObject)
        {
            if (rootObject == null) return;

            Transform rootTransform = rootObject.transform;
            List<CacheObejct> trackedHierarchy = new List<CacheObejct>();
            HashSet<uint> affectedCrcs = new HashSet<uint>();
            foreach (CacheObejct trackedObject in new List<CacheObejct>(mAllObjectDic.Values))
            {
                if (trackedObject?.obj == null) continue;
                Transform trackedTransform = trackedObject.obj.transform;
                if (trackedObject.obj != rootObject && !trackedTransform.IsChildOf(rootTransform)) continue;
                trackedHierarchy.Add(trackedObject);
                affectedCrcs.Add(trackedObject.crc);
            }

            // 子对象先注销，确保父对象的 Unity 级联销毁不会让子对象提前变成无法定位的假空引用。
            trackedHierarchy.Sort((left, right) =>
                GetTransformDepth(right.obj.transform).CompareTo(GetTransformDepth(left.obj.transform)));
            foreach (CacheObejct trackedObject in trackedHierarchy)
                DestroyTrackedInstance(trackedObject);

            foreach (uint crc in affectedCrcs)
                TryEvictLoadedAssetIfUnused(crc, true);
        }

        private static int GetTransformDepth(Transform transform)
        {
            int depth = 0;
            while (transform != null)
            {
                depth++;
                transform = transform.parent;
            }
            return depth;
        }

        private bool DestroyTrackedInstance(CacheObejct cacheObject)
        {
            if (cacheObject == null) return false;
            int instanceId = cacheObject.insid;
            uint crc = cacheObject.crc;
            mAllObjectDic.Remove(instanceId);
            if (mObjectPoolDic.TryGetValue(crc, out List<CacheObejct> objectPoolList))
            {
                objectPoolList.Remove(cacheObject);
                if (objectPoolList.Count == 0) mObjectPoolDic.Remove(crc);
            }
            UntrackInstance(cacheObject);
            cacheObject.Release();
            mCacheObejctPool.Recycl(cacheObject);
            return true;
        }
        /// <summary>
        /// 加载图片资源
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public AssetHandle<Sprite> LoadSprite(string path)
        {
            // 只有调用方未提供扩展名时才使用默认 png，避免 jpg/png 被重复拼接。
            if (string.IsNullOrEmpty(Path.GetExtension(path))) path += ".png";
            return LoadResource<Sprite>(path);
        }
        /// <summary>
        /// 加载Texture图片
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public AssetHandle<Texture> LoadTexture(string path)
        {
            // 只有没有扩展名时才使用默认 jpg，允许 png、jpeg、tga 等实际纹理路径直接加载。
            if (string.IsNullOrEmpty(Path.GetExtension(path))) path += ".jpg";
            return LoadResource<Texture>(path);
        }
        /// <summary>
        /// 加载音频文件
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public AssetHandle<AudioClip> LoadAudio(string path)
        {
            return LoadResource<AudioClip>(path);
        }
        /// <summary>
        /// 加载Text资源
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public AssetHandle<TextAsset> LoadTextAsset(string path)
        {
            return LoadResource<TextAsset>(path);
        }
        /// <summary>
        /// 从图集中加载指定的图片
        /// </summary>
        /// <param name="atlasPath"></param>
        /// <param name="spriteName"></param>
        /// <returns></returns>
        public AssetHandle<Sprite> LoadAtlasSprite(string atlasPath, string spriteName)
        {
            if (!ValidateAtlasRequest(atlasPath, spriteName, nameof(LoadAtlasSprite)))
                return null;

            atlasPath = EnsureAtlasExtension(atlasPath, ".spriteatlas");
            uint crc = Crc32.GetCrc32(atlasPath);
            if (!TryBeginModuleOperation(crc, nameof(LoadAtlasSprite), out string operationModule))
                return null;
            try
            {
                SpriteAtlas atlas = LoadResourceInternal<SpriteAtlas>(atlasPath);
                Sprite sprite = FindSpriteInAtlas(atlas, spriteName);
                if (sprite == null)
                {
                    RollbackAtlasLoad(crc, atlasPath);
                    return null;
                }

                return CreateAssetHandle(crc, sprite, atlasPath, AssetHandleReleasePolicy.DestroyOnHandleRelease);
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }
        /// <summary>
        /// 异步加载 Unity SpriteAtlas 中指定名称的 Sprite。
        /// </summary>
        /// <param name="atlasPath">图集资源路径。</param>
        /// <param name="spriteName">Sprite 名称。</param>
        /// <param name="cancellationToken">取消当前调用；不会取消其他调用方共享的底层加载。</param>
        /// <returns>独立资源句柄；加载或查找失败时返回 null。</returns>
        public async UniTask<AssetHandle<Sprite>> LoadAtlasSpriteAsync(
            string atlasPath,
            string spriteName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidateAtlasRequest(atlasPath, spriteName, nameof(LoadAtlasSpriteAsync)))
                return null;

            atlasPath = EnsureAtlasExtension(atlasPath, ".spriteatlas");
            uint crc = Crc32.GetCrc32(atlasPath);
            if (!TryBeginModuleOperation(crc, nameof(LoadAtlasSpriteAsync), out string operationModule))
                return null;

            bool wasAlreadyCached = mAlreayLoadAssetsDic.ContainsKey(crc);
            bool ownershipDelivered = false;
            BeginAssetLoadConsumer(crc);
            try
            {
                SpriteAtlas atlas = await LoadResourceAsyncInternal<SpriteAtlas>(atlasPath, false);
                if (cancellationToken.IsCancellationRequested)
                {
                    if (!wasAlreadyCached)
                        RollbackAtlasLoad(crc, atlasPath);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                Sprite sprite = FindSpriteInAtlas(atlas, spriteName);
                if (sprite == null)
                {
                    RollbackAtlasLoad(crc, atlasPath);
                    return null;
                }

                AssetHandle<Sprite> handle = CreateAssetHandle(
                    crc,
                    sprite,
                    atlasPath,
                    AssetHandleReleasePolicy.DestroyOnHandleRelease);
                ownershipDelivered = handle != null;
                return handle;
            }
            finally
            {
                if (!ownershipDelivered)
                    RequestDeferredAssetRollback(crc);
                EndAssetLoadConsumer(crc, true);
                EndModuleOperation(operationModule);
            }
        }

        private Sprite FindSpriteInAtlas(SpriteAtlas spriteAtlas, string name)
        {
            if (spriteAtlas == null)
            {
                Debug.LogError("Not find spriteAtlas Name:" + name);
                return null;
            }

            //从图集中获取指定名称的图片
            Sprite sprite = spriteAtlas.GetSprite(name);
            if (sprite != null)
            {
                return sprite;
            }
            Debug.LogError("Not find Sprite  Name:" + name);
            return null;
        }


        /// <summary>
        /// 加载tpsheet图集
        /// </summary>
        /// <param name="path"></param>
        public AssetHandle<Sprite> LoadPNGAtlasSprite(string path, string name)
        {
            if (!ValidateAtlasRequest(path, name, nameof(LoadPNGAtlasSprite)))
                return null;

            path = EnsureAtlasExtension(path, ".png");
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadPNGAtlasSprite), out string operationModule))
                return null;
            try
            {
                // The auxiliary array is valid only while its primary BundleItem remains cached.
                if (TryGetCachedAtlasObjects(path, crc, out UnityEngine.Object[] objectArr))
                {
                    Sprite cachedSprite = FindSpriteInAtlas(objectArr, name);
                    if (cachedSprite != null)
                        return CreateAssetHandle(crc, cachedSprite, path);

                    RollbackAtlasLoad(crc, path);
                    return null;
                }

                UnityEngine.Object[] objects = LoadAllResourceInternal<UnityEngine.Object>(path);
                if (objects == null) return null;

                objects = CacheAtlasObjects(path, objects);
                Sprite sprite = FindSpriteInAtlas(objects, name);
                if (sprite == null)
                {
                    RollbackAtlasLoad(crc, path);
                    return null;
                }

                return CreateAssetHandle(crc, sprite, path);
            }
            finally
            {
                EndModuleOperation(operationModule);
            }
        }
        /// <summary>
        /// 异步加载 TexturePacker 图集中的指定 Sprite。
        /// </summary>
        /// <param name="path">PNG 图集路径。</param>
        /// <param name="name">Sprite 名称。</param>
        /// <param name="cancellationToken">取消当前调用；不会取消其他调用方共享的底层加载。</param>
        /// <returns>独立资源句柄；加载或查找失败时返回 null。</returns>
        public async UniTask<AssetHandle<Sprite>> LoadPNGAtlasSpriteAsync(
            string path,
            string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ValidateAtlasRequest(path, name, nameof(LoadPNGAtlasSpriteAsync)))
                return null;

            path = EnsureAtlasExtension(path, ".png");
            uint crc = Crc32.GetCrc32(path);
            if (!TryBeginModuleOperation(crc, nameof(LoadPNGAtlasSpriteAsync), out string operationModule))
                return null;

            bool wasAlreadyCached = mAlreayLoadAssetsDic.ContainsKey(crc);
            bool ownershipDelivered = false;
            BeginAssetLoadConsumer(crc);
            try
            {
                if (TryGetCachedAtlasObjects(path, crc, out UnityEngine.Object[] cachedObjects))
                {
                    Sprite cachedSprite = FindSpriteInAtlas(cachedObjects, name);
                    if (cachedSprite != null)
                    {
                        AssetHandle<Sprite> cachedHandle = CreateAssetHandle(crc, cachedSprite, path);
                        ownershipDelivered = cachedHandle != null;
                        return cachedHandle;
                    }

                    RollbackAtlasLoad(crc, path);
                    return null;
                }

                UnityEngine.Object[] objects = await LoadAllResourceAsyncInternal(path);
                if (cancellationToken.IsCancellationRequested)
                {
                    if (!wasAlreadyCached)
                        RollbackAtlasLoad(crc, path);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (objects == null)
                {
                    if (!wasAlreadyCached)
                        RollbackAtlasLoad(crc, path);
                    return null;
                }

                objects = CacheAtlasObjects(path, objects);
                Sprite sprite = FindSpriteInAtlas(objects, name);
                if (sprite == null)
                {
                    RollbackAtlasLoad(crc, path);
                    return null;
                }

                AssetHandle<Sprite> handle = CreateAssetHandle(crc, sprite, path);
                ownershipDelivered = handle != null;
                return handle;
            }
            finally
            {
                if (!ownershipDelivered)
                    RequestDeferredAssetRollback(crc);
                EndAssetLoadConsumer(crc, true);
                EndModuleOperation(operationModule);
            }
        }

        private Sprite FindSpriteInAtlas(UnityEngine.Object[] objects, string name)
        {
            if (objects == null)
                return null;

            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] is Sprite sprite &&
                    sprite != null &&
                    string.Equals(name, sprite.name, StringComparison.Ordinal))
                    return sprite;
            }

            Debug.LogError("没有找到名字为" + name + "的图片 请检查图片名称是否正确！");
            return null;
        }

        private static string EnsureAtlasExtension(string path, string extension)
        {
            return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                ? path
                : path + extension;
        }

        private static bool ValidateAtlasRequest(string path, string spriteName, string operation)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Debug.LogError($"Atlas path is empty. Operation:{operation}");
                return false;
            }
            if (string.IsNullOrWhiteSpace(spriteName))
            {
                Debug.LogError($"Sprite name is empty. Operation:{operation}, Path:{path}");
                return false;
            }

            return true;
        }
        /// <summary>
        /// 清理所有异步加载任务
        /// </summary>
        public void ClearAllAsyncLoadTask()
        {
            foreach (long taskId in mAsyncLoadingTaskList) UntrackAsyncTask(taskId);
            mAsyncLoadingTaskList.Clear();
        }
        /// <summary>
        /// 清理加载的资源，释放内存
        /// </summary>
        /// <param name="absoluteCleaning">深度清理：true：销毁所有由AssetBUnle加载和生成的对象，彻底释放内存占用
        /// 深度清理 false：销毁对象池中的对象，但不销毁由AssetBundle克隆出并在使用的对象，具体的内存释放根据内存引用计数选择性释放</param>
        public void ClearResourcesAssets(bool absoluteCleaning)
        {
            if (absoluteCleaning)
            {
                List<CacheObejct> allTrackedObjects = new List<CacheObejct>(mAllObjectDic.Values);
                foreach (CacheObejct cacheObject in allTrackedObjects)
                {
                    if (cacheObject == null) continue;
                    DestroyTrackedInstance(cacheObject);
                }
                mAllObjectDic.Clear();
                mObjectPoolDic.Clear();
                ClearAllAsyncLoadTask();
            }
            else
            {
                List<CacheObejct> pooledObjects = new List<CacheObejct>();
                foreach (List<CacheObejct> objList in mObjectPoolDic.Values)
                {
                    if (objList != null) pooledObjects.AddRange(objList);
                }
                foreach (CacheObejct cacheObject in pooledObjects)
                {
                    if (cacheObject == null) continue;
                    DestroyTrackedInstance(cacheObject);
                }
                mObjectPoolDic.Clear();
            }

            List<uint> loadedAssetCrcList = new List<uint>(mAlreayLoadAssetsDic.Keys);
            foreach (uint crc in loadedAssetCrcList)
            {
                // 浅清理同时保留活动实例和活动资源句柄；深度清理会使句柄失效并显式强制回收。
                if (absoluteCleaning ||
                    (!HasTrackedObject(crc) &&
                     !HasActiveAssetHandle(crc) &&
                     !HasAssetLoadConsumer(crc)))
                    EvictLoadedAsset(crc, absoluteCleaning);
            }

            //清理列表
            mLoadObjectCallBackDic.Clear();
            mAllAssetObjectDic.Clear();
            foreach (ModuleResourceState state in mModuleResourceStateDic.Values)
            {
                state.atlasPaths.Clear();
                state.asyncTaskIds.Clear();
            }
            if (absoluteCleaning) mModuleResourceStateDic.Clear();
            //00 深度清理已经移除全部框架跟踪资源，所有业务模块依赖租约同步进入停用状态。
            if (absoluteCleaning) AssetBundleManager.Instance.DeactivateAllModulesAfterGlobalClear();
            //释放未使用的资源 (未使用的资源指的是 没有被引用的资源)
            Resources.UnloadUnusedAssets();
            //触发GC垃圾回收
            System.GC.Collect();
        }

        /// <summary>
        /// 清理指定资源模块中由框架跟踪的缓存和对象。
        /// </summary>
        /// <remarks>
        /// PooledOnly 会在存在活动 GameObject 或 AssetHandle 时拒绝清理且不修改模块状态；
        /// ForceTrackedObjects 会销毁实例并使模块内的活动 AssetHandle 失效。
        /// </remarks>
        public async UniTask<ModuleClearResult> ClearModuleAssetsAsync(
            string bundleModule,
            ModuleClearMode mode = ModuleClearMode.PooledOnly)
        {
            ModuleClearResult result = new ModuleClearResult
            {
                bundleModule = bundleModule,
                status = ModuleClearStatus.Failed
            };
            if (string.IsNullOrWhiteSpace(bundleModule))
            {
                result.status = ModuleClearStatus.InvalidModule;
                result.message = "资源模块名称不能为空。";
                return result;
            }

            ModuleClearStatus beginStatus = await AssetBundleManager.Instance.BeginModuleClearAsync(bundleModule);
            if (beginStatus != ModuleClearStatus.Success)
            {
                result.status = beginStatus;
                result.message = beginStatus == ModuleClearStatus.NotInitialized
                    ? $"资源模块尚未初始化：{bundleModule}"
                    : beginStatus == ModuleClearStatus.DependencyInUse
                        ? $"资源模块仍被活动业务模块依赖，不能清理：{bundleModule}"
                        : $"资源模块当前无法清理：{bundleModule}，状态：{beginStatus}";
                return result;
            }

            try
            {
                await UniTask.SwitchToMainThread();
                mModuleResourceStateDic.TryGetValue(bundleModule, out ModuleResourceState state);
                if (state != null && state.asyncTaskIds.Count > 0)
                {
                    result.status = ModuleClearStatus.Busy;
                    result.message = $"资源模块仍有 {state.asyncTaskIds.Count} 个异步任务：{bundleModule}";
                    return result;
                }

                List<int> instanceIds = state == null
                    ? new List<int>()
                    : new List<int>(state.objectInstanceIds);
                List<uint> loadedAssetCrcs = state == null
                    ? new List<uint>()
                    : new List<uint>(state.loadedAssetCrcs);
                if (mode == ModuleClearMode.PooledOnly)
                {
                    foreach (int instanceId in instanceIds)
                    {
                        if (mAllObjectDic.TryGetValue(instanceId, out CacheObejct cacheObject) &&
                            cacheObject != null && !cacheObject.isInPool)
                        {
                            result.status = ModuleClearStatus.InUse;
                            result.message = $"资源模块仍有活动 GameObject，未执行任何清理：{bundleModule}";
                            return result;
                        }
                    }

                    int activeAssetHandleCount = 0;
                    foreach (uint crc in loadedAssetCrcs)
                        activeAssetHandleCount += GetActiveAssetHandleCount(crc);
                    if (activeAssetHandleCount > 0)
                    {
                        result.status = ModuleClearStatus.InUse;
                        result.message =
                            $"资源模块仍有 {activeAssetHandleCount} 个活动 AssetHandle，未执行任何清理：{bundleModule}";
                        return result;
                    }
                }

                foreach (int instanceId in instanceIds)
                {
                    if (!mAllObjectDic.TryGetValue(instanceId, out CacheObejct cacheObject) || cacheObject == null) continue;
                    if (mode == ModuleClearMode.PooledOnly && !cacheObject.isInPool) continue;
                    if (DestroyTrackedInstance(cacheObject)) result.destroyedObjectCount++;
                }

                foreach (uint crc in loadedAssetCrcs)
                {
                    if (EvictLoadedAssetCore(
                            crc,
                            mode == ModuleClearMode.ForceTrackedObjects,
                            out bool removedAtlasCache))
                        result.releasedAssetCount++;
                    if (removedAtlasCache)
                        result.releasedAtlasCount++;
                }

                if (state != null)
                {
                    foreach (string atlasPath in new List<string>(state.atlasPaths))
                    {
                        if (RemoveAtlasCache(atlasPath, bundleModule)) result.releasedAtlasCount++;
                    }
                    foreach (long taskId in new List<long>(state.asyncTaskIds))
                    {
                        mAsyncLoadingTaskList.Remove(taskId);
                        mLoadObjectCallBackDic.Remove(taskId);
                    }
                    mModuleResourceStateDic.Remove(bundleModule);
                }

                //00 只有缓存、实例、图集和任务全部清理成功后才释放当前 Business 的 Shared 租约。
                AssetBundleManager.Instance.DeactivateModuleAfterClear(bundleModule);
                await Resources.UnloadUnusedAssets();
                result.status = ModuleClearStatus.Success;
                result.message = $"资源模块清理完成：{bundleModule}";
                return result;
            }
            catch (Exception exception)
            {
                result.status = ModuleClearStatus.Failed;
                result.message = $"资源模块清理失败：{bundleModule}";
                result.exception = exception;
                Debug.LogError($"{result.message} Exception:{exception}");
                return result;
            }
            finally
            {
                AssetBundleManager.Instance.EndModuleClear(bundleModule);
            }
        }

        /// <summary>
        /// 00 先按现有规则清理模块资源，再显式移除模块配置和依赖图。
        /// </summary>
        public async UniTask<ModuleUnloadResult> UnloadModuleAssetsAsync(
            string bundleModule,
            ModuleClearMode mode = ModuleClearMode.PooledOnly)
        {
            ModuleUnloadResult result = new ModuleUnloadResult
            {
                bundleModule = bundleModule,
                status = ModuleUnloadStatus.Failed
            };
            //00 卸载不暗改旧清理 API；它显式复用清理结果并在失败时保留配置。
            ModuleClearResult clearResult = await ClearModuleAssetsAsync(bundleModule, mode);
            result.clearResult = clearResult;
            if (clearResult.status != ModuleClearStatus.Success)
            {
                result.status = ConvertClearStatusToUnloadStatus(clearResult.status);
                result.message = $"资源模块清理未完成，配置未卸载：{clearResult.message}";
                result.exception = clearResult.exception;
                return result;
            }
            // 清理成功后再进入 AssetBundleManager 的独立配置卸载事务；竞态会被其 Busy 门禁安全拒绝。
            ModuleUnloadResult unloadResult =
                await AssetBundleManager.Instance.UnloadAssetModuleAsync(bundleModule);
            //00 对外结果同时保留已完成的清理统计，便于调用方审计销毁和释放数量。
            unloadResult.clearResult = clearResult;
            return unloadResult;
        }

        /// <summary>
        /// 00 将旧清理状态映射为新的卸载状态，保留调用方可判断的失败原因。
        /// </summary>
        private static ModuleUnloadStatus ConvertClearStatusToUnloadStatus(ModuleClearStatus clearStatus)
        {
            switch (clearStatus)
            {
                case ModuleClearStatus.InvalidModule:
                    return ModuleUnloadStatus.InvalidModule;
                case ModuleClearStatus.NotInitialized:
                    return ModuleUnloadStatus.NotInitialized;
                case ModuleClearStatus.Busy:
                    return ModuleUnloadStatus.Busy;
                // 清理阶段的活动依赖者同时也是已初始化依赖者，映射为专用状态可直接指导调用方先卸载业务模块。
                case ModuleClearStatus.DependencyInUse:
                    return ModuleUnloadStatus.DependentModuleInitialized;
                case ModuleClearStatus.InUse:
                    return ModuleUnloadStatus.InUse;
                default:
                    return ModuleUnloadStatus.Failed;
            }
        }

        public void Release(AssetsRequest request)
        {
            if (request == null || !request.TryBeginRelease(out GameObject instance))
                return;

            if (instance != null)
                Release(instance);
        }
        /// <summary>
        /// 初始化资源模块
        /// </summary>
        /// <param name="bundleModule">模块类型</param>
        /// <param name="isRemoteAsset">是否为远端按需下载资源模块</param>
        /// <returns></returns>
        public async UniTask<bool> InitAssetModule(string bundleModule, bool isRemoteAsset = false)
        {
            if (!isRemoteAsset)
            {
               return await AssetBundleManager.Instance.InitializeAssetModule(bundleModule);
            }

            return await RemoteAssetSystem.Instance.InitializeRemoteModuleAsync(bundleModule);
        }
        #endregion
    }
}
