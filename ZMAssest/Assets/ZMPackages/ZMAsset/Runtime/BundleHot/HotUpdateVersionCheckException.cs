using System;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 远端热更清单无法确认时使用的异常，携带模块和失败类别，便于上层区分网络失败与业务初始化失败。
    /// </summary>
    public sealed class HotUpdateVersionCheckException : InvalidOperationException
    {
        /// <summary>
        /// 创建一次带有模块上下文的版本检查异常。
        /// </summary>
        public HotUpdateVersionCheckException(
            string bundleModule,
            string failureKind,
            string message,
            Exception innerException = null)
            : base(message, innerException)
        {
            BundleModule = bundleModule;
            FailureKind = failureKind;
        }

        /// <summary>
        /// 发生版本检查失败的资源模块。
        /// </summary>
        public string BundleModule { get; }

        /// <summary>
        /// 失败类别，例如 Network、Http、EmptyResponse 或 InvalidManifest。
        /// </summary>
        public string FailureKind { get; }
    }
}
