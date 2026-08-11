using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;

namespace ZM.Asset
{
    /// <summary>
    /// WebGL transaction strategy. Candidate manifests are durable metadata records and one
    /// active-snapshot record is the only business-visible commit point for every module.
    /// </summary>
    internal sealed class WebGLVersionPointerCommitStrategy : IHotUpdateCommitStrategy
    {
        [Serializable]
        private sealed class TransactionJournal
        {
            public string transactionId;
            public string state;
            public string previousSnapshotJson;
            public List<string> orderedModules;
            public Dictionary<string, string> candidates;
        }

        internal const string ActiveSnapshotKey = "hot-update/active-snapshot.json";
        private const string TransactionDirectory = "hot-update/transactions";
        private readonly IAssetMetadataStore mMetadataStore;
        private readonly WebGLAsyncGate mGate = new WebGLAsyncGate(1);
        private bool mInitialized;

        internal WebGLVersionPointerCommitStrategy(IAssetMetadataStore metadataStore)
        {
            mMetadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        }

        internal async UniTask InitializeAsync()
        {
            await mGate.WaitAsync(default);
            try
            {
                if (mInitialized)
                    return;
                await RecoverAllAsyncCore();
                await PublishStoredSnapshotAsync();
                mInitialized = true;
            }
            finally
            {
                mGate.Release();
            }
        }

        internal UniTask<HotUpdateCommitContext> CreateTransactionAsync(string moduleName)
        {
            ZMAsset.ValidateModuleName(moduleName);
            return UniTask.FromResult(new HotUpdateCommitContext
            {
                ModuleName = moduleName,
                TransactionId = Guid.NewGuid().ToString("N")
            });
        }

        internal async UniTask PrepareCandidateAsync(HotUpdateCommitContext context, string manifestJson)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            HotAssetsManifest manifest = JsonConvert.DeserializeObject<HotAssetsManifest>(manifestJson);
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.manifestId))
                throw new InvalidDataException($"模块 {context.ModuleName} 的候选 Manifest 缺少 manifestId。");
            if (!string.Equals(manifest.targetPlatform, "WebGL", StringComparison.Ordinal))
                throw new InvalidDataException($"模块 {context.ModuleName} 的候选 Manifest 目标平台不是 WebGL。");

            context.CandidateManifestId = manifest.manifestId;
            context.CandidateManifestJson = manifestJson;
            await mMetadataStore.WriteTextAtomicallyAsync(
                GetManifestKey(context.ModuleName, manifest.manifestId), manifestJson);
        }

        internal async UniTask CommitGroupAsync(
            IReadOnlyList<HotUpdateCommitContext> contexts,
            string transactionId,
            IReadOnlyList<string> orderedModules)
        {
            if (contexts == null || contexts.Count == 0)
                return;

            await InitializeAsync();
            await mGate.WaitAsync(default);
            try
            {
                string previousJson = await ReadSnapshotJsonAsync();
                WebGLActiveSnapshot next = DeserializeSnapshot(previousJson);
                next.transactionId = transactionId;
                Dictionary<string, string> candidates = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (HotUpdateCommitContext context in contexts)
                {
                    if (string.IsNullOrWhiteSpace(context.CandidateManifestId))
                        throw new InvalidOperationException($"模块 {context.ModuleName} 尚未准备候选 Manifest。");
                    context.PreviousActiveSnapshotJson = previousJson;
                    candidates[context.ModuleName] = context.CandidateManifestId;
                    next.modules[context.ModuleName] = context.CandidateManifestId;
                }

                string journalKey = GetJournalKey(transactionId);
                TransactionJournal journal = new TransactionJournal
                {
                    transactionId = transactionId,
                    state = "Preparing",
                    previousSnapshotJson = previousJson,
                    orderedModules = new List<string>(orderedModules),
                    candidates = candidates
                };
                await WriteJournalAsync(journalKey, journal);

                // The full module map lives in one IndexedDB value. This write is the sole visibility switch.
                await mMetadataStore.WriteTextAtomicallyAsync(
                    ActiveSnapshotKey,
                    JsonConvert.SerializeObject(next, Formatting.None));
                journal.state = "PointerCommitted";
                await WriteJournalAsync(journalKey, journal);
                await PublishSnapshotAsync(next);
                foreach (HotUpdateCommitContext context in contexts)
                    context.IsActivePointerCommitted = true;
            }
            finally
            {
                mGate.Release();
            }
        }

        internal async UniTask RollbackGroupAsync(IReadOnlyList<HotUpdateCommitContext> contexts, string transactionId)
        {
            if (contexts == null || contexts.Count == 0)
                return;
            await mGate.WaitAsync(default);
            try
            {
                string previousJson = contexts[0].PreviousActiveSnapshotJson;
                WebGLActiveSnapshot active = DeserializeSnapshot(await ReadSnapshotJsonAsync());
                if (string.Equals(active.transactionId, transactionId, StringComparison.Ordinal))
                {
                    await mMetadataStore.WriteTextAtomicallyAsync(ActiveSnapshotKey, previousJson);
                    await PublishSnapshotAsync(DeserializeSnapshot(previousJson));
                }
                await mMetadataStore.DeleteIfExistsAsync(GetJournalKey(transactionId));
                foreach (HotUpdateCommitContext context in contexts)
                    context.IsActivePointerCommitted = false;
            }
            finally
            {
                mGate.Release();
            }
        }

        internal async UniTask FinalizeGroupAsync(string transactionId)
        {
            await mMetadataStore.DeleteIfExistsAsync(GetJournalKey(transactionId));
        }

        internal async UniTask RecoverAllAsync()
        {
            await mGate.WaitAsync(default);
            try
            {
                await RecoverAllAsyncCore();
                await PublishStoredSnapshotAsync();
                mInitialized = true;
            }
            finally
            {
                mGate.Release();
            }
        }

        private async UniTask RecoverAllAsyncCore()
        {
            string[] journalKeys = await mMetadataStore.GetFilesAsync(TransactionDirectory, "*.json");
            foreach (string journalKey in journalKeys)
            {
                TransactionJournal journal;
                try
                {
                    journal = JsonConvert.DeserializeObject<TransactionJournal>(
                        await mMetadataStore.ReadTextAsync(journalKey));
                }
                catch (Exception exception)
                {
                    throw new InvalidDataException($"WebGL 热更新事务日志损坏：{journalKey}", exception);
                }
                if (journal == null || string.IsNullOrWhiteSpace(journal.transactionId))
                    throw new InvalidDataException($"WebGL 热更新事务日志缺少事务标识：{journalKey}");

                WebGLActiveSnapshot active = DeserializeSnapshot(await ReadSnapshotJsonAsync());
                if (string.Equals(active.transactionId, journal.transactionId, StringComparison.Ordinal))
                    await mMetadataStore.WriteTextAtomicallyAsync(ActiveSnapshotKey, journal.previousSnapshotJson);
                await mMetadataStore.DeleteIfExistsAsync(journalKey);
            }
        }

        private async UniTask PublishStoredSnapshotAsync()
        {
            await PublishSnapshotAsync(DeserializeSnapshot(await ReadSnapshotJsonAsync()));
        }

        private async UniTask PublishSnapshotAsync(WebGLActiveSnapshot snapshot)
        {
            Dictionary<string, HotAssetsManifest> manifests =
                new Dictionary<string, HotAssetsManifest>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in snapshot.modules)
            {
                string json = await mMetadataStore.ReadTextAsync(GetManifestKey(entry.Key, entry.Value));
                HotAssetsManifest manifest = JsonConvert.DeserializeObject<HotAssetsManifest>(json);
                if (manifest == null)
                    throw new InvalidDataException($"WebGL 活动 Manifest 无法解析：{entry.Key}/{entry.Value}");
                manifests.Add(entry.Key, manifest);
            }
            WebGLActiveAssetRegistry.Publish(snapshot, manifests);
        }

        private async UniTask<string> ReadSnapshotJsonAsync()
        {
            if (!await mMetadataStore.ExistsAsync(ActiveSnapshotKey))
                return JsonConvert.SerializeObject(new WebGLActiveSnapshot(), Formatting.None);
            return await mMetadataStore.ReadTextAsync(ActiveSnapshotKey);
        }

        private static WebGLActiveSnapshot DeserializeSnapshot(string json)
        {
            WebGLActiveSnapshot snapshot = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonConvert.DeserializeObject<WebGLActiveSnapshot>(json);
            snapshot ??= new WebGLActiveSnapshot();
            snapshot.modules ??= new Dictionary<string, string>(StringComparer.Ordinal);
            return snapshot;
        }

        private UniTask WriteJournalAsync(string key, TransactionJournal journal)
        {
            return mMetadataStore.WriteTextAtomicallyAsync(
                key, JsonConvert.SerializeObject(journal, Formatting.None));
        }

        private static string GetManifestKey(string moduleName, string manifestId)
        {
            if (manifestId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new InvalidDataException($"Manifest 标识包含非法字符：{manifestId}");
            return $"hot-update/manifests/{moduleName}/{manifestId}.json";
        }

        private static string GetJournalKey(string transactionId)
        {
            return $"{TransactionDirectory}/{transactionId}.json";
        }

        public HotUpdateCommitContext CreateTransaction(string moduleName, string finalSnapshotPath, string localManifestPath) => throw SyncNotSupported();
        public void RecoverInterruptedTransaction(string moduleName, string finalSnapshotPath, string localManifestPath, bool forceRollback) => throw SyncNotSupported();
        public void PromoteSnapshot(HotUpdateCommitContext context, string manifestJson) => throw SyncNotSupported();
        public bool Rollback(HotUpdateCommitContext context, out Exception failure) { failure = SyncNotSupported(); return false; }
        public void FinalizeTransaction(HotUpdateCommitContext context) => throw SyncNotSupported();
        public void PromoteVerifiedFile(string stagingPath, string destinationPath, string operationId, string moduleName, string targetName) => throw SyncNotSupported();
        public void RecoverVerifiedFile(string destinationPath, string operationId, string moduleName, string targetName) => throw SyncNotSupported();

        private static PlatformNotSupportedException SyncNotSupported()
        {
            return new PlatformNotSupportedException("WebGL 版本指针事务只能异步执行，禁止同步阻塞浏览器主线程。");
        }
    }
}
