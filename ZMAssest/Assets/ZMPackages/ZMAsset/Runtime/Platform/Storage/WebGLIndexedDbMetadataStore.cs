using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;

namespace ZM.ZMAsset
{
    /// <summary>
    /// Stores small WebGL metadata records in the project-owned IndexedDB database.
    /// Bundle payloads remain owned by Unity's browser cache.
    /// </summary>
    internal sealed class WebGLIndexedDbMetadataStore : IAssetMetadataStore
    {
        private const string DatabaseName = "ZMAsset.Metadata.v1";
        private static readonly Dictionary<string, string> sEditorRecords =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void ResetEditorStateForTests()
        {
#if !UNITY_WEBGL || UNITY_EDITOR
            sEditorRecords.Clear();
#endif
        }

        public bool Exists(string path) => throw CreateSyncException();

        public string ReadText(string path) => throw CreateSyncException();

        public void WriteText(string path, string content) => throw CreateSyncException();

        public void WriteTextAtomically(string path, string content) => throw CreateSyncException();

        public void RecoverAtomicWrite(string path) => throw CreateSyncException();

        public string[] GetFiles(string directoryPath, string searchPattern) => throw CreateSyncException();

        public void DeleteIfExists(string path) => throw CreateSyncException();

        public async UniTask<bool> ExistsAsync(string path)
        {
            return await WebGLIndexedDbBridge.ExistsAsync(DatabaseName, NormalizeKey(path));
        }

        public async UniTask<string> ReadTextAsync(string path)
        {
            string value = await WebGLIndexedDbBridge.ReadAsync(DatabaseName, NormalizeKey(path));
            if (value == null)
                throw new InvalidOperationException($"WebGL 元数据不存在：{path}");
            return value;
        }

        public async UniTask WriteTextAsync(string path, string content)
        {
            await WebGLIndexedDbBridge.WriteAsync(DatabaseName, NormalizeKey(path), content ?? string.Empty);
        }

        public async UniTask WriteTextAtomicallyAsync(string path, string content)
        {
            await WriteTextAsync(path, content);
        }

        public async UniTask<string[]> GetFilesAsync(string directoryPath, string searchPattern)
        {
            string prefix = NormalizeKey(directoryPath).TrimEnd('/') + "/";
            string[] keys = await WebGLIndexedDbBridge.ListKeysAsync(DatabaseName, prefix);
            if (string.IsNullOrEmpty(searchPattern) || searchPattern == "*")
                return keys;

            string suffix = searchPattern.StartsWith("*", StringComparison.Ordinal)
                ? searchPattern.Substring(1)
                : null;
            List<string> matches = new List<string>();
            foreach (string key in keys)
            {
                string fileName = key.Substring(key.LastIndexOf('/') + 1);
                if (suffix != null
                    ? fileName.EndsWith(suffix, StringComparison.Ordinal)
                    : string.Equals(fileName, searchPattern, StringComparison.Ordinal))
                    matches.Add(key);
            }
            return matches.ToArray();
        }

        public async UniTask DeleteIfExistsAsync(string path)
        {
            await WebGLIndexedDbBridge.DeleteAsync(DatabaseName, NormalizeKey(path));
        }

        private static string NormalizeKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("WebGL 元数据键不能为空。", nameof(path));
            return path.Replace('\\', '/');
        }

        private static PlatformNotSupportedException CreateSyncException()
        {
            return new PlatformNotSupportedException("WebGL IndexedDB 只能通过异步 API 访问，禁止同步阻塞浏览器主线程。");
        }

        /// <summary>
        /// The callback bridge owns request completion and keeps all JS interop in one place.
        /// Non-WebGL players use an in-memory implementation for deterministic contract tests.
        /// </summary>
        private static class WebGLIndexedDbBridge
        {
            private sealed class PendingRequest
            {
                internal UniTaskCompletionSource<string> CompletionSource;
            }

            private static readonly Dictionary<int, PendingRequest> sPending = new Dictionary<int, PendingRequest>();
            private static int sNextRequestId;
            private delegate void SuccessCallback(int requestId, string value);
            private delegate void ErrorCallback(int requestId, string error);

#if UNITY_WEBGL && !UNITY_EDITOR
            private static readonly SuccessCallback sSuccessCallback = HandleSuccess;
            private static readonly ErrorCallback sErrorCallback = HandleError;
            [DllImport("__Internal")]
            private static extern void ZMAssetIndexedDbGet(string databaseName, string key, int requestId, SuccessCallback success, ErrorCallback error);
            [DllImport("__Internal")]
            private static extern void ZMAssetIndexedDbPut(string databaseName, string key, string value, int requestId, SuccessCallback success, ErrorCallback error);
            [DllImport("__Internal")]
            private static extern void ZMAssetIndexedDbDelete(string databaseName, string key, int requestId, SuccessCallback success, ErrorCallback error);
            [DllImport("__Internal")]
            private static extern void ZMAssetIndexedDbList(string databaseName, string prefix, int requestId, SuccessCallback success, ErrorCallback error);
#endif

            internal static async UniTask<bool> ExistsAsync(string databaseName, string key)
            {
                return await ReadAsync(databaseName, key) != null;
            }

            internal static UniTask<string> ReadAsync(string databaseName, string key)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return Begin((id, success, error) => ZMAssetIndexedDbGet(databaseName, key, id, success, error));
#else
                sEditorRecords.TryGetValue(key, out string value);
                return UniTask.FromResult(value);
#endif
            }

            internal static UniTask WriteAsync(string databaseName, string key, string value)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return Begin((id, success, error) => ZMAssetIndexedDbPut(databaseName, key, value, id, success, error));
#else
                sEditorRecords[key] = value;
                return UniTask.CompletedTask;
#endif
            }

            internal static UniTask DeleteAsync(string databaseName, string key)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return Begin((id, success, error) => ZMAssetIndexedDbDelete(databaseName, key, id, success, error));
#else
                sEditorRecords.Remove(key);
                return UniTask.CompletedTask;
#endif
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            internal static async UniTask<string[]> ListKeysAsync(string databaseName, string prefix)
            {
                string json = await Begin((id, success, error) => ZMAssetIndexedDbList(databaseName, prefix, id, success, error));
                return JsonConvert.DeserializeObject<string[]>(json) ?? Array.Empty<string>();
            }
#else
            internal static UniTask<string[]> ListKeysAsync(string databaseName, string prefix)
            {
                List<string> keys = new List<string>();
                foreach (string key in sEditorRecords.Keys)
                    if (key.StartsWith(prefix, StringComparison.Ordinal))
                        keys.Add(key);
                return UniTask.FromResult(keys.ToArray());
            }
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
            private static UniTask<string> Begin(Action<int, SuccessCallback, ErrorCallback> start)
            {
                int requestId = unchecked(++sNextRequestId);
                UniTaskCompletionSource<string> completionSource = new UniTaskCompletionSource<string>();
                sPending.Add(requestId, new PendingRequest { CompletionSource = completionSource });
                try
                {
                    start(requestId, sSuccessCallback, sErrorCallback);
                }
                catch (Exception exception)
                {
                    sPending.Remove(requestId);
                    completionSource.TrySetException(exception);
                }
                return completionSource.Task;
            }

            [AOT.MonoPInvokeCallback(typeof(SuccessCallback))]
            private static void HandleSuccess(int requestId, string value)
            {
                if (!sPending.TryGetValue(requestId, out PendingRequest request))
                    return;
                sPending.Remove(requestId);
                request.CompletionSource.TrySetResult(value);
            }

            [AOT.MonoPInvokeCallback(typeof(ErrorCallback))]
            private static void HandleError(int requestId, string error)
            {
                if (!sPending.TryGetValue(requestId, out PendingRequest request))
                    return;
                sPending.Remove(requestId);
                request.CompletionSource.TrySetException(
                    new InvalidOperationException($"WebGL IndexedDB 操作失败：{error}"));
            }
#endif
        }
    }
}
