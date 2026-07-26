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
        if (Jobs.Count == 0) { Finish(BuildState.Succeeded, "构建完成", $"已完成 {completedJobs} 个模块"); return; }

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
            if (cancelRequested && CanCancel) { Finish(BuildState.Cancelled, "构建已取消", "已在阶段切换点安全停止"); return; }
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
        completedJobs++;
        OverallProgress = totalJobs <= 0 ? 1f : (float)completedJobs / totalJobs;
        AddLog($"模块完成：{currentJob.name}");
        currentJob = null;
        Notify();
        EditorApplication.delayCall += PrepareNextJob;
    }

    private static void Finish(BuildState state, string stage, string detail)
    {
        if (waitingForStage)
        {
            EditorApplication.update -= WaitForNextStage;
            waitingForStage = false;
        }
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

    private static void AddLog(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (Logs.Count == 0 || Logs[Logs.Count - 1] != message) Logs.Add(message);
        if (Logs.Count > 8) Logs.RemoveAt(0);
    }

    private static void Notify() => Changed?.Invoke();
}
