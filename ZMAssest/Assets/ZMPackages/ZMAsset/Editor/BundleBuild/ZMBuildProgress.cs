// 自定义构建管线展示方案：独立进度状态与模块任务调度层。
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

internal static class ZMBuildProgress
{
    internal enum BuildState { Idle, Running, Succeeded, Failed, Cancelled }

    private sealed class Job
    {
        internal string name;
        internal Action action;
        internal Func<IEnumerator> stagedAction;
        internal IEnumerator routine;
    }

    private static readonly Queue<Job> Jobs = new Queue<Job>();
    private static readonly List<string> Logs = new List<string>();
    private static Job currentJob;
    private static int completedJobs;
    private static int totalJobs;
    //00 统一构建将多个模块包在同一个原子事务 Job 内，因此单独保存真实模块数量，不能复用 Job 数量。
    private static int completedModules;
    private static int totalModules;
    private static bool cancelRequested;
    private static double startedAt;
    private static double finishedAt;
    private static double resumeAt;
    private static bool waitingForStage;

    internal static event Action Changed;
    internal static BuildState State { get; private set; } = BuildState.Idle;
    internal static string Title { get; private set; } = string.Empty;
    internal static string Stage { get; private set; } = string.Empty;
    internal static string Detail { get; private set; } = string.Empty;
    internal static string OutputPath { get; private set; } = string.Empty;
    internal static float OverallProgress { get; private set; }
    internal static float StageProgress { get; private set; }
    internal static bool CanCancel { get; private set; }
    internal static bool Visible => State != BuildState.Idle;
    internal static int RunId { get; private set; }
    internal static IReadOnlyList<string> RecentLogs => Logs;
    internal static int CompletedJobs => completedJobs;
    internal static int TotalJobs => totalJobs;
    //00 面板展示优先使用统一事务登记的真实模块闭包；普通任务没有闭包时保持 Job 即模块的旧语义。
    internal static int DisplayCompletedModules => totalModules > 0 ? completedModules : completedJobs;
    internal static int DisplayTotalModules => totalModules > 0 ? totalModules : totalJobs;
    internal static double ElapsedSeconds
    {
        get
        {
            if (startedAt <= 0) return 0;
            double end = State == BuildState.Running || finishedAt <= 0
                ? EditorApplication.timeSinceStartup
                : finishedAt;
            return Math.Max(0, end - startedAt);
        }
    }

    internal static void Run(string title, IEnumerable<(string name, Action action)> jobs, string outputPath)
    {
        if (State == BuildState.Running) return;
        Jobs.Clear();
        Logs.Clear();
        foreach ((string name, Action action) in jobs)
            Jobs.Enqueue(new Job { name = name, action = action });

        Title = title;
        RunId++;
        OutputPath = outputPath ?? string.Empty;
        totalJobs = Jobs.Count;
        completedJobs = 0;
        //00 普通逐模块任务没有额外闭包信息，默认由 Job 计数驱动完成文案。
        completedModules = 0;
        totalModules = 0;
        cancelRequested = false;
        OverallProgress = 0f;
        StageProgress = 0f;
        Stage = "准备构建";
        Detail = totalJobs > 0 ? $"等待处理 {totalJobs} 个模块" : "没有可处理的模块";
        CanCancel = true;
        State = totalJobs > 0 ? BuildState.Running : BuildState.Failed;
        startedAt = EditorApplication.timeSinceStartup;
        finishedAt = State == BuildState.Running ? 0 : startedAt;
        AddLog(Detail);
        Notify();
        if (State == BuildState.Running) EditorApplication.delayCall += PrepareNextJob;
    }

    internal static void RunStaged(string title, IEnumerable<(string name, Func<IEnumerator> action)> jobs, string outputPath)
    {
        if (State == BuildState.Running) return;
        Jobs.Clear();
        Logs.Clear();
        foreach ((string name, Func<IEnumerator> action) in jobs)
            Jobs.Enqueue(new Job { name = name, stagedAction = action });
        StartRun(title, outputPath);
    }

    private static void StartRun(string title, string outputPath)
    {
        Title = title;
        RunId++;
        OutputPath = outputPath ?? string.Empty;
        totalJobs = Jobs.Count;
        completedJobs = 0;
        //00 每次新构建事务都清空上一轮统一构建模块范围，避免完成弹窗显示历史次数。
        completedModules = 0;
        totalModules = 0;
        cancelRequested = false;
        OverallProgress = 0f;
        StageProgress = 0f;
        Stage = "准备构建";
        Detail = totalJobs > 0 ? $"等待处理 {totalJobs} 个模块" : "没有可处理的模块";
        CanCancel = true;
        State = totalJobs > 0 ? BuildState.Running : BuildState.Failed;
        startedAt = EditorApplication.timeSinceStartup;
        finishedAt = State == BuildState.Running ? 0 : startedAt;
        AddLog(Detail);
        Notify();
        if (State == BuildState.Running) EditorApplication.delayCall += PrepareNextJob;
    }

    internal static void Report(string stage, string detail, float progress, bool canCancel = true)
    {
        if (State != BuildState.Running) return;
        Stage = stage ?? string.Empty;
        Detail = detail ?? string.Empty;
        StageProgress = Mathf.Clamp01(progress);
        OverallProgress = totalJobs <= 0 ? StageProgress : Mathf.Clamp01((completedJobs + StageProgress) / totalJobs);
        CanCancel = canCancel;
        AddLog(string.IsNullOrEmpty(Detail) ? Stage : $"{Stage} · {Detail}");
        Notify();
    }

    internal static bool CancellationRequested => cancelRequested;

    /// <summary>
    ///  声明当前原子构建事务包含的实际模块数量；调用方必须在依赖闭包计算完成后调用。
    /// </summary>
    internal static void SetModuleScope(int moduleCount)
    {
        //00 空闭包不是可发布构建，归零让失败路径继续沿用 Job 级诊断而不会产生“完成 0 个模块”的成功提示。
        totalModules = Math.Max(0, moduleCount);
        completedModules = 0;
        //00 只有正在执行的进度面板才更新展示，批处理入口调用不会产生无效窗口事件。
        if (State != BuildState.Running || totalModules <= 0) return;
        Detail = $"本次构建包含 {totalModules} 个模块";
        AddLog(Detail);
        Notify();
    }

    /// <summary>
    ///  在原子发布成功后确认实际完成模块数；发布之前不得调用，避免失败事务误报模块已完成。
    /// </summary>
    internal static void CompletePublishedModules(int moduleCount)
    {
        //00 只接受已声明范围内的计数，并限制到总数，防御调用方意外重复上报。
        completedModules = totalModules <= 0 ? 0 : Mathf.Clamp(moduleCount, 0, totalModules);
        if (State != BuildState.Running || totalModules <= 0) return;
        Detail = $"已原子发布 {completedModules}/{totalModules} 个模块";
        AddLog(Detail);
        Notify();
    }

    internal static void RequestCancel()
    {
        if (State != BuildState.Running || !CanCancel) return;
        cancelRequested = true;
        Stage = "正在取消";
        Detail = "将在当前安全阶段结束后停止";
        CanCancel = false;
        AddLog(Detail);
        Notify();
    }

    internal static void Dismiss()
    {
        if (State == BuildState.Running) return;
        State = BuildState.Idle;
        Notify();
    }

    private static void PrepareNextJob()
    {
        if (State != BuildState.Running) return;
        if (cancelRequested) { Finish(BuildState.Cancelled, "构建已取消", "未继续处理剩余模块"); return; }
        if (Jobs.Count == 0)
        {
            //00 统一事务优先使用真实模块范围；普通任务仍保持一个 Job 对应一个模块的旧显示语义。
            int completedCount = totalModules > 0 ? completedModules : completedJobs;
            Finish(BuildState.Succeeded, "构建完成", $"已完成 {completedCount} 个模块");
            return;
        }

        currentJob = Jobs.Dequeue();
        Stage = "准备模块";
        Detail = currentJob.name;
        StageProgress = 0f;
        OverallProgress = totalJobs <= 0 ? 0f : (float)completedJobs / totalJobs;
        CanCancel = true;
        AddLog($"开始处理模块：{currentJob.name}");
        Notify();
        EditorApplication.delayCall += ExecuteCurrentJob;
    }

    private static void ExecuteCurrentJob()
    {
        if (State != BuildState.Running || currentJob == null) return;
        try
        {
            if (currentJob.stagedAction != null)
            {
                currentJob.routine = currentJob.stagedAction.Invoke();
                AdvanceCurrentJob();
            }
            else
            {
                currentJob.action?.Invoke();
                CompleteCurrentJob();
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Finish(BuildState.Failed, "构建失败", exception.Message);
        }
    }

    private static void AdvanceCurrentJob()
    {
        if (State != BuildState.Running || currentJob?.routine == null) return;
        try
        {
            //00 RequestCancel 只会在 CanCancel=true 时受理，并会立即禁用按钮；阶段边界只检查已受理标记，不能再次要求 CanCancel=true。
            if (cancelRequested) { Finish(BuildState.Cancelled, "构建已取消", "已在阶段切换点安全停止"); return; }
            if (currentJob.routine.MoveNext())
            {
                Notify();
                ScheduleNextStage(.12f);
            }
            else CompleteCurrentJob();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            Finish(BuildState.Failed, "构建失败", exception.Message);
        }
    }

    private static void ScheduleNextStage(float delay)
    {
        resumeAt = EditorApplication.timeSinceStartup + delay;
        if (waitingForStage) return;
        waitingForStage = true;
        EditorApplication.update += WaitForNextStage;
    }

    private static void WaitForNextStage()
    {
        if (EditorApplication.timeSinceStartup < resumeAt)
        {
            Notify();
            return;
        }
        EditorApplication.update -= WaitForNextStage;
        waitingForStage = false;
        AdvanceCurrentJob();
    }

    private static void CompleteCurrentJob()
    {
        //00 即使枚举器已经自然结束也要显式 Dispose，保证自定义 IEnumerator 持有的非托管资源按协议释放。
        DisposeCurrentRoutineSafely();
        completedJobs++;
        OverallProgress = totalJobs <= 0 ? 1f : (float)completedJobs / totalJobs;
        AddLog($"模块完成：{currentJob.name}");
        currentJob = null;
        Notify();
        EditorApplication.delayCall += PrepareNextJob;
    }

    private static void Finish(BuildState state, string stage, string detail)
    {
        //00 移除可能尚未执行的编辑器回调，防止取消后旧任务回调在下一轮构建中被再次触发。
        EditorApplication.delayCall -= PrepareNextJob;
        EditorApplication.delayCall -= ExecuteCurrentJob;
        if (waitingForStage)
        {
            EditorApplication.update -= WaitForNextStage;
            waitingForStage = false;
        }
        //00 取消和失败都会经过 Finish；必须在丢弃 currentJob 引用前释放枚举器，才能执行构建事务的 finally 回滚与 staging 清理。
        DisposeCurrentRoutineSafely();
        Jobs.Clear();
        currentJob = null;
        State = state;
        finishedAt = EditorApplication.timeSinceStartup;
        Stage = stage;
        Detail = detail;
        CanCancel = false;
        StageProgress = state == BuildState.Succeeded ? 1f : StageProgress;
        OverallProgress = state == BuildState.Succeeded ? 1f : OverallProgress;
        AddLog(detail);
        Notify();
    }

    /// <summary>
    ///  释放当前 staged 枚举器；Dispose 异常会被记录，但不能阻止进度状态收口或掩盖原始构建异常。
    /// </summary>
    private static void DisposeCurrentRoutineSafely()
    {
        //00 Action 类型任务没有枚举器，或者枚举器已经被本方法摘除时无需重复释放。
        IEnumerator routine = currentJob?.routine;
        if (routine == null) return;
        //00 先清空引用再调用外部 Dispose，防止 Dispose 内部回调进度器时发生二次释放。
        currentJob.routine = null;
        //00 IEnumerator 本身不继承 IDisposable，只有实现 IDisposable 的迭代器才声明了确定性清理能力。
        if (!(routine is IDisposable disposable)) return;
        try
        {
            disposable.Dispose();
        }
        catch (Exception disposeException)
        {
            //00 清理失败必须可诊断；当前状态仍按原取消/失败结果结束，避免清理异常覆盖真正的构建根因。
            Debug.LogException(new InvalidOperationException("释放构建枚举器时发生异常。", disposeException));
        }
    }

    private static void AddLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (Logs.Count == 0 || Logs[Logs.Count - 1] != message) Logs.Add(message);
        if (Logs.Count > 8) Logs.RemoveAt(0);
    }

    private static void Notify() => Changed?.Invoke();
}
