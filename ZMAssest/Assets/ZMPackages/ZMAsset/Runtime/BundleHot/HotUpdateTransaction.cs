using System;
using System.Collections.Generic;
using System.Threading;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 显式多模块热更新请求。模块顺序由业务层决定，框架不会推断或追加 Shared 消费者。
    /// </summary>
    public sealed class HotUpdateTransactionRequest
    {
        public IReadOnlyList<string> OrderedModules { get; }
        public bool CheckAssetsVersion { get; }
        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// 创建显式事务请求；OrderedModules 的顺序同时决定提交、初始化和逆序回滚顺序。
        /// </summary>
        public HotUpdateTransactionRequest(IReadOnlyList<string> orderedModules, bool checkAssetsVersion = true, CancellationToken cancellationToken = default)
        {
            OrderedModules = orderedModules;
            CheckAssetsVersion = checkAssetsVersion;
            CancellationToken = cancellationToken;
        }
    }

    /// <summary>
    /// 多模块事务结果；失败以结果返回，便于业务层决定重试、退出登录或阻止进入大厅。
    /// </summary>
    public sealed class HotUpdateTransactionResult
    {
        public bool Succeeded { get; internal set; }
        public bool IsCancelled { get; internal set; }
        public string TransactionId { get; internal set; }
        public string FailedModule { get; internal set; }
        public string Message { get; internal set; }
        public Exception Exception { get; internal set; }
        public IReadOnlyList<string> OrderedModules { get; internal set; }
        public IReadOnlyList<string> ChangedModules { get; internal set; }
    }
}
