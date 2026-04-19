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
    public class ChannelDeleteWindow : EditorWindow
    {
        private class UsageEntry
        {
            public string assetPath;
            public string absolutePath;
            public int line;              // 1-based
            public int matchIndex;        // index in file
            public int matchLength;
            public string funcLine;       // enclosing function signature (trimmed)
            public string codeLine;       // the exact line containing the usage
            public int dropdownIndex;     // per-row remap selection
        }

        private string _channelDisplayName;
        private int _channelId;
        private string _sourceIdent;

        private string[] _chanNames;
        private int[] _chanIds;
        private string[] _chanIdents;

        private Action _onForceDelete;  // invoked to actually remove the channel (provider handles serialized deletion)
        private Action _onPostModify;   // invoked after edits (provider rescans usages etc.)

        private Vector2 _scroll;
        private readonly List<UsageEntry> _entries = new List<UsageEntry>();
        private int _remapAllIndex = 0;

        private Regex _rx; // compiled regex for this identifier

        public static void ShowForChannel(
            string displayName,
            int channelId,
            string sourceIdent,
            string[] chanNames,
            int[] chanIds,
            string[] chanIdents,
            Action onForceDelete,
            Action onPostModify)
        {
            var w = GetWindow<ChannelDeleteWindow>(true, "Delete/Refactor Channel", true);
            w.position = new Rect(Screen.currentResolution.width / 2f - 480, Screen.currentResolution.height / 2f - 320, 960, 640);
            w.Init(displayName, channelId, sourceIdent, chanNames, chanIds, chanIdents, onForceDelete, onPostModify);
            w.Focus();
        }

        private void Init(string displayName, int channelId, string sourceIdent,
                          string[] chanNames, int[] chanIds, string[] chanIdents,
                          Action onForceDelete, Action onPostModify)
        {
            _channelDisplayName = displayName;
            _channelId = channelId;
            _sourceIdent = sourceIdent;

            _chanNames = (string[])chanNames.Clone();
            _chanIds = (int[])chanIds.Clone();
            _chanIdents = (string[])chanIdents.Clone();

            _onForceDelete = onForceDelete;
            _onPostModify = onPostModify;

            // Default remap-all selection to "Default" if present; else first different channel
            int defIndex = Array.FindIndex(_chanNames, n => string.Equals(n, "Default", StringComparison.OrdinalIgnoreCase));
            if (defIndex >= 0) _remapAllIndex = defIndex;
            else
            {
                _remapAllIndex = 0;
                int selfIndex = Array.IndexOf(_chanIds, _channelId);
                if (_remapAllIndex == selfIndex && _chanNames.Length > 1)
                    _remapAllIndex = (selfIndex + 1 < _chanNames.Length) ? selfIndex + 1 : 0;
            }

            _rx = new Regex($@"\bLogChannels\.{Regex.Escape(_sourceIdent)}\b", RegexOptions.CultureInvariant);
            Scan();
        }

        private void OnGUI()
        {
            // Header + warning
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Delete/Refactor Channel: {_channelDisplayName} (LogChannels.{_sourceIdent})", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Warning: Deleting a channel while code still references it will cause compile errors. " +
                "Use per-usage actions (Change logging channel or Delete line) or 'Remap all to' first. " +
                "Safe Delete becomes available when no usages remain.",
                MessageType.Warning);

            // Toolbar
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                Scan();

            GUILayout.Space(12);
            GUILayout.Label("Remap all to:", GUILayout.Width(100));
            _remapAllIndex = EditorGUILayout.Popup(_remapAllIndex, _chanNames, GUILayout.Width(260));
            EditorGUI.BeginDisabledGroup(_chanIds[_remapAllIndex] == _channelId);
            if (GUILayout.Button("Apply All", EditorStyles.toolbarButton, GUILayout.Width(90)))
            {
                RemapAll();
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            // List usages
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"{_entries.Count} usage(s) of LogChannels.{_sourceIdent} found", EditorStyles.miniBoldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var e in _entries.ToArray()) // snapshot
            {
                EditorGUILayout.BeginVertical("box");

                // Header row with path + line and actions
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label($"{e.assetPath}:{e.line}", EditorStyles.miniBoldLabel);
                GUILayout.FlexibleSpace();

                // Open
                if (GUILayout.Button("Open", GUILayout.Width(60)))
                {
                    var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(e.assetPath);
                    AssetDatabase.OpenAsset(obj, e.line);
                }

                // Per-usage remap
                GUILayout.Space(8);
                GUILayout.Label("Change to:", GUILayout.Width(72));
                if (e.dropdownIndex <= 0) e.dropdownIndex = Math.Max(0, Array.IndexOf(_chanIds, _channelId));
                e.dropdownIndex = EditorGUILayout.Popup(e.dropdownIndex, _chanNames, GUILayout.Width(260));

                EditorGUI.BeginDisabledGroup(_chanIds[e.dropdownIndex] == _channelId);
                if (GUILayout.Button("Change", GUILayout.Width(80)))
                {
                    if (TryReplaceSingleOccurrence(e, _chanIdents[e.dropdownIndex]))
                    {
                        _entries.Remove(e);
                        _onPostModify?.Invoke();
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
                        _onPostModify?.Invoke();
                    }
                }
                GUI.color = Color.white;

                EditorGUILayout.EndHorizontal();

                // Function signature line (bold-ish mini)
                var fnStyle = new GUIStyle(EditorStyles.miniBoldLabel) { wordWrap = true };
                EditorGUILayout.LabelField(e.funcLine, fnStyle);

                // Code line (monospace-like look; still using label)
                var codeStyle = new GUIStyle(EditorStyles.label) { wordWrap = true };
                EditorGUILayout.LabelField(e.codeLine, codeStyle);

                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            // Bottom: Safe Delete + Force Delete
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();

            // Safe delete only if no usages remain
            EditorGUI.BeginDisabledGroup(_entries.Count > 0);
            if (GUILayout.Button("Safe Delete Channel", GUILayout.Width(180)))
            {
                _onForceDelete?.Invoke(); // provider removes the channel
                Close();
                return;
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.FlexibleSpace();

            // Force delete (always enabled)
            if (GUILayout.Button("Force Delete Channel", GUILayout.Width(180)))
            {
                var confirm = EditorUtility.DisplayDialog(
                    "Force Delete Channel?",
                    $"You are about to remove the channel '{_channelDisplayName}'.\n\n" +
                    $"Remaining usages of LogChannels.{_sourceIdent}: {_entries.Count} occurrence(s).\n\n" +
                    "Proceeding may cause compile errors if code still references this channel.\n\n" +
                    "Are you sure you want to force delete?",
                    "Delete", "Cancel");
                if (confirm)
                {
                    _onForceDelete?.Invoke();
                    Close();
                    return;
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        private void RemapAll()
        {
            string targetIdent = _chanIdents[_remapAllIndex];
            if (_chanIds[_remapAllIndex] == _channelId) return;

            int totalReplacements = 0;
            int filesChanged = 0;

            // Group by file for a single-pass edit per file
            var byFile = _entries.GroupBy(x => x.absolutePath).ToList();

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var grp in byFile)
                {
                    var filePath = grp.Key;
                    string text;
                    try { text = File.ReadAllText(filePath); }
                    catch { continue; }

                    // Replace all occurrences in this file
                    string newText = _rx.Replace(text, $"LogChannels.{targetIdent}");
                    if (!string.Equals(newText, text, StringComparison.Ordinal))
                    {
                        try
                        {
                            File.WriteAllText(filePath, newText);
                            filesChanged++;

                            // Count quickly (approx):
                            totalReplacements += _rx.Matches(text).Count;

                            var rel = "Assets" + filePath.Substring(Application.dataPath.Length);
                            AssetDatabase.ImportAsset(rel);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"Could not write {filePath}: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // Rescan this window list and notify provider
            Scan();
            _onPostModify?.Invoke();

            EditorUtility.DisplayDialog("Remap complete",
                $"Updated {totalReplacements} occurrence(s) in {filesChanged} file(s).", "OK");
        }

        private bool TryReplaceSingleOccurrence(UsageEntry e, string targetIdent)
        {
            string text;
            try { text = File.ReadAllText(e.absolutePath); }
            catch (Exception ex)
            {
                Debug.LogError($"Could not read {e.assetPath}: {ex.Message}");
                return false;
            }

            // Re-match to find fresh position at/near line (indices may have shifted)
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
                Scan(); // refresh list
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

            // Find the nearest match again to get a current index
            Match m = FindMatchAtOrNearLine(text, e.line);
            if (m == null)
            {
                Debug.LogWarning($"Entry not found anymore in {e.assetPath}:{e.line}. Refreshing.");
                Scan();
                return false;
            }

            int idx = m.Index;

            // Remove the entire line containing the match and following newline(s)
            GetLineBoundsAtIndex(text, idx, out int lineStart, out int lineEndInclBreaks);

            string newText = text.Substring(0, lineStart) + text.Substring(lineEndInclBreaks);

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

        // Return first match nearest to the given line, or null if none.
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
                        matchIndex = m.Index,
                        matchLength = m.Length,
                        funcLine = func,
                        codeLine = code,
                        dropdownIndex = Math.Max(0, Array.IndexOf(_chanIds, _channelId)) // default to self
                    });
                }
            }
        }

        // Helpers to extract clean function signature and exact code line ------------------------

        private static void ExtractFunctionAndCodeLines(string text, int matchIndex, out string funcLine, out string codeLine)
        {
            // Get the line with the match
            GetLineBoundsAtIndex(text, matchIndex, out int lineStart, out int lineEndInclBreaks);
            // single line content (without CR/LF)
            int lineEnd = lineEndInclBreaks;
            while (lineEnd > lineStart && (text[lineEnd - 1] == '\r' || text[lineEnd - 1] == '\n')) lineEnd--;

            codeLine = text.Substring(lineStart, lineEnd - lineStart).Trim();

            // Find a plausible function signature above
            funcLine = FindNearestFunctionHeaderAbove(text, lineStart);
        }

        private static void GetLineBoundsAtIndex(string text, int index, out int lineStart, out int lineEndWithBreaks)
        {
            lineStart = index;
            while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r') lineStart--;

            lineEndWithBreaks = index;
            while (lineEndWithBreaks < text.Length && text[lineEndWithBreaks] != '\n' && text[lineEndWithBreaks] != '\r') lineEndWithBreaks++;
            // include following newline chars
            while (lineEndWithBreaks < text.Length && (text[lineEndWithBreaks] == '\n' || text[lineEndWithBreaks] == '\r')) lineEndWithBreaks++;
        }

        private static string FindNearestFunctionHeaderAbove(string text, int fromIndex)
        {
            // Scan backward up to 80 lines looking for something that looks like a method signature.
            // Heuristics: contains '(' and ')', does not end with ';', and does not start with control keywords.
            var control = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return" };

            int pos = fromIndex - 1;
            int linesChecked = 0;
            while (pos > 0 && linesChecked < 80)
            {
                // Find previous line
                int end = pos;
                while (end > 0 && text[end - 1] != '\n' && text[end - 1] != '\r') end--;
                int start = end - 1;
                while (start > 0 && text[start - 1] != '\n' && text[start - 1] != '\r') start--;

                string line = text.Substring(start, end - start).Trim();
                linesChecked++;

                // Skip empty, attributes, and region/compiler lines
                if (string.IsNullOrEmpty(line) || line.StartsWith("[") || line.StartsWith("#"))
                {
                    pos = start - 1;
                    continue;
                }

                bool looksLikeMethod = line.Contains("(") && line.Contains(")") && !line.EndsWith(";");
                if (looksLikeMethod)
                {
                    // get first token
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

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, @"[ \t]+", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + " …";
        }
    }
}
#endif