using System;

namespace ZM.Asset
{
    /// <summary>
    /// 第 2 期 WebGL 热更新事务边界。
    /// 浏览器版本指针事务在第 4 期启用，本实现只负责阻止误用 Native 目录切换。
    /// </summary>
    internal sealed class WebGLUnsupportedHotUpdateCommitStrategy : IHotUpdateCommitStrategy
    {
        public HotUpdateCommitContext CreateTransaction(
            string moduleName,
            string finalSnapshotPath,
            string localManifestPath) => throw CreateException();

        public void RecoverInterruptedTransaction(
            string moduleName,
            string finalSnapshotPath,
            string localManifestPath,
            bool forceRollback) => throw CreateException();

        public void PromoteSnapshot(HotUpdateCommitContext context, string manifestJson) => throw CreateException();

        public bool Rollback(HotUpdateCommitContext context, out Exception failure) => throw CreateException();

        public void FinalizeTransaction(HotUpdateCommitContext context) => throw CreateException();

        public void PromoteVerifiedFile(
            string stagingPath,
            string destinationPath,
            string operationId,
            string moduleName,
            string targetName) => throw CreateException();

        public void RecoverVerifiedFile(
            string destinationPath,
            string operationId,
            string moduleName,
            string targetName) => throw CreateException();

        private static PlatformNotSupportedException CreateException()
        {
            return new PlatformNotSupportedException(
                "WebGL 第 2 期尚未启用浏览器版本指针事务，已阻止回退到 Native 目录提交实现。");
        }
    }
}
