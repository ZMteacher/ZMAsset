using Cysharp.Threading.Tasks;

namespace ZM.Asset
{
    /// <summary>
    /// 保存 Manifest、活动版本指针和事务日志等小型元数据。
    /// Bundle 二进制的下载和缓存不属于该接口职责。
    /// </summary>
    internal interface IAssetMetadataStore
    {
        bool Exists(string path);

        string ReadText(string path);

        UniTask<string> ReadTextAsync(string path);

        void WriteText(string path, string content);

        UniTask WriteTextAsync(string path, string content);

        UniTask<bool> ExistsAsync(string path);

        UniTask WriteTextAtomicallyAsync(string path, string content);

        UniTask<string[]> GetFilesAsync(string directoryPath, string searchPattern);

        UniTask DeleteIfExistsAsync(string path);

        void WriteTextAtomically(string path, string content);

        void RecoverAtomicWrite(string path);

        string[] GetFiles(string directoryPath, string searchPattern);

        void DeleteIfExists(string path);
    }
}
