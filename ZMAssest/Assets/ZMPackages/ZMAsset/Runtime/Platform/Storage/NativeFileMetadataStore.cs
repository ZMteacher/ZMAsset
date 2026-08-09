using System.IO;
using Cysharp.Threading.Tasks;

namespace ZM.ZMAsset
{
    /// <summary>
    /// Native 文件元数据存储。
    /// 原子写入继续采用同目录临时文件加 Replace/Move，确保进程中断不会暴露半段 JSON。
    /// </summary>
    internal sealed class NativeFileMetadataStore : IAssetMetadataStore
    {
        public bool Exists(string path)
        {
            return File.Exists(path);
        }

        public string ReadText(string path)
        {
            return File.ReadAllText(path);
        }

        public async UniTask<string> ReadTextAsync(string path)
        {
            return await File.ReadAllTextAsync(path);
        }

        public void WriteText(string path, string content)
        {
            File.WriteAllText(path, content);
        }

        public async UniTask WriteTextAsync(string path, string content)
        {
            await File.WriteAllTextAsync(path, content);
        }

        public UniTask<bool> ExistsAsync(string path)
        {
            return UniTask.FromResult(Exists(path));
        }

        public UniTask WriteTextAtomicallyAsync(string path, string content)
        {
            WriteTextAtomically(path, content);
            return UniTask.CompletedTask;
        }

        public UniTask<string[]> GetFilesAsync(string directoryPath, string searchPattern)
        {
            return UniTask.FromResult(GetFiles(directoryPath, searchPattern));
        }

        public UniTask DeleteIfExistsAsync(string path)
        {
            DeleteIfExists(path);
            return UniTask.CompletedTask;
        }

        public void WriteTextAtomically(string path, string content)
        {
            string directoryPath = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directoryPath))
                Directory.CreateDirectory(directoryPath);

            string writingPath = path + ".writing";
            File.WriteAllText(writingPath, content);
            if (File.Exists(path))
                File.Replace(writingPath, path, null);
            else
                File.Move(writingPath, path);
        }

        public void RecoverAtomicWrite(string path)
        {
            string writingPath = path + ".writing";
            if (!File.Exists(writingPath))
                return;

            if (File.Exists(path))
                File.Delete(writingPath);
            else
                File.Move(writingPath, path);
        }

        public string[] GetFiles(string directoryPath, string searchPattern)
        {
            return Directory.Exists(directoryPath)
                ? Directory.GetFiles(directoryPath, searchPattern)
                : System.Array.Empty<string>();
        }

        public void DeleteIfExists(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
