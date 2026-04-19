using System;
using UnityEngine;

namespace Logging
{
    public struct LogEntry
    {
        public DateTime time;
        public LogType type;
        public LogChannels channel;          // enum instead of string name
        public Color color;
        public string message;
        public string stackTrace;
        public UnityEngine.Object context;
    }

    public static class Log
    {
        public static event Action<LogEntry> OnLog;

        public static void Send(LogChannels channel, string message, UnityEngine.Object context = null, LogType type = LogType.Log)
        {
            var cfg = LogConfig.Instance;
            var ch = cfg.GetById((int)channel); // may be null if enum/member/config mismatch

            // Respect per-channel enable state if we found it; otherwise default to enabled
            if (ch != null && !ch.enabled) return;

            var prefixColor = ch != null ? ch.color : Color.white;
            var displayName = ch != null ? ch.name : channel.ToString();
            var hex = ColorUtility.ToHtmlStringRGB(prefixColor);
            var final = $"<color=#{hex}>[{displayName}]</color> {message}";

            switch (type)
            {
                case LogType.Warning: Debug.LogWarning(final, context); break;
                case LogType.Error: Debug.LogError(final, context); break;
                case LogType.Assert: Debug.LogAssertion(final, context); break;
                case LogType.Exception: Debug.LogError(final, context); break;
                default: Debug.Log(final, context); break;
            }

            OnLog?.Invoke(new LogEntry
            {
                time = DateTime.Now,
                type = type,
                channel = channel,
                color = prefixColor,
                message = message,
                stackTrace = Environment.StackTrace,
                context = context
            });
        }
    }
}