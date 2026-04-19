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
    public class ChannelUsagesWindow : EditorWindow
    {
        private class UsageEntry
        {
            public string assetPath;
            public string absolutePath;
            public int line;        // 1-based
            public string funcLine; // enclosing function signature
            public string codeLine; // exact usage line
            public int dropdownIndex; // per-row "Change to" selection
        }

        private string _channelDisplayName;
        private string _sourceIdent;

        // Channels (from LogConfig) for the "Change to" dropdown
        private string[] _chanNames;
        private int[] _chanIds;
        private string[] _chanIdents;

        private Vector2 _scroll;
        private readonly List<UsageEntry> _entries = new List<UsageEntry>();
        private Regex _rx;
        private string _search = "";

        public static void ShowForChannel(string displayName, string sourceIdent)
        {
            var w = GetWindow<ChannelUsagesWindow>(true, "Channel Usages", true);
            w.position = new Rect(Screen.currentResolution.width / 2f - 480, Screen.currentResolution.height / 2f - 320, 960, 640);
            w.Init(displayName, sourceIdent);
            w.Focus();
        }

        private void Init(string displayName, string sourceIdent)
        {
            _channelDisplayName = displayName;
            _sourceIdent = sourceIdent;
            _rx = new Regex($@"\bLogChannels\.{Regex.Escape(_sourceIdent)}\b", RegexOptions.CultureInvariant);

            RefreshChannels();
            Scan();
        }

        private void RefreshChannels()
        {
            var cfg = LogConfig.Instance;
            if (cfg == null || cfg.channels == null) { _chanNames = new string[0]; _chanIds = new int[0]; _chanIdents = new string[0]; return; }

            // Build arrays (sanitize + unique suffix consistent with generator)
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>(cfg.channels.Count);
            var ids = new List<int>(cfg.channels.Count);
            var idents = new List<string>(cfg.channels.Count);

            foreach (var c in cfg.channels)
            {
                if (c == null) continue;
                names.Add(c.name ?? "Channel");
                ids.Add(c.Id);
                var baseName = Sanitize(c.name);
                idents.Add(MakeUniqueMember(baseName, taken));
            }

            _chanNames = names.ToArray();
            _chanIds = ids.ToArray();
            _chanIdents = idents.ToArray();
        }

        private void OnGUI()
        {
            // Header
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Usages of: {_channelDisplayName} (LogChannels.{_sourceIdent})", EditorStyles.boldLabel);

            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                RefreshChannels();
                Scan();
            }

            GUILayout.FlexibleSpace();
            _search = GUILayout.TextField(_search, GUI.skin.FindStyle("ToolbarSeachTextField") ?? EditorStyles.toolbarTextField, GUILayout.Width(260));
            EditorGUILayout.EndHorizontal();

            // Summary
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{_entries.Count} usage(s) found", EditorStyles.miniBoldLabel);

            // List
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var e in _entries.ToArray())
            {
                if (!PassesFilter(e)) continue;

                EditorGUILayout.BeginVertical("box");

                // Row: path + actions
                EditorGUILayout.BeginHorizontal();

                GUILayout.Label($"{e.assetPath}:{e.line}", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();

                // Open
                if (GUILayout.Button("Open", GUILayout.Width(60)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(e.assetPath);
                    AssetDatabase.OpenAsset(obj, e.line);
                }

                // Change to: [popup] [Change]
                GUILayout.Space(10);
                GUILayout.Label("Change to:", GUILayout.Width(72));
                if (e.dropdownIndex < 0 || e.dropdownIndex >= _chanNames.Length) e.dropdownIndex = GetDefaultDropdownIndex();
                e.dropdownIndex = EditorGUILayout.Popup(e.dropdownIndex, _chanNames, GUILayout.Width(260));

                bool sameAsSource = (_chanIdents.Length > 0 && e.dropdownIndex >= 0 && e.dropdownIndex < _chanIdents.Length)
                                    ? string.Equals(_chanIdents[e.dropdownIndex], _sourceIdent, StringComparison.Ordinal)
                                    : true;

                EditorGUI.BeginDisabledGroup(sameAsSource || _chanIdents.Length == 0);
                if (GUILayout.Button("Change", GUILayout.Width(80)))
                {
                    if (TryReplaceSingleOccurrence(e, _chanIdents[e.dropdownIndex]))
                    {
                        _entries.Remove(e);
                    }
                }
                EditorGUI.EndDisabledGroup();

                // Delete line
                GUILayout.Space(8);
                GUI.color = new Color(0.85f, 0.35f, 0.35f);
                if (GUILayout.Button("Delete line", GUILayout.Width(100)))
                {
                    if (TryDeleteLine(e))
                    {
                        _entries.Remove(e);
                    }
                }
                GUI.color = Color.white;

                EditorGUILayout.EndHorizontal();

                // Function signature then code line
                var fnStyle = new GUIStyle(EditorStyles.miniBoldLabel) { wordWrap = true };
                var codeStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
                EditorGUILayout.LabelField(e.funcLine, fnStyle);
                EditorGUILayout.LabelField(e.codeLine, codeStyle);

                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        private int GetDefaultDropdownIndex()
        {
            // Prefer "Default" if present; else first entry
            int def = Array.FindIndex(_chanNames ?? Array.Empty<string>(), n => string.Equals(n, "Default", StringComparison.OrdinalIgnoreCase));
            return def >= 0 ? def : 0;
        }

        private bool PassesFilter(UsageEntry e)
        {
            if (string.IsNullOrWhiteSpace(_search)) return true;
            var s = _search.Trim();
            return e.assetPath.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                   || e.funcLine.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0
                   || e.codeLine.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Scan()
        {
            _entries.Clear();

            var files = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories)
                                 .Where(p => !p.Replace('\\', '/')
                                               .EndsWith("/Logging/Generated/LogChannels.generated.cs", StringComparison.OrdinalIgnoreCase))
                                 .ToArray();

            foreach (var abs in files)
            {
                string text;
                try { text = File.ReadAllText(abs); }
                catch { continue; }

                foreach (Match m in _rx.Matches(text))
                {
                    // Compute 1-based line number for the match
                    int lineNum = 1;
                    for (int i = 0; i < m.Index && i < text.Length; i++)
                        if (text[i] == '\n') lineNum++;

                    // Extract function signature line above and the exact code line
                    ExtractFunctionAndCodeLines(text, m.Index, out string func, out string code);

                    var rel = "Assets" + abs.Substring(Application.dataPath.Length);

                    _entries.Add(new UsageEntry
                    {
                        assetPath = rel,
                        absolutePath = abs,
                        line = lineNum,
                        funcLine = func,
                        codeLine = code,
                        dropdownIndex = GetDefaultDropdownIndex()
                    });
                }
            }
        }

        // Actions --------------------------------------------------------------------------------

        private bool TryReplaceSingleOccurrence(UsageEntry e, string targetIdent)
        {
            string text;
            try { text = File.ReadAllText(e.absolutePath); }
            catch (Exception ex)
            {
                Debug.LogError($"Could not read {e.assetPath}: {ex.Message}");
                return false;
            }

            // Re-match at/near the stored line (indices may have changed)
            Match m = FindMatchAtOrNearLine(text, e.line);
            if (m == null)
            {
                Debug.LogWarning($"Entry not found anymore in {e.assetPath}:{e.line}. Refreshing.");
                Scan();
                return false;
            }

            int idx = m.Index;
            int len = m.Length;

            string newText = text.Substring(0, idx) + $"LogChannels.{targetIdent}" + text.Substring(idx + len);
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

        private bool TryDeleteLine(UsageEntry e)
        {
            string text;
            try { text = File.ReadAllText(e.absolutePath); }
            catch (Exception ex)
            {
                Debug.LogError($"Could not read {e.assetPath}: {ex.Message}");
                return false;
            }

            Match m = FindMatchAtOrNearLine(text, e.line);
            if (m == null)
            {
                Debug.LogWarning($"Entry not found anymore in {e.assetPath}:{e.line}. Refreshing.");
                Scan();
                return false;
            }

            GetLineBoundsAtIndex(text, m.Index, out int lineStart, out int lineEndWithBreaks);
            string newText = text.Substring(0, lineStart) + text.Substring(lineEndWithBreaks);

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

        // Matching / extraction helpers ----------------------------------------------------------

        private Match FindMatchAtOrNearLine(string text, int line)
        {
            int curLine = 1;
            int pos = 0;
            int bestDelta = int.MaxValue;
            Match best = null;

            foreach (Match m in _rx.Matches(text))
            {
                // Advance curLine to this match
                while (pos < m.Index && pos < text.Length)
                {
                    if (text[pos] == '\n') curLine++;
                    pos++;
                }

                int delta = Math.Abs(curLine - line);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = m;
                    if (delta == 0) break;
                }
            }
            return best;
        }

        private static void ExtractFunctionAndCodeLines(string text, int matchIndex, out string funcLine, out string codeLine)
        {
            GetLineBoundsAtIndex(text, matchIndex, out int lineStart, out int lineEndInclBreaks);

            int lineEnd = lineEndInclBreaks;
            while (lineEnd > lineStart && (text[lineEnd - 1] == '\r' || text[lineEnd - 1] == '\n')) lineEnd--;
            codeLine = text.Substring(lineStart, lineEnd - lineStart).Trim();

            funcLine = FindNearestFunctionHeaderAbove(text, lineStart);
        }

        private static void GetLineBoundsAtIndex(string text, int index, out int lineStart, out int lineEndWithBreaks)
        {
            lineStart = index;
            while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r') lineStart--;

            lineEndWithBreaks = index;
            while (lineEndWithBreaks < text.Length && text[lineEndWithBreaks] != '\n' && text[lineEndWithBreaks] != '\r') lineEndWithBreaks++;
            while (lineEndWithBreaks < text.Length && (text[lineEndWithBreaks] == '\n' || text[lineEndWithBreaks] == '\r')) lineEndWithBreaks++;
        }

        private static string FindNearestFunctionHeaderAbove(string text, int fromIndex)
        {
            var control = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return" };

            int pos = fromIndex - 1;
            int linesChecked = 0;
            while (pos > 0 && linesChecked < 80)
            {
                int end = pos;
                while (end > 0 && text[end - 1] != '\n' && text[end - 1] != '\r') end--;
                int start = end - 1;
                while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r') start--;

                string line = text.Substring(start, end - start).Trim();
                linesChecked++;

                if (string.IsNullOrEmpty(line) || line.StartsWith("[") || line.StartsWith("#"))
                {
                    pos = start - 1;
                    continue;
                }

                bool looksLikeMethod = line.Contains("(") && line.Contains(")") && !line.EndsWith(";");
                if (looksLikeMethod)
                {
                    var firstToken = FirstToken(line);
                    if (!control.Contains(firstToken))
                        return line;
                }

                pos = start - 1;
            }
            return "(unknown function)";
        }

        private static string FirstToken(string line)
        {
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int j = i;
            while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_' )) j++;
            return (j > i) ? line.Substring(i, j - i) : "";
        }

        // Sanitize + unique helpers (match your enum generator) ---------------------------------

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