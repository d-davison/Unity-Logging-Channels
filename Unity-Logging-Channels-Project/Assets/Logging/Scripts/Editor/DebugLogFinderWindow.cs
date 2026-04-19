#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Logging.Editor
{
    public class DebugLogFinderWindow : EditorWindow
    {
        [Serializable]
        private class LogCallEntry
        {
            public string assetPath;
            public string absolutePath;
            public int line;
            public string callType;   // Log, LogWarning, LogError, LogAssertion, LogException
            public string argsRaw;    // inside (...), raw text
            public int matchIndex;    // in file text
            public int matchLength;
            public string preview;    // short preview for UI
            public int channelPopupIndex; // for per-entry "Swap to..." selection
        }

        private static readonly Regex LogCallRx = new Regex(
            @"\b(?:UnityEngine\.)?Debug\.(Log|LogWarning|LogError|LogAssertion|LogException)\s*\((.*?)\)\s*;",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private Vector2 _scroll;
        private List<LogCallEntry> _entries = new List<LogCallEntry>();
        private List<string> _channelNames = new List<string>();
        private List<string> _channelEnumIdents = new List<string>();
        private int _defaultChannelIndex = 0;
        private string _search = "";
        private bool _showWarnings = true;
        private bool _showErrors = true;
        private bool _showLogs = true;
        private bool _showAssertions = true;
        private bool _showExceptions = true;
        
        public static void ShowWindow()
        {
            var w = GetWindow<DebugLogFinderWindow>("Find Debug.Log");
            w.position = new Rect(Screen.currentResolution.width / 2f - 400, Screen.currentResolution.height / 2f - 300, 800, 600);
            w.RefreshChannels();
            w.Scan();
        }

        private void OnEnable()
        {
            RefreshChannels();
            Scan();
        }

        private void OnGUI()
        {
            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                RefreshChannels();
                Scan();
            }

            GUILayout.Space(8);
            GUILayout.Label("Default replacement:", GUILayout.Width(140));
            var newDefault = EditorGUILayout.Popup(_defaultChannelIndex, _channelNames.ToArray(), GUILayout.MaxWidth(220));
            if (newDefault != _defaultChannelIndex) _defaultChannelIndex = newDefault;

            GUILayout.Space(16);
            _showLogs = GUILayout.Toggle(_showLogs, "Log", EditorStyles.toolbarButton);
            _showWarnings = GUILayout.Toggle(_showWarnings, "Warning", EditorStyles.toolbarButton);
            _showErrors = GUILayout.Toggle(_showErrors, "Error", EditorStyles.toolbarButton);
            _showAssertions = GUILayout.Toggle(_showAssertions, "Assertion", EditorStyles.toolbarButton);
            _showExceptions = GUILayout.Toggle(_showExceptions, "Exception", EditorStyles.toolbarButton);

            GUILayout.FlexibleSpace();
            _search = GUILayout.TextField(_search, GUI.skin.FindStyle("ToolbarSeachTextField") ?? EditorStyles.toolbarTextField, GUILayout.Width(220));
            EditorGUILayout.EndHorizontal();

            // Summary
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{_entries.Count} Debug.* calls found", EditorStyles.miniBoldLabel);

            // List
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var e in _entries)
            {
                if (!PassesFilter(e)) continue;

                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();

                // Path + line + type
                var nicePath = e.assetPath;
                GUILayout.Label($"{nicePath}:{e.line}  •  Debug.{e.callType}", EditorStyles.miniBoldLabel);

                GUILayout.FlexibleSpace();

                // Open
                if (GUILayout.Button("Open", GUILayout.Width(60)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(e.assetPath);
                    AssetDatabase.OpenAsset(obj, e.line);
                }

                // Swap to specific channel (popup)
                GUILayout.Space(6);
                GUILayout.Label("Channel:", GUILayout.Width(56));
                e.channelPopupIndex = EditorGUILayout.Popup(e.channelPopupIndex, _channelNames.ToArray(), GUILayout.MaxWidth(220));
                if (GUILayout.Button("Swap", GUILayout.Width(60)))
                {
                    if (TryReplaceCall(e, e.channelPopupIndex))
                    {
                        Repaint();
                    }
                }

                // Remove line
                GUILayout.Space(6);
                GUI.color = new Color(0.85f, 0.35f, 0.35f);
                if (GUILayout.Button("Remove line", GUILayout.Width(100)))
                {
                    if (TryRemoveCall(e))
                    {
                        GUI.color = Color.white;
                        Repaint();
                        continue;
                    }
                }
                GUI.color = Color.white;

                EditorGUILayout.EndHorizontal();

                // Snippet preview
                var previewStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
                EditorGUILayout.LabelField(TrimPreview(e.preview, 220), previewStyle);

                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private bool PassesFilter(LogCallEntry e)
        {
            bool typeOk =
                (e.callType == "Log" && _showLogs) ||
                (e.callType == "LogWarning" && _showWarnings) ||
                (e.callType == "LogError" && _showErrors) ||
                (e.callType == "LogAssertion" && _showAssertions) ||
                (e.callType == "LogException" && _showExceptions);

            if (!typeOk) return false;
            if (string.IsNullOrEmpty(_search)) return true;

            var s = _search.Trim();
            return e.assetPath.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                || e.preview.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshChannels()
        {
            var cfg = LogConfig.Instance;
            _channelNames = cfg.channels.Select(c => c?.name ?? "Channel").ToList();

            // Build enum identifiers consistent with generator (sanitize + unique suffix)
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _channelEnumIdents = new List<string>(_channelNames.Count);
            foreach (var n in _channelNames)
            {
                var baseName = Sanitize(n);
                var unique = MakeUniqueMember(baseName, taken);
                _channelEnumIdents.Add(unique);
            }

            // Prefer "Default" if present
            int idx = _channelNames.FindIndex(n => string.Equals(n, "Default", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _defaultChannelIndex = idx;
            else if (_defaultChannelIndex < 0 || _defaultChannelIndex >= _channelNames.Count)
                _defaultChannelIndex = 0;
        }

        private void Scan()
        {
            _entries.Clear();

            var files = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories)
                                 .Where(p => !p.Replace('\\', '/')
                                               .EndsWith("/Logging/Generated/LogChannels.generated.cs", StringComparison.OrdinalIgnoreCase))
                                 .ToArray();

            foreach (var absPath in files)
            {
                string text;
                try { text = File.ReadAllText(absPath); }
                catch { continue; }

                foreach (Match m in LogCallRx.Matches(text))
                {
                    var type = m.Groups[1].Value; // Log, LogWarning, etc.
                    var args = m.Groups[2].Value; // inside (...)

                    // Compute line by counting newlines up to match index
                    int line = 1;
                    for (int i = 0; i < m.Index && i < text.Length; i++)
                        if (text[i] == '\n') line++;

                    var relPath = "Assets" + absPath.Substring(Application.dataPath.Length);
                    var start = Math.Max(0, m.Index - 80);
                    var len = Math.Min(m.Length + 160, text.Length - start);
                    var snip = text.Substring(start, len);

                    _entries.Add(new LogCallEntry
                    {
                        assetPath = relPath,
                        absolutePath = absPath,
                        line = line,
                        callType = type,
                        argsRaw = args,
                        matchIndex = m.Index,
                        matchLength = m.Length,
                        preview = snip.Replace("\r", " ").Replace("\n", " ")
                    });
                }
            }
        }

        private bool TryReplaceCall(LogCallEntry e, int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= _channelEnumIdents.Count) return false;

            string text;
            try { text = File.ReadAllText(e.absolutePath); }
            catch (Exception ex)
            {
                Debug.LogError($"Could not read {e.assetPath}: {ex.Message}");
                return false;
            }

            var current = FindCallAtOrNearLine(text, e.line);
            if (current == null)
            {
                Debug.LogWarning($"Entry not found anymore in {e.assetPath}:{e.line}. Rescanning.");
                Scan();
                return false;
            }

            var args = current.Value.args;
            var callType = current.Value.callType;
            int idx = current.Value.index;
            int len = current.Value.length;

            // Parse args: message[, context]
            SplitTopLevelArgs(args, out var messageExpr, out var contextExpr);

            string typeToken = "Log";
            switch (callType)
            {
                case "LogWarning": typeToken = "Warning"; break;
                case "LogError": typeToken = "Error"; break;
                case "LogAssertion": typeToken = "Assert"; break;
                case "LogException": typeToken = "Exception"; break;
                default: typeToken = "Log"; break;
            }

            // Preserve indentation
            string indent = ComputeIndentBefore(text, idx);

            string enumIdent = _channelEnumIdents[channelIndex];
            // Build replacement (fully qualified to avoid usings)
            string replacement = $"{indent}Logging.Log.Send(Logging.LogChannels.{enumIdent}, \"\" + {messageExpr}"
                               + (string.IsNullOrEmpty(contextExpr) ? "" : $", {contextExpr}")
                               + $", UnityEngine.LogType.{typeToken});";

            // Replace in text
            string newText = text.Substring(0, idx) + replacement + text.Substring(idx + len);

            try
            {
                File.WriteAllText(e.absolutePath, newText);
                var rel = e.assetPath;
                AssetDatabase.ImportAsset(rel);
                // Rescan to refresh entries and line numbers
                Scan();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Could not write {e.assetPath}: {ex.Message}");
                return false;
            }
        }

        private bool TryRemoveCall(LogCallEntry e)
        {
            string text;
            try { text = File.ReadAllText(e.absolutePath); }
            catch (Exception ex)
            {
                Debug.LogError($"Could not read {e.assetPath}: {ex.Message}");
                return false;
            }

            var current = FindCallAtOrNearLine(text, e.line);
            if (current == null)
            {
                Debug.LogWarning($"Entry not found anymore in {e.assetPath}:{e.line}. Rescanning.");
                Scan();
                return false;
            }

            int idx = current.Value.index;
            int len = current.Value.length;

            // Remove the call
            string newText = text.Substring(0, idx) + text.Substring(idx + len);

            try
            {
                File.WriteAllText(e.absolutePath, newText);
                var rel = e.assetPath;
                AssetDatabase.ImportAsset(rel);
                Scan();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Could not write {e.assetPath}: {ex.Message}");
                return false;
            }
        }

        private (int index, int length, string args, string callType)? FindCallAtOrNearLine(string text, int line)
        {
            int bestDelta = int.MaxValue;
            (int index, int length, string args, string callType)? best = null;

            foreach (Match m in LogCallRx.Matches(text))
            {
                int mLine = 1;
                for (int i = 0; i < m.Index && i < text.Length; i++)
                    if (text[i] == '\n') mLine++;

                int delta = Math.Abs(mLine - line);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = (m.Index, m.Length, m.Groups[2].Value, m.Groups[1].Value);
                    if (delta == 0) break;
                }
            }

            return best;
        }

        private static void SplitTopLevelArgs(string raw, out string messageExpr, out string contextExpr)
        {
            var parts = new List<string>();
            int depth = 0;
            bool inStr = false;
            char strQ = '\0';
            bool esc = false;
            int start = 0;

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (inStr)
                {
                    if (esc) { esc = false; continue; }
                    if (c == '\\') { esc = true; continue; }
                    if (c == strQ) { inStr = false; strQ = '\0'; continue; }
                    continue;
                }
                if (c == '"' || c == '\'') { inStr = true; strQ = c; continue; }
                if (c == '(') { depth++; continue; }
                if (c == ')') { if (depth > 0) depth--; continue; }
                if (c == ',' && depth == 0)
                {
                    parts.Add(raw.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start <= raw.Length)
                parts.Add(raw.Substring(start));

            messageExpr = parts.Count > 0 ? parts[0].Trim() : "\"\"";
            contextExpr = parts.Count > 1 ? parts[1].Trim() : null;
        }

        private static string ComputeIndentBefore(string text, int index)
        {
            int i = index - 1;
            while (i >= 0 && text[i] != '\n' && text[i] != '\r') i--;
            int j = i + 1;
            int k = j;
            while (k < text.Length && (text[k] == ' ' || text[k] == '\t')) k++;
            return text.Substring(j, Math.Max(0, k - j));
        }

        private static string TrimPreview(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"\s+", " ");
            if (s.Length <= max) return s;
            return s.Substring(0, max) + " …";
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Channel";
            var filtered = new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            if (string.IsNullOrEmpty(filtered)) filtered = "Channel";
            if (!char.IsLetter(filtered[0]) && filtered[0] != '_') filtered = "_" + filtered;
            return filtered;
        }

        private static string MakeUniqueMember(string baseName, HashSet<string> taken)
        {
            var name = baseName;
            int i = 1;
            while (taken.Contains(name))
            {
                name = $"{baseName}_{i}";
                i++;
            }
            taken.Add(name);
            return name;
        }
    }
}
#endif