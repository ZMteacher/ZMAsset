using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

namespace ZM.Asset
{
    [Serializable]
    internal sealed class WebGLActiveSnapshot
    {
        public string transactionId;
        public Dictionary<string, string> modules = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Immutable-at-publication in-memory view of the active WebGL version pointer.
    /// Readers either observe the old complete snapshot or the new complete snapshot.
    /// </summary>
    internal static class WebGLActiveAssetRegistry
    {
        private static readonly object sLock = new object();
        private static Dictionary<string, HotAssetsManifest> sManifests =
            new Dictionary<string, HotAssetsManifest>(StringComparer.Ordinal);
        private static string sTransactionId;

        internal static string ActiveTransactionId
        {
            get { lock (sLock) return sTransactionId; }
        }

        internal static void Publish(WebGLActiveSnapshot snapshot, IDictionary<string, HotAssetsManifest> manifests)
        {
            Dictionary<string, HotAssetsManifest> next =
                new Dictionary<string, HotAssetsManifest>(StringComparer.Ordinal);
            if (snapshot?.modules != null)
            {
                foreach (KeyValuePair<string, string> entry in snapshot.modules)
                {
                    if (!manifests.TryGetValue(entry.Key, out HotAssetsManifest manifest) || manifest == null)
                        throw new InvalidOperationException($"活动快照缺少模块 Manifest：{entry.Key}/{entry.Value}");
                    next.Add(entry.Key, manifest);
                }
            }

            lock (sLock)
            {
                sManifests = next;
                sTransactionId = snapshot?.transactionId;
            }
        }

        internal static bool TryResolve(string moduleName, string bundleName, out AssetBundleLocation location)
        {
            lock (sLock)
            {
                if (sManifests.TryGetValue(moduleName, out HotAssetsManifest manifest) &&
                    TryFindFile(manifest, bundleName, out HotFileInfo file))
                {
                    location = new AssetBundleLocation(
                        moduleName,
                        bundleName,
                        AssetBundleSourceKind.HotUpdate,
                        WebGLAssetDownloadService.CombineUrl(manifest.downLoadURL, bundleName),
                        file.bundleHash,
                        file.crc,
                        false);
                    return true;
                }
            }

            location = default;
            return false;
        }

        internal static bool TryGetManifest(string moduleName, out HotAssetsManifest manifest)
        {
            lock (sLock)
                return sManifests.TryGetValue(moduleName, out manifest);
        }

        internal static void ResetForTests()
        {
            lock (sLock)
            {
                sManifests = new Dictionary<string, HotAssetsManifest>(StringComparer.Ordinal);
                sTransactionId = null;
            }
        }

        private static bool TryFindFile(HotAssetsManifest manifest, string bundleName, out HotFileInfo file)
        {
            if (manifest?.hotAssetsPatchList != null)
            {
                for (int patchIndex = manifest.hotAssetsPatchList.Count - 1; patchIndex >= 0; patchIndex--)
                {
                    List<HotFileInfo> files = manifest.hotAssetsPatchList[patchIndex]?.hotAssetsList;
                    if (files == null)
                        continue;
                    for (int index = files.Count - 1; index >= 0; index--)
                    {
                        if (string.Equals(files[index]?.abName, bundleName, StringComparison.Ordinal))
                        {
                            file = files[index];
                            return true;
                        }
                    }
                }
            }
            file = null;
            return false;
        }
    }
}
