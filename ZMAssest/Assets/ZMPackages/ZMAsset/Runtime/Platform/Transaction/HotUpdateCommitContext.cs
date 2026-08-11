namespace ZM.Asset
{
    /// <summary>
    /// 描述一次 Native 热更新目录事务使用的全部可信路径和旧状态。
    /// 路径只能由提交策略基于模块名和框架根目录生成，不能使用服务端输入直接拼接。
    /// </summary>
    internal sealed class HotUpdateCommitContext
    {
        internal string ModuleName { get; set; }

        internal string TransactionId { get; set; }

        internal string FinalSnapshotPath { get; set; }

        internal string StagingSnapshotPath { get; set; }

        internal string BackupSnapshotPath { get; set; }

        internal string LocalManifestPath { get; set; }

        internal string ManifestStagingPath { get; set; }

        internal string ManifestBackupPath { get; set; }

        internal string JournalPath { get; set; }

        internal bool HadFinalSnapshot { get; set; }

        internal bool HadLocalManifest { get; set; }

        internal string CandidateManifestId { get; set; }

        internal string CandidateManifestJson { get; set; }

        internal string PreviousActiveSnapshotJson { get; set; }

        internal bool IsActivePointerCommitted { get; set; }
    }

    /// <summary>
    /// Native 事务关键事件，用于锁定迁移前后的目录切换顺序，不写入普通运行日志。
    /// </summary>
    internal readonly struct HotUpdateCommitTrace
    {
        internal HotUpdateCommitTrace(string operationId, string stage)
        {
            OperationId = operationId;
            Stage = stage;
        }

        internal string OperationId { get; }

        internal string Stage { get; }
    }
}
