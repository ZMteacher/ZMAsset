using System;
using Cysharp.Threading.Tasks;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 第 2 期 WebGL 元数据边界。
    /// IndexedDB 元数据在事务阶段启用，在此之前任何调用都明确失败，禁止访问浏览器中的 Native 文件 API。
    /// </summary>
    internal sealed class WebGLUnsupportedMetadataStore : IAssetMetadataStore
    {
        public bool Exists(string path) => throw CreateException();

        public string ReadText(string path) => throw CreateException();

        public UniTask<string> ReadTextAsync(string path) => throw CreateException();

        public void WriteText(string path, string content) => throw CreateException();

        public UniTask WriteTextAsync(string path, string content) => throw CreateException();

        public UniTask<bool> ExistsAsync(string path) => throw CreateException();

        public UniTask WriteTextAtomicallyAsync(string path, string content) => throw CreateException();

        public UniTask<string[]> GetFilesAsync(string directoryPath, string searchPattern) => throw CreateException();

        public UniTask DeleteIfExistsAsync(string path) => throw CreateException();

        public void WriteTextAtomically(string path, string content) => throw CreateException();

        public void RecoverAtomicWrite(string path) => throw CreateException();

        public string[] GetFiles(string directoryPath, string searchPattern) => throw CreateException();

        public void DeleteIfExists(string path) => throw CreateException();

        private static PlatformNotSupportedException CreateException()
        {
            return new PlatformNotSupportedException(
                "WebGL 第 2 期尚未启用浏览器元数据存储，已阻止回退到 Native 文件实现。");
        }
    }
}
