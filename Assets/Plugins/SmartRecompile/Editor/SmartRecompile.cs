#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = System.Reflection.Assembly;

namespace UnityLibrary.EditorTool
{
    // Experimental Unity 6000.x editor tool. No source files are modified.
    // IMPORTANT: Unity can still recompile deferred comment-only changes on a later refresh.
    public class SmartRecompile : EditorWindow
    {
        private Vector2 scroll;

        [MenuItem("Tools/UnityLibrary/Smart Recompile")]
        private static void Open()
        {
            GetWindow<SmartRecompile>("Smart Recompile");
        }

        private void OnEnable()
        {
            EditorApplication.update += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(7);
            EditorGUILayout.LabelField("Smart Recompile Refresh (experimental)", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            bool enabled = SmartRecompileRefresh.Enabled;
            bool requested = EditorGUILayout.ToggleLeft("Enabled - defer comment/whitespace-only edits", enabled);
            if (requested != enabled)
                SmartRecompileRefresh.SetEnabled(requested);

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Watcher", SmartRecompileRefresh.Enabled ? "Active" : "Off");
            EditorGUILayout.LabelField("Automatic asset refresh", SmartRecompileRefresh.LockHeld ? "Blocked by this tool" : "Not blocked by this tool");
            EditorGUILayout.LabelField("Comparer", SmartRecompileRefresh.ComparerName);
            EditorGUILayout.LabelField("Scripts tracked", SmartRecompileRefresh.TrackedCount.ToString());
            EditorGUILayout.LabelField("Pending comment-only files", SmartRecompileRefresh.PendingCommentCount.ToString());
            EditorGUILayout.LabelField("Compilation", SmartRecompileRefresh.IsCompiling ? "Running" : "Idle");
            EditorGUILayout.LabelField("Detected comment-only edits", SmartRecompileRefresh.CommentChanges.ToString());
            EditorGUILayout.LabelField("Detected code edits", SmartRecompileRefresh.CodeChanges.ToString());
            EditorGUILayout.LabelField("Tool-triggered refreshes", SmartRecompileRefresh.Refreshes.ToString());
            EditorGUILayout.LabelField("Observed compilation starts", SmartRecompileRefresh.Compilations.ToString());

            EditorGUILayout.Space(7);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh Now"))
                    SmartRecompileRefresh.RefreshNow("Manual refresh from window");
                if (GUILayout.Button("Clear Log"))
                    SmartRecompileRefresh.ClearLog();
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                "When enabled, this blocks ALL automatic asset imports, not only C# scripts. " +
                "Other assets and comment-only edits are deferred until a real code change or Refresh Now. " +
                "A later refresh may STILL compile the deferred comment-only changes. " +
                "Disable Visual Studio's 'Refresh Unity's AssetDatabase on save' option, " +
                "because explicit refresh calls bypass this lock.", MessageType.Warning);

            if (!SmartRecompileRefresh.HasRoslyn)
                EditorGUILayout.HelpBox(
                    "Roslyn is not available in the Editor process. Using a conservative, " +
                    "dependency-free comparison: blank lines, indentation, and ordinary // comments. " +
                    "Other formatting edits are treated as real code changes.", MessageType.Info);

            EditorGUILayout.LabelField("Recent events", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(135));
            foreach (string line in SmartRecompileRefresh.LogLines)
                EditorGUILayout.SelectableLabel(line, EditorStyles.miniLabel, GUILayout.Height(16));
            EditorGUILayout.EndScrollView();
        }
    }

    [InitializeOnLoad]
    internal static class SmartRecompileRefresh
    {
        private const string Prefix = "SmartRecompileRefresh.v1.";
        private const double DebounceSeconds = 0.35;
        private const double BatchSeconds = 0.15;
        private static readonly string AssetsDirectory = Path.GetFullPath(Path.Combine(Application.dataPath));
        private static readonly ConcurrentQueue<string> Events = new ConcurrentQueue<string>();
        private static readonly Dictionary<string, string> Snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, double> PendingEvents = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DeferredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<string> Logs = new List<string>();
        private static FileSystemWatcher watcher;
        private static bool enabled;
        private static bool lockHeld;
        private static bool refreshPending;
        private static bool compiling;
        private static double refreshDue;
        private static int commentChanges;
        private static int codeChanges;
        private static int refreshes;
        private static int compilations;
        private static string lastCompilationReason = "";

        public static bool Enabled => enabled;
        public static bool LockHeld => lockHeld;
        public static bool IsCompiling => compiling || EditorApplication.isCompiling;
        public static int TrackedCount => Snapshots.Count;
        public static int PendingCommentCount => DeferredFiles.Count;
        public static int CommentChanges => commentChanges;
        public static int CodeChanges => codeChanges;
        public static int Refreshes => refreshes;
        public static int Compilations => compilations;
        public static bool HasRoslyn => SourceComparer.HasRoslyn;
        public static string ComparerName => HasRoslyn ? "Roslyn syntax trees (reflection)" : "Conservative line comparison";
        public static IReadOnlyList<string> LogLines => Logs;

        static SmartRecompileRefresh()
        {
            enabled = SessionState.GetBool(Prefix + "enabled", false);
            lockHeld = SessionState.GetBool(Prefix + "lockHeld", false);
            commentChanges = SessionState.GetInt(Prefix + "comments", 0);
            codeChanges = SessionState.GetInt(Prefix + "code", 0);
            refreshes = SessionState.GetInt(Prefix + "refreshes", 0);
            compilations = SessionState.GetInt(Prefix + "compilations", 0);
            string storedLog = SessionState.GetString(Prefix + "log", "");
            if (!string.IsNullOrEmpty(storedLog))
                Logs.AddRange(storedLog.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));

            EditorApplication.update += Update;
            EditorApplication.quitting += OnQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;

            if (enabled)
            {
                if (!lockHeld)
                    TakeLock();
                StartWatching();
                Log("Monitor resumed after assembly reload / Editor initialization.");
            }
            else if (lockHeld)
            {
                // Recover a stale lock left by a previous domain load, if present.
                ReleaseLock();
            }
        }

        public static void SetEnabled(bool value)
        {
            if (enabled == value)
                return;

            enabled = value;
            SessionState.SetBool(Prefix + "enabled", enabled);
            if (enabled)
            {
                TakeLock();
                StartWatching();
                Log("Enabled. Watching Assets/**/*.cs.");
            }
            else
            {
                StopWatching();
                PendingEvents.Clear();
                refreshPending = false;
                DeferredFiles.Clear();
                Snapshots.Clear();
                ReleaseLock();
                Log("Disabled. Unity's normal auto-refresh settings are now in control.");
                // Apply pending changes even if Unity Preferences has Auto Refresh disabled.
                AssetDatabase.Refresh();
            }
        }

        private static void TakeLock()
        {
            if (lockHeld)
                return;
            AssetDatabase.DisallowAutoRefresh();
            lockHeld = true;
            SessionState.SetBool(Prefix + "lockHeld", true);
        }

        private static void ReleaseLock()
        {
            if (!lockHeld)
                return;
            AssetDatabase.AllowAutoRefresh();
            lockHeld = false;
            SessionState.SetBool(Prefix + "lockHeld", false);
        }

        private static void StartWatching()
        {
            StopWatching();
            if (!Directory.Exists(AssetsDirectory))
                return;

            Snapshots.Clear();
            try
            {
                foreach (string file in Directory.EnumerateFiles(AssetsDirectory, "*.cs", SearchOption.AllDirectories))
                {
                    try { Snapshots[file] = File.ReadAllText(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }

                watcher = new FileSystemWatcher(AssetsDirectory, "*.cs")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnFileChanged;
                watcher.Created += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileRenamed;
                watcher.Error += OnWatcherError;
            }
            catch (Exception ex)
            {
                StopWatching();
                Log("ERROR: Cannot watch scripts: " + ex.Message);
            }
        }

        private static void StopWatching()
        {
            if (watcher == null)
                return;
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnFileChanged;
            watcher.Created -= OnFileChanged;
            watcher.Deleted -= OnFileChanged;
            watcher.Renamed -= OnFileRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
            watcher = null;
        }

        // FileSystemWatcher callbacks run on a different thread. Queue paths only.
        private static void OnFileChanged(object sender, FileSystemEventArgs e) => Events.Enqueue(e.FullPath);
        private static void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            Events.Enqueue(e.OldFullPath);
            Events.Enqueue(e.FullPath);
        }
        private static void OnWatcherError(object sender, ErrorEventArgs e) => Events.Enqueue("__WATCHER_ERROR__");

        private static void Update()
        {
            if (!enabled)
                return;

            double now = EditorApplication.timeSinceStartup;
            while (Events.TryDequeue(out string path))
            {
                if (path == "__WATCHER_ERROR__")
                {
                    Log("Watcher overflow/error. Turn tool off and on to rebuild its file snapshot.");
                    continue;
                }
                PendingEvents[path] = now + DebounceSeconds;
            }

            if (PendingEvents.Count > 0)
            {
                string[] ready = PendingEvents.Where(x => x.Value <= now).Select(x => x.Key).ToArray();
                foreach (string path in ready)
                {
                    PendingEvents.Remove(path);
                    CompareFile(path);
                }
            }

            if (refreshPending && now >= refreshDue && !IsCompiling && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                refreshPending = false;
                RefreshNow("Detected actual C# code change");
            }
        }

        private static void CompareFile(string path)
        {
            bool existed = Snapshots.TryGetValue(path, out string before);
            if (!File.Exists(path))
            {
                if (existed)
                {
                    Snapshots.Remove(path);
                    RegisterCodeChange(path, "Deleted");
                }
                return;
            }

            string after;
            try { after = File.ReadAllText(path); }
            catch (IOException) { PendingEvents[path] = EditorApplication.timeSinceStartup + DebounceSeconds; return; }
            catch (UnauthorizedAccessException) { PendingEvents[path] = EditorApplication.timeSinceStartup + DebounceSeconds; return; }

            if (existed && string.Equals(before, after, StringComparison.Ordinal))
                return;

            Snapshots[path] = after;
            if (existed && SourceComparer.OnlyNonCodeChanged(before, after))
            {
                DeferredFiles.Add(path);
                commentChanges++;
                SessionState.SetInt(Prefix + "comments", commentChanges);
                Log("DEFERRED: " + Relative(path) + " (comments/formatting only)");
            }
            else
            {
                RegisterCodeChange(path, existed ? "Code change" : "New script");
            }
        }

        private static void RegisterCodeChange(string path, string reason)
        {
            codeChanges++;
            SessionState.SetInt(Prefix + "code", codeChanges);
            Log("REFRESH QUEUED: " + Relative(path) + " (" + reason + ")");
            refreshPending = true;
            refreshDue = EditorApplication.timeSinceStartup + BatchSeconds;
        }

        public static void RefreshNow(string reason)
        {
            Log("Refreshing: " + reason + (DeferredFiles.Count > 0 ? " (also includes deferred edits)" : ""));
            refreshPending = false;
            DeferredFiles.Clear();
            refreshes++;
            SessionState.SetInt(Prefix + "refreshes", refreshes);
            lastCompilationReason = reason;

            // Explicit AssetDatabase.Refresh works even while blocked, but temporarily
            // releasing our own lock reduces the risk of leaving the pipeline gated.
            bool relock = enabled && lockHeld;
            if (relock)
                ReleaseLock();
            try
            {
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Log("Refresh failed: " + ex.Message);
                throw;
            }
            finally
            {
                if (relock && enabled && !lockHeld)
                    TakeLock();
            }
        }

        private static void OnCompilationStarted(object context)
        {
            compiling = true;
            compilations++;
            SessionState.SetInt(Prefix + "compilations", compilations);
            Log("Compilation STARTED" + (string.IsNullOrEmpty(lastCompilationReason) ? " (may be external)" : " after " + lastCompilationReason));
            lastCompilationReason = "";
        }

        private static void OnCompilationFinished(object context)
        {
            compiling = false;
            Log("Compilation finished.");
        }

        private static void OnBeforeAssemblyReload()
        {
            StopWatching();
            // Preserve native refresh lock across the domain reload. SessionState
            // stores its ownership so the new domain does not increment it again.
        }

        private static void OnQuitting()
        {
            StopWatching();
            enabled = false;
            SessionState.SetBool(Prefix + "enabled", false);
            ReleaseLock();
        }

        private static string Relative(string path)
        {
            return path.StartsWith(AssetsDirectory, StringComparison.OrdinalIgnoreCase)
                ? "Assets" + path.Substring(AssetsDirectory.Length).Replace('\\', '/')
                : path;
        }

        private static void Log(string message)
        {
            Logs.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (Logs.Count > 35)
                Logs.RemoveRange(35, Logs.Count - 35);
            SessionState.SetString(Prefix + "log", string.Join("\n", Logs));
        }

        public static void ClearLog()
        {
            Logs.Clear();
            SessionState.SetString(Prefix + "log", "");
        }
    }

    internal static class SourceComparer
    {
        private static bool checkedRoslyn;
        private static MethodInfo parseText;
        private static MethodInfo equivalent;

        public static bool HasRoslyn
        {
            get
            {
                LocateRoslyn();
                return parseText != null && equivalent != null;
            }
        }

        public static bool OnlyNonCodeChanged(string before, string after)
        {
            if (string.Equals(before, after, StringComparison.Ordinal))
                return true;

            // Directives can change compiled behavior, excluded even in Roslyn mode.
            if (ContainsDirective(before) || ContainsDirective(after))
                return false;

            if (HasRoslyn)
            {
                try
                {
                    object left = parseText.Invoke(null, CreateArguments(parseText, before));
                    object right = parseText.Invoke(null, CreateArguments(parseText, after));
                    return (bool)equivalent.Invoke(left, new object[] { right, false });
                }
                catch
                {
                    // Fail closed: do not skip a refresh if the parser failed.
                    return false;
                }
            }

            return SafeLineNormalize(before) == SafeLineNormalize(after);
        }

        private static bool ContainsDirective(string source)
        {
            using (StringReader reader = new StringReader(source))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        // Conservative fallback: don't attempt comment stripping in files that
        // contain multiline strings, block comments, or other complex syntax.
        private static string SafeLineNormalize(string source)
        {
            if (source.Contains("/*") || source.Contains("*/") || source.Contains("@\"") || source.Contains("\"\"\""))
                return source;

            StringBuilder result = new StringBuilder(source.Length);
            using (StringReader reader = new StringReader(source))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string code = line.Trim();
                    if (code.Length == 0)
                        continue;

                    // Documentation comments are significant for some build tools.
                    if (code.StartsWith("//", StringComparison.Ordinal) && !code.StartsWith("///", StringComparison.Ordinal))
                        continue;

                    // Only strip simple end-of-line comments if there are no quote
                    // characters; quoted strings can legally contain // characters.
                    if (code.IndexOf('"') < 0 && code.IndexOf('\'') < 0)
                    {
                        int comment = code.IndexOf("//", StringComparison.Ordinal);
                        if (comment >= 0)
                            code = code.Substring(0, comment).TrimEnd();
                    }
                    result.AppendLine(code);
                }
            }
            return result.ToString();
        }

        private static void LocateRoslyn()
        {
            if (checkedRoslyn)
                return;
            checkedRoslyn = true;
            try
            {
                Assembly assembly = UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies().FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp");
                if (assembly == null)
                    assembly = Assembly.Load("Microsoft.CodeAnalysis.CSharp");
                Type type = assembly.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree", false);
                if (type == null)
                    return;

                parseText = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ParseText" && m.GetParameters().Length >= 1 &&
                                         m.GetParameters()[0].ParameterType == typeof(string) &&
                                         m.GetParameters().Skip(1).All(p => p.IsOptional));
                if (parseText == null)
                    return;

                Type treeType = parseText.ReturnType;
                equivalent = treeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "IsEquivalentTo" && m.ReturnType == typeof(bool) &&
                                         m.GetParameters().Length == 2 &&
                                         m.GetParameters()[0].ParameterType.IsAssignableFrom(treeType) &&
                                         m.GetParameters()[1].ParameterType == typeof(bool));
            }
            catch
            {
                parseText = null;
                equivalent = null;
            }
        }

        private static object[] CreateArguments(MethodInfo method, string source)
        {
            ParameterInfo[] parameters = method.GetParameters();
            object[] args = new object[parameters.Length];
            args[0] = source;
            for (int i = 1; i < parameters.Length; i++)
            {
                object value = parameters[i].DefaultValue;
                if (value == DBNull.Value || value == Missing.Value)
                    value = parameters[i].ParameterType.IsValueType ? Activator.CreateInstance(parameters[i].ParameterType) : null;
                args[i] = value;
            }
            return args;
        }
    }
}
#endif
