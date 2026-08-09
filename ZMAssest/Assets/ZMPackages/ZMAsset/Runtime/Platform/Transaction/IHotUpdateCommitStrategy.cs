using System;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 把已经准备并校验的候选版本切换为业务可见版本。
    /// 配置初始化仍由 HotAssetsModule 负责，因此失败时可以在同一事务边界内回滚磁盘和内存状态。
    /// </summary>
    internal interface IHotUpdateCommitStrategy
    {
        HotUpdateCommitContext CreateTransaction(
            string moduleName,
            string finalSnapshotPath,
            string localManifestPath);

        void RecoverInterruptedTransaction(
            string moduleName,
            string finalSnapshotPath,
            string localManifestPath,
            bool forceRollback);

        void PromoteSnapshot(HotUpdateCommitContext context, string manifestJson);

        bool Rollback(HotUpdateCommitContext context, out Exception failure);

        void FinalizeTransaction(HotUpdateCommitContext context);

        void PromoteVerifiedFile(
            string stagingPath,
            string destinationPath,
            string operationId,
            string moduleName,
            string targetName);

        /// <summary>
        /// 在读取或再次发布单文件前恢复上次被进程中断的切换现场。
        /// 正式文件缺失时恢复旧备份；正式文件存在时把残留备份视为已完成提交后的清理材料。
        /// </summary>
        void RecoverVerifiedFile(
            string destinationPath,
            string operationId,
            string moduleName,
            string targetName);
    }
}
