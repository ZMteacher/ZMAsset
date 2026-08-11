using System;

namespace ZM.Asset
{
    public enum ShaderVariantPrewarmStatus
    {
        Succeeded = 0,
        AlreadyWarmed = 1,
        EmptyCollection = 2,
        Cancelled = 3,
        TimedOut = 4,
        Busy = 5,
        ModuleInitializationFailed = 6,
        MissingManifest = 7,
        InvalidManifest = 8,
        Failed = 9
    }

    /// <summary>
    /// Controls progressive shader warmup. A default value uses 32 variants per frame and a 30-second timeout.
    /// </summary>
    public readonly struct ShaderVariantPrewarmOptions
    {
        public const int DefaultVariantsPerFrame = 32;
        public const float DefaultTimeoutSeconds = 30f;

        public readonly int VariantsPerFrame;
        public readonly float TimeoutSeconds;

        public ShaderVariantPrewarmOptions(
            int variantsPerFrame = DefaultVariantsPerFrame,
            float timeoutSeconds = DefaultTimeoutSeconds)
        {
            if (variantsPerFrame <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(variantsPerFrame),
                    variantsPerFrame,
                    "每帧预热变体数量必须大于 0。");
            if (timeoutSeconds <= 0f || float.IsNaN(timeoutSeconds) || float.IsInfinity(timeoutSeconds))
                throw new ArgumentOutOfRangeException(
                    nameof(timeoutSeconds),
                    timeoutSeconds,
                    "Shader 预热超时必须是大于 0 的有限秒数。");

            VariantsPerFrame = variantsPerFrame;
            TimeoutSeconds = timeoutSeconds;
        }

        internal ShaderVariantPrewarmOptions Normalize()
        {
            return VariantsPerFrame == 0 && TimeoutSeconds == 0f
                ? new ShaderVariantPrewarmOptions(DefaultVariantsPerFrame, DefaultTimeoutSeconds)
                : this;
        }
    }

    public readonly struct ShaderVariantPrewarmProgress
    {
        public readonly string ModuleName;
        public readonly string ProfileName;
        public readonly int WarmedVariantCount;
        public readonly int TotalVariantCount;
        public readonly float NormalizedProgress;
        public readonly double ElapsedSeconds;

        internal ShaderVariantPrewarmProgress(
            string moduleName,
            string profileName,
            int warmedVariantCount,
            int totalVariantCount,
            double elapsedSeconds)
        {
            ModuleName = moduleName ?? string.Empty;
            ProfileName = profileName ?? string.Empty;
            WarmedVariantCount = Math.Max(0, warmedVariantCount);
            TotalVariantCount = Math.Max(0, totalVariantCount);
            NormalizedProgress = TotalVariantCount <= 0
                ? 1f
                : Math.Min(1f, (float)WarmedVariantCount / TotalVariantCount);
            ElapsedSeconds = Math.Max(0d, elapsedSeconds);
        }
    }

    public sealed class ShaderVariantPrewarmResult
    {
        public ShaderVariantPrewarmStatus Status { get; }
        public string ModuleName { get; }
        public string ProfileName { get; }
        public int WarmedVariantCount { get; }
        public int TotalVariantCount { get; }
        public double ElapsedSeconds { get; }
        public string Message { get; }

        public bool IsSuccess =>
            Status == ShaderVariantPrewarmStatus.Succeeded ||
            Status == ShaderVariantPrewarmStatus.AlreadyWarmed ||
            Status == ShaderVariantPrewarmStatus.EmptyCollection;

        internal ShaderVariantPrewarmResult(
            ShaderVariantPrewarmStatus status,
            string moduleName,
            string profileName,
            int warmedVariantCount,
            int totalVariantCount,
            double elapsedSeconds,
            string message)
        {
            Status = status;
            ModuleName = moduleName ?? string.Empty;
            ProfileName = profileName ?? string.Empty;
            WarmedVariantCount = Math.Max(0, warmedVariantCount);
            TotalVariantCount = Math.Max(0, totalVariantCount);
            ElapsedSeconds = Math.Max(0d, elapsedSeconds);
            Message = message ?? string.Empty;
        }
    }
}
