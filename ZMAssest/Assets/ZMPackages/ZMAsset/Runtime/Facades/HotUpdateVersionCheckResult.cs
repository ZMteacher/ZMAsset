using System;

namespace ZM.ZMAsset
{
    /// <summary>
    /// 资源版本检查的最终状态。
    /// </summary>
    public enum HotUpdateVersionCheckStatus
    {
        /// <summary>
        /// 无法确认远端版本，调用方必须阻止继续初始化或进入游戏。
        /// 该枚举值必须为零，确保结果结构体默认值属于失败关闭状态。
        /// </summary>
        UnableToConfirm = 0,

        /// <summary>
        /// 已成功取得远端清单，并确认当前资源已经是最新版本。
        /// </summary>
        ConfirmedNoUpdate = 1,

        /// <summary>
        /// 已成功取得远端清单，并确认存在需要下载的资源。
        /// </summary>
        UpdateAvailable = 2
    }

    /// <summary>
    /// 资源版本检查返回的不可变结果。
    /// </summary>
    public readonly struct HotUpdateVersionCheckResult
    {
        /// <summary>
        /// 使用旧版二态参数创建版本检查结果，保留已有调用方的兼容性。
        /// </summary>
        public HotUpdateVersionCheckResult(bool requiresUpdate, float downloadSizeMb)
            : this(
                requiresUpdate
                    ? HotUpdateVersionCheckStatus.UpdateAvailable
                    : HotUpdateVersionCheckStatus.ConfirmedNoUpdate,
                downloadSizeMb,
                null,
                null)
        {
        }

        /// <summary>
        /// 创建带有明确状态和错误上下文的版本检查结果。
        /// </summary>
        public HotUpdateVersionCheckResult(
            HotUpdateVersionCheckStatus status,
            float downloadSizeMb,
            string errorMessage = null,
            Exception exception = null)
        {
            if (!Enum.IsDefined(typeof(HotUpdateVersionCheckStatus), status))
                throw new ArgumentOutOfRangeException(nameof(status), status, "未知的资源版本检查状态。");

            if (float.IsNaN(downloadSizeMb) || float.IsInfinity(downloadSizeMb) || downloadSizeMb < 0f)
                throw new ArgumentOutOfRangeException(nameof(downloadSizeMb), downloadSizeMb, "下载大小不能为负数或非有限值。");

            Status = status;
            // 无法确认时不允许携带看似可用的下载大小，避免调用方误以为结果可信。
            DownloadSizeMb = status == HotUpdateVersionCheckStatus.UnableToConfirm ? 0f : downloadSizeMb;
            ErrorMessage = errorMessage;
            Exception = exception;
        }

        /// <summary>
        /// 创建无法确认远端版本的结果。
        /// </summary>
        public static HotUpdateVersionCheckResult CreateUnableToConfirm(string errorMessage, Exception exception = null)
        {
            return new HotUpdateVersionCheckResult(
                HotUpdateVersionCheckStatus.UnableToConfirm,
                0f,
                errorMessage,
                exception);
        }

        /// <summary>
        /// 版本检查最终状态。
        /// </summary>
        public HotUpdateVersionCheckStatus Status { get; }

        /// <summary>
        /// 是否已经成功完成远端版本确认。
        /// </summary>
        public bool IsConfirmed =>
            Status == HotUpdateVersionCheckStatus.ConfirmedNoUpdate ||
            Status == HotUpdateVersionCheckStatus.UpdateAvailable;

        /// <summary>
        /// 是否可以在不下载补丁的情况下继续初始化资源模块。
        /// </summary>
        public bool CanProceedWithoutUpdate => Status == HotUpdateVersionCheckStatus.ConfirmedNoUpdate;

        /// <summary>
        /// 是否存在需要下载的远端资源。
        /// 只有 Status 为 UpdateAvailable 时该属性才表示有效的“有更新”结论。
        /// </summary>
        [Obsolete("请改用 Status 或 CanProceedWithoutUpdate；RequiresUpdate=false 不能表示版本检查成功。", false)]
        public bool RequiresUpdate => Status == HotUpdateVersionCheckStatus.UpdateAvailable;

        /// <summary>
        /// 本次预计下载大小，单位为 MB。
        /// </summary>
        public float DownloadSizeMb { get; }

        /// <summary>
        /// 无法确认时返回给业务层的可读错误信息。
        /// </summary>
        public string ErrorMessage { get; }

        /// <summary>
        /// 无法确认时保留的原始异常，便于日志和重试策略使用。
        /// </summary>
        public Exception Exception { get; }
    }
}
