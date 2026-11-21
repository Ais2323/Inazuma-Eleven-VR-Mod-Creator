using System;
using System.Collections.Concurrent;
using UnityEngine;

// Shared UI/Console logging. Worker threads only enqueue; Loading drains on Unity's main thread.
public static class AppLog
{
    internal readonly struct Entry
    {
        internal readonly string Text;
        internal readonly bool ShowInfo;
        internal Entry(string text, bool showInfo) { Text = text; ShowInfo = showInfo; }
    }
    static readonly ConcurrentQueue<Entry> pending = new();
    static bool enabled;
    [ThreadStatic] static bool forwarding;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        while (pending.TryDequeue(out _)) { }
        enabled = true;
        Application.logMessageReceivedThreaded -= Receive;
        Application.logMessageReceivedThreaded += Receive;
        Loading.EnsureAvailable();
    }

    public static void Info(object message, UnityEngine.Object context = null) => Write(message, LogType.Log, false, context);
    public static void Result(object message, UnityEngine.Object context = null) => Write(message, LogType.Log, true, context);
    public static void Warning(object message, UnityEngine.Object context = null) => Write(message, LogType.Warning, true, context);
    public static void Error(object message, UnityEngine.Object context = null) => Write(message, LogType.Error, true, context);
    public static void Exception(Exception error, UnityEngine.Object context = null) => Write(error, LogType.Exception, true, context);

    static void Write(object message, LogType type, bool showInfo, UnityEngine.Object context)
    {
        if (enabled) Enqueue(message is Exception error ? error.Message : message?.ToString() ?? "", showInfo);
        bool previous = forwarding;
        forwarding = true;
        try {
            switch (type) {
                case LogType.Warning: Debug.LogWarning(message, context); break;
                case LogType.Error: Debug.LogError(message, context); break;
                case LogType.Exception: Debug.LogException((Exception)message, context); break;
                default: Debug.Log(message, context); break;
            }
        }
        finally { forwarding = previous; }
    }

    static void Receive(string text, string stack, LogType type)
    {
        if (!enabled || forwarding) return;
        // Includes unhandled exceptions and native/third-party errors in Player builds.
        Enqueue(text, type != LogType.Log);
    }

    static void Enqueue(string text, bool showInfo)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 4000) text = text.Substring(0, 4000) + "…";
        // Bound repeated/native log spam without allowing progress to evict errors.
        if (!showInfo && pending.Count >= 128) return;
        if (pending.Count >= 256) pending.TryDequeue(out _);
        pending.Enqueue(new Entry(text, showInfo));
    }

    internal static bool TryRead(out Entry entry) => pending.TryDequeue(out entry);
}
