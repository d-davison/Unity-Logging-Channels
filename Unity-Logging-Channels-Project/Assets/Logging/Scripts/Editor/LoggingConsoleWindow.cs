#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls; // <-- SearchField
using UnityEngine;

namespace Logging.Editor
{
    public class LoggingConsoleWindow : EditorWindow
    {
        private static readonly List<LogEntry> _entries = new List<LogEntry>();
        private Vector2 _scroll;
        private bool _collapse;
        private string _search = "";
        private SearchField _searchField;

        [MenuItem("Window/Logging Console")]
        public static void ShowWindow()
        {
            GetWindow<LoggingConsoleWindow>("Logging Console");
        }

        private void OnEnable()
        {
            if (_searchField == null)
            {
                _searchField = new SearchField();
                _searchField.downOrUpArrowKeyPressed += Repaint;
            }

            Log.OnLog += OnLog;
            Application.logMessageReceived += OnUnityLog;
        }

        private void OnDisable()
        {
            Log.OnLog -= OnLog;
            Application.logMessageReceived -= OnUnityLog;
        }

        private void OnLog(LogEntry e)
        {
            _entries.Add(e);
            Repaint();
        }

        private static readonly Regex ChannelTag = new Regex(@"\[(.+?)\]\s*", RegexOptions.Compiled);
        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            // Skip our own colored entries to avoid duplicates
            if (condition.StartsWith("<color", System.StringComparison.Ordinal)) return;

            string channel = "Unity";
            Color color = Color.white;

            var match = ChannelTag.Match(condition);
            if (match.Success)
            {
                channel = match.Groups[1].Value;
            }

            _entries.Add(new LogEntry
            {
                time = System.DateTime.Now,
                type = type,
                channel = LogChannels.Default,
                color = color,
                message = condition,
                stackTrace = stackTrace,
                context = null
            });
            Repaint();
        }

        private bool ChannelEnabled(string channel)
        {
            var c = LogConfig.Instance.GetByName(channel);
            return c == null || c.enabled;
        }

        private void OnGUI()
        {
            var cfg = LogConfig.Instance;

            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Clear", EditorStyles.toolbarButton)) _entries.Clear();
            _collapse = GUILayout.Toggle(_collapse, "Collapse", EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();
            _search = _searchField.OnToolbarGUI(_search); // <-- Cross-version safe search field
            EditorGUILayout.EndHorizontal();

            // Channel quick toggles
            EditorGUILayout.BeginHorizontal();
            foreach (var ch in cfg.channels)
            {
                var prev = ch.enabled;
                var newEnabled = GUILayout.Toggle(ch.enabled, ch.name, "Button");
                if (newEnabled != prev)
                {
                    ch.enabled = newEnabled;
                    EditorUtility.SetDirty(cfg);
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            // List
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            string lastKey = null;
            int collapsedCount = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!ChannelEnabled(e.channel.ToString())) continue;
                if (!string.IsNullOrEmpty(_search) && e.message.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                string key = _collapse ? $"{e.type}:{e.channel.ToString()}:{e.message}" : null;
                if (_collapse && key == lastKey)
                {
                    collapsedCount++;
                    continue;
                }

                var style = new GUIStyle(EditorStyles.label) { richText = true, wordWrap = true };
                var hex = ColorUtility.ToHtmlStringRGB(e.color);
                var prefix = $"<color=#{hex}>[{e.channel.ToString()}]</color> "; 

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(prefix + e.message, style, GUILayout.ExpandWidth(true));
                if (_collapse && collapsedCount > 0)
                {
                    GUILayout.Label($"x{collapsedCount + 1}", GUILayout.Width(40));
                    collapsedCount = 0;
                }
                EditorGUILayout.EndHorizontal();

                lastKey = key;
            }

            EditorGUILayout.EndScrollView();
        }
    }
}
#endif