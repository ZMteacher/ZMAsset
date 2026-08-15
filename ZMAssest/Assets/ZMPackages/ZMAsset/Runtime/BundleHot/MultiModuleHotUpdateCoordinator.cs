using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace ZM.Asset
{
    /// <summary>
    /// 显式多模块热更新协调器：并行准备、串行提交、串行初始化、失败逆序回滚。
    /// </summary>
    internal sealed class MultiModuleHotUpdateCoordinator
    {
        [Serializable]
        private sealed class GroupTransactionJournal
        {
            public string transactionId;
            public string state;
            public List<string> orderedModules;
        }

        private readonly HotAssetsManager mManager;
        private readonly HotDownloadScheduler mScheduler;
        private readonly IAssetMetadataStore mMetadataStore;
        private readonly IHotUpdateCommitStrategy mCommitStrategy;

        /// <summary>
        /// 创建协调器；manager 负责主线程更新，scheduler 负责跨模块共享下载预算，metadataStore 负责平台对应的事务日志。
        /// </summary>
        public MultiModuleHotUpdateCoordinator(
            HotAssetsManager manager,
            HotDownloadScheduler scheduler,
            IAssetMetadataStore metadataStore,
            IHotUpdateCommitStrategy commitStrategy)
        {
            mManager = manager ?? throw new ArgumentNullException(nameof(manager));
            mScheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            // 使用管理器创建时已经选定的同一平台服务，避免协调器再次读取全局工厂后发生后端混用。
            mMetadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            mCommitStrategy = commitStrategy ?? throw new ArgumentNullException(nameof(commitStrategy));
        }

        /// <summary>
        /// 执行完整组事务：准备下载、校验、原子切换、初始化、提交或回滚。
        /// </summary>
        /// <returns>描述整组结果的对象；失败不会把半成品当作成功返回。</returns>
        public async UniTask<HotUpdateTransactionResult> ExecuteAsync(HotUpdateTransactionRequest request)
        {
            List<string> orderedModules = ValidateAndCopyModules(request);
            string transactionId = Guid.NewGuid().ToString("N");
            string journalPath = GetJournalPath(transactionId);
            List<HotAssetsModule> modules = orderedModules.Select(mManager.GetOrNewAssetModule).ToList();
            List<string> changedModules = new List<string>();
            List<HotUpdateCommitContext> changedContexts = new List<HotUpdateCommitContext>();
            bool usesWebGLPointer = mCommitStrategy is WebGLVersionPointerCommitStrategy;
            string failedModule = null;

            // 多模块原子事务要求组内模块尚未进入使用期，否则跨模块依赖的内存配置无法在失败时整体回退。
            // 单模块事务仍保留原有安全重载能力；多模块业务应在进入大厅或游戏前完成事务。
            if (modules.Count > 1)
            {
                List<string> initializedModules = modules
                    .Where(module => AssetBundleManager.Instance.IsAssetModuleInitialized(module.CurBundleModuleName))
                    .Select(module => module.CurBundleModuleName)
                    .ToList();
                if (initializedModules.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"多模块热更新必须在模块初始化前执行，当前已初始化：{string.Join("、", initializedModules)}。" +
                        "请先卸载这些模块，或在进入大厅/游戏前调用事务 API。");
                }
            }

            GroupTransactionJournal journal = new GroupTransactionJournal
            {
                transactionId = transactionId,
                state = "Preparing",
                orderedModules = orderedModules
            };

            try
            {
                // 先落组日志，再允许任一模块创建 staging；进程中断时启动恢复知道本次事务属于哪些模块。
                await WriteJournalAsync(journalPath, journal);
                int batchSize = Math.Max(1, mScheduler.ModuleConcurrency);
                for (int offset = 0; offset < modules.Count; offset += batchSize)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    List<HotAssetsModule> batch = modules.Skip(offset).Take(batchSize).ToList();
                    int pendingCount = batch.Count;
                    Exception prepareException = null;

                    // 先登记完整批次，再启动任一下载器；第一次分配时即可看到全部模块，避免首模块独占全部线程。
                    foreach (HotAssetsModule module in batch)
                        mManager.ActivateCoordinatedModule(module);

                    foreach (HotAssetsModule module in batch)
                    {
                        try
                        {
                        // 此处只准备 staging，不切换正式目录；回调返回后 pendingCount 才减少。
                        module.PrepareForCoordinatedTransaction(
                                mManager.RefreshCoordinatedDownloadAllocation,
                                (preparedModule, hasChanges) =>
                                {
                                    if (hasChanges)
                                        changedModules.Add(preparedModule.CurBundleModuleName);
                                    mManager.DeactivateCoordinatedModule(preparedModule);
                                    pendingCount--;
                                },
                                (failed, file, exception) =>
                                {
                                    failedModule = failed.CurBundleModuleName;
                                    prepareException = exception ?? new IOException(
                                        $"模块 {failedModule} 下载失败，文件：{file?.abName ?? "未知文件"}");
                                    mManager.DeactivateCoordinatedModule(failed);
                                    pendingCount--;
                                },
                                request.CheckAssetsVersion);
                        }
                        catch (Exception exception)
                        {
                            failedModule = module.CurBundleModuleName;
                            prepareException = exception;
                            mManager.DeactivateCoordinatedModule(module);
                            pendingCount--;
                        }
                    }

                    // 批次准备超时保护：防止异常/回调断裂导致准备阶段永久挂起
                    float prepareTimeout = 300f;
                    float prepareElapsed = 0f;
                    while (pendingCount > 0 && prepareException == null)
                    {
                        request.CancellationToken.ThrowIfCancellationRequested();
                        await UniTask.Delay(500, cancellationToken: request.CancellationToken);
                        prepareElapsed += 0.5f;
                        if (prepareElapsed >= prepareTimeout)
                        {
                            prepareException = new TimeoutException(
                                $"批次准备超时（{prepareTimeout}s），{pendingCount} 个模块未完成回调。" +
                                $"可能原因：版本检查异常被吞、下载回调未触发、或网络持续不可达。");
                            break;
                        }
                    }
                    if (prepareException != null)
                        throw new InvalidOperationException($"模块 {failedModule} 准备热更新快照失败。", prepareException);
                }

                journal.state = "Committing";
                await WriteJournalAsync(journalPath, journal);
                foreach (HotAssetsModule module in modules)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    failedModule = module.CurBundleModuleName;
                    // 所有模块准备完成后才进入提交阶段，避免部分模块提前对业务可见。
                    await module.PrepareCoordinatedCommitAsync();
                }


                changedContexts = modules
                    .Select(module => module.TransactionContext)
                    .Where(context => context != null && !string.IsNullOrWhiteSpace(context.CandidateManifestId))
                    .ToList();
                if (usesWebGLPointer && changedContexts.Count > 0)
                {
                    await ((WebGLVersionPointerCommitStrategy)mCommitStrategy).CommitGroupAsync(
                        changedContexts,
                        transactionId,
                        orderedModules);
                }

                journal.state = "Initializing";
                await WriteJournalAsync(journalPath, journal);
                foreach (HotAssetsModule module in modules)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    failedModule = module.CurBundleModuleName;
                    // 磁盘切换完成后才初始化内存配置，确保配置与正式目录来自同一版本。
                    if (!await module.InitializeCoordinatedTransaction())
                        throw new InvalidOperationException($"模块 {failedModule} 配置初始化失败。");
                }

                // 先删除组日志声明整组提交成功，再清理各模块备份；若进程在清理中退出，模块日志会向前完成新版本。
                if (usesWebGLPointer)
                    await ((WebGLVersionPointerCommitStrategy)mCommitStrategy).FinalizeGroupAsync(transactionId);
                await DeleteJournalAsync(journalPath);
                foreach (HotAssetsModule module in modules)
                    await module.FinalizeCoordinatedTransactionAsync(usesWebGLPointer);

                return CreateResult(true, false, transactionId, orderedModules, changedModules, null, "多模块热更新事务已完成。", null);
            }
            catch (Exception exception)
            {
                bool isCancelled = exception is OperationCanceledException;
                Debug.LogError($"多模块热更新事务失败，事务：{transactionId}，模块：{failedModule ?? "准备阶段"}，异常：{exception}");
                Exception rollbackFailure = null;

                if (usesWebGLPointer && changedContexts.Count > 0)
                {
                    try
                    {
                        await ((WebGLVersionPointerCommitStrategy)mCommitStrategy).RollbackGroupAsync(
                            changedContexts,
                            transactionId);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackFailure = rollbackException;
                    }
                }

                // 逆序回滚与显式提交顺序相反，先撤销消费者，再撤销它依赖的 Shared。
                for (int index = modules.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        mManager.DeactivateCoordinatedModule(modules[index]);
                        await modules[index].RollbackCoordinatedTransaction(
                            isCancelled,
                            isCancelled ? "热更新事务已取消。" : exception.Message);
                    }
                    catch (Exception rollbackException)
                    {
                        Debug.LogError($"模块 {modules[index].CurBundleModuleName} 事务回滚失败：{rollbackException}");
                        rollbackFailure = rollbackFailure == null
                            ? rollbackException
                            : new AggregateException(rollbackFailure, rollbackException);
                    }
                }

                if (rollbackFailure == null)
                {
                    try
                    {
                        await DeleteJournalAsync(journalPath);
                    }
                    catch (Exception journalException)
                    {
                        rollbackFailure = journalException;
                    }
                }

                if (rollbackFailure != null)
                {
                    // 回滚不完整时保留组日志，并锁住当前管理器，等待下次启动从同一现场继续恢复。
                    Exception combinedException = new AggregateException(exception, rollbackFailure);
                    mManager.MarkTransactionRecoveryFailure(combinedException);
                    return CreateResult(
                        false,
                        isCancelled,
                        transactionId,
                        orderedModules,
                        changedModules,
                        failedModule,
                        "多模块热更新失败，且回滚未完整完成。已保留事务日志并阻止后续热更新，请重启应用继续恢复。",
                        combinedException);
                }

                string message = isCancelled ? "多模块热更新事务已取消并回滚。" : "多模块热更新事务失败并已回滚。";
                return CreateResult(false, isCancelled, transactionId, orderedModules, changedModules, failedModule, message, exception);
            }
        }

        /// <summary>
        /// 在任何新热更新开始前恢复遗留组事务；组日志存在即整组逆序回滚，不猜测提交进度。
        /// </summary>
        public async UniTask RecoverInterruptedTransactionsAsync()
        {
            string journalDirectory = GetJournalDirectory();
            if (mCommitStrategy is WebGLVersionPointerCommitStrategy webGLStrategy)
            {
                await webGLStrategy.RecoverAllAsync();
                foreach (string journalPath in await mMetadataStore.GetFilesAsync(journalDirectory, "*.group.transaction"))
                    await DeleteJournalAsync(journalPath);
                return;
            }
            // 写日志期间退出时优先把完整临时日志提升为正式日志，再执行同一条整组回滚流程。
            foreach (string writingPath in mMetadataStore.GetFiles(journalDirectory, "*.group.transaction.writing"))
            {
                string journalPath = writingPath.Substring(0, writingPath.Length - ".writing".Length);
                mMetadataStore.RecoverAtomicWrite(journalPath);
            }

            foreach (string journalPath in mMetadataStore.GetFiles(journalDirectory, "*.group.transaction"))
            {
                GroupTransactionJournal journal =
                    JsonConvert.DeserializeObject<GroupTransactionJournal>(mMetadataStore.ReadText(journalPath));
                if (journal?.orderedModules == null || journal.orderedModules.Count == 0)
                    throw new InvalidDataException($"多模块热更新事务日志损坏：{journalPath}");

                for (int index = journal.orderedModules.Count - 1; index >= 0; index--)
                    mManager.GetOrNewAssetModule(journal.orderedModules[index]).RollbackInterruptedGroupTransaction();
                await DeleteJournalAsync(journalPath);
                Debug.LogWarning($"已回滚上次中断的多模块热更新事务：{journal.transactionId}");
            }
        }

        private static List<string> ValidateAndCopyModules(HotUpdateTransactionRequest request)
        {
            if (request?.OrderedModules == null || request.OrderedModules.Count == 0)
                throw new ArgumentException("多模块热更新至少需要一个模块。", nameof(request));

            List<string> modules = new List<string>();
            HashSet<string> uniqueModules = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawModule in request.OrderedModules)
            {
                string module = rawModule?.Trim();
                if (string.IsNullOrEmpty(module))
                    throw new ArgumentException("热更新模块名称不能为空。", nameof(request));
                if (module.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    module.Contains(Path.DirectorySeparatorChar.ToString()) ||
                    module.Contains(Path.AltDirectorySeparatorChar.ToString()))
                    throw new ArgumentException($"热更新模块名称非法：{module}", nameof(request));
                if (!uniqueModules.Add(module))
                    throw new ArgumentException($"热更新模块重复：{module}", nameof(request));
                modules.Add(module);
            }
            return modules;
        }

        private static HotUpdateTransactionResult CreateResult(bool succeeded, bool cancelled, string id,
            IReadOnlyList<string> orderedModules, IReadOnlyList<string> changedModules, string failedModule,
            string message, Exception exception)
        {
            return new HotUpdateTransactionResult
            {
                Succeeded = succeeded,
                IsCancelled = cancelled,
                TransactionId = id,
                OrderedModules = orderedModules,
                ChangedModules = changedModules,
                FailedModule = failedModule,
                Message = message,
                Exception = exception
            };
        }

        private static string GetJournalDirectory()
        {
            return Path.Combine(Application.persistentDataPath, "HotAssets", ".transactions");
        }

        private static string GetJournalPath(string transactionId)
        {
            return Path.Combine(GetJournalDirectory(), $"{transactionId}.group.transaction");
        }

        private UniTask WriteJournalAsync(string path, GroupTransactionJournal journal)
        {
            return mMetadataStore.WriteTextAtomicallyAsync(
                path,
                JsonConvert.SerializeObject(journal, Formatting.Indented));
        }

        private async UniTask DeleteJournalAsync(string path)
        {
            await mMetadataStore.DeleteIfExistsAsync(path);
            await mMetadataStore.DeleteIfExistsAsync(path + ".writing");
        }

    }
}
