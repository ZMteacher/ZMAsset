using System.Collections.Generic;

namespace ZM.Asset
{
    /// <summary>
    /// 远端资源预下载结果。预下载是闲时任务：单个文件失败不中断其余文件，最终结果聚合反馈。
    /// </summary>
    public sealed class RemotePreDownloadResult
    {
        /// <summary>
        /// 预下载的目标模块。
        /// </summary>
        public string ModuleName { get; }

        /// <summary>
        /// 模块初始化与参数校验是否通过；为 false 时 TotalCount 等统计无意义，IsSuccess 必为 false。
        /// </summary>
        public bool ModuleReady { get; }

        /// <summary>
        /// 发起时检测到的待下载文件总数；0 表示模块已全部就绪（ModuleReady 为 false 时本字段无意义）。
        /// </summary>
        public int TotalCount { get; }

        /// <summary>
        /// 就绪成功的文件数（含预下载期间被按需加载并发就绪的文件）。
        /// </summary>
        public int SuccessCount { get; }

        /// <summary>
        /// 下载失败的文件名列表；失败文件仍留在缺失列表中，后续按需加载会自动重试。
        /// </summary>
        public IReadOnlyList<string> FailedFileNames { get; }

        /// <summary>
        /// 全部文件就绪时为 true。
        /// </summary>
        public bool IsSuccess => ModuleReady && FailedFileNames.Count == 0;

        internal RemotePreDownloadResult(string moduleName, bool moduleReady, int totalCount, int successCount, List<string> failedFileNames)
        {
            ModuleName = moduleName;
            ModuleReady = moduleReady;
            TotalCount = totalCount;
            SuccessCount = successCount;
            FailedFileNames = failedFileNames ?? new List<string>();
        }
    }
}
