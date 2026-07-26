using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

internal static class ZMEditorAnimation
{
    private sealed class State
    {
        internal float value;
        internal float from;
        internal float target;
        internal float duration;
        internal double startedAt;
    }

    private static readonly Dictionary<string, State> States = new Dictionary<string, State>();

    internal static float Tween(string key, float target, float duration = .16f)
    {
        double now = EditorApplication.timeSinceStartup;
        if (!States.TryGetValue(key, out State state))
        {
            state = new State { value = target, from = target, target = target, duration = duration, startedAt = now };
            States.Add(key, state);
            return target;
        }

        UpdateValue(state, now);
        if (!Mathf.Approximately(state.target, target))
        {
            state.from = state.value;
            state.target = target;
            state.duration = Mathf.Max(.01f, duration);
            state.startedAt = now;
        }

        UpdateValue(state, now);
        if (!Mathf.Approximately(state.value, state.target)) RequestRepaint();
        return state.value;
    }

    internal static void Restart(string key, float duration = .2f)
    {
        double now = EditorApplication.timeSinceStartup;
        States[key] = new State { value = 0f, from = 0f, target = 1f, duration = Mathf.Max(.01f, duration), startedAt = now };
        RequestRepaint();
    }

    internal static float Ease(float value) => value * value * (3f - 2f * value);

    private static void UpdateValue(State state, double now)
    {
        float elapsed = (float)(now - state.startedAt);
        float normalized = Mathf.Clamp01(elapsed / state.duration);
        state.value = Mathf.LerpUnclamped(state.from, state.target, Ease(normalized));
        if (normalized >= 1f) state.value = state.target;
    }

    private static void RequestRepaint()
    {
        if (EditorWindow.focusedWindow != null) EditorWindow.focusedWindow.Repaint();
    }
}
