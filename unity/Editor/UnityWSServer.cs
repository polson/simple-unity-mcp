using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace UnityMCP.Editor
{
    public class UnityMCPSettings : EditorWindow
    {
        private const string PortKey = "UnityMCP_Port";
        private const string AutoStartKey = "UnityMCP_AutoStart";

        public static int Port
        {
            get => EditorPrefs.GetInt(PortKey, 6789);
            set => EditorPrefs.SetInt(PortKey, value);
        }

        public static bool AutoStart
        {
            get => EditorPrefs.GetBool(AutoStartKey, true);
            set => EditorPrefs.SetBool(AutoStartKey, value);
        }

        [MenuItem("Window/Unity MCP Settings")]
        public static void ShowWindow()
        {
            GetWindow<UnityMCPSettings>("Unity MCP Settings");
        }

        private void OnGUI()
        {
            GUILayout.Label("Model Context Protocol Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            int newPort = EditorGUILayout.IntField("WebSocket Port", Port);
            bool newAutoStart = EditorGUILayout.Toggle("Auto Start Server", AutoStart);
            
            if (EditorGUI.EndChangeCheck())
            {
                Port = newPort;
                AutoStart = newAutoStart;
            }

            GUILayout.Space(10);
            
            bool isRunning = UnityWSServer.IsRunning;
            GUILayout.Label($"Status: {(isRunning ? "Running" : "Stopped")}");

            if (isRunning)
            {
                if (GUILayout.Button("Stop Server"))
                {
                    UnityWSServer.Stop();
                }
            }
            else
            {
                if (GUILayout.Button("Start Server"))
                {
                    UnityWSServer.Start();
                }
            }
        }
    }

    [InitializeOnLoad]
    public static class UnityWSServer
    {
        static WebSocketServer _server;
        static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        static readonly object _consoleEntriesLock = new object();
        static readonly List<ConsoleEntry> _consoleEntries = new List<ConsoleEntry>(256);
        const int MaxConsoleEntries = 2000;

        public static bool IsRunning => _server != null && _server.IsListening;

        static UnityWSServer()
        {
            EditorApplication.quitting += Stop;
            EditorApplication.update += ProcessQueue;
            EditorApplication.quitting += DetachConsoleCapture;
            Application.logMessageReceivedThreaded -= CaptureConsoleMessage;
            Application.logMessageReceivedThreaded += CaptureConsoleMessage;
            
            if (UnityMCPSettings.AutoStart)
            {
                Start();
            }
        }

        sealed class ConsoleEntry
        {
            public DateTime TimestampUtc;
            public LogType Type;
            public string Message;
            public string StackTrace;
        }

        public static void Enqueue(Action action)
        {
            _mainThreadQueue.Enqueue(action);
        }

        static void CaptureConsoleMessage(string condition, string stackTrace, LogType type)
        {
            lock (_consoleEntriesLock)
            {
                _consoleEntries.Add(new ConsoleEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    Type = type,
                    Message = condition ?? string.Empty,
                    StackTrace = stackTrace ?? string.Empty,
                });

                if (_consoleEntries.Count > MaxConsoleEntries)
                {
                    int toRemove = _consoleEntries.Count - MaxConsoleEntries;
                    _consoleEntries.RemoveRange(0, toRemove);
                }
            }
        }

        static void DetachConsoleCapture()
        {
            Application.logMessageReceivedThreaded -= CaptureConsoleMessage;
        }

        public static object GetConsoleErrors(int limit, bool includeStackTrace)
        {
            limit = Mathf.Clamp(limit, 1, 500);
            var errors = new JArray();
            int scannedCount;

            lock (_consoleEntriesLock)
            {
                scannedCount = _consoleEntries.Count;
                for (int i = _consoleEntries.Count - 1; i >= 0 && errors.Count < limit; i--)
                {
                    ConsoleEntry entry = _consoleEntries[i];
                    if (!IsErrorType(entry.Type))
                        continue;

                    var row = new JObject
                    {
                        ["timestamp_utc"] = entry.TimestampUtc.ToString("o"),
                        ["type"] = entry.Type.ToString(),
                        ["message"] = entry.Message,
                    };

                    if (includeStackTrace && !string.IsNullOrWhiteSpace(entry.StackTrace))
                        row["stack_trace"] = entry.StackTrace;

                    errors.Add(row);
                }
            }

            return new
            {
                success = true,
                limit,
                include_stack_trace = includeStackTrace,
                scanned_count = scannedCount,
                error_count = errors.Count,
                errors,
            };
        }

        static bool IsErrorType(LogType type)
        {
            return type == LogType.Error ||
                   type == LogType.Assert ||
                   type == LogType.Exception;
        }

        static void ProcessQueue()
        {
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[UnityWSServer] Error in main thread action: {ex}");
                }
            }
        }

        public static void Start()
        {
            if (IsRunning) return;

            try
            {
                Application.runInBackground = true;
                int port = UnityMCPSettings.Port;
                _server = new WebSocketServer(port);
                _server.AddWebSocketService<MCPHandler>("/");
                _server.Start();
                Debug.Log($"[UnityWSServer] Listening on ws://127.0.0.1:{port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityWSServer] Failed to start: {ex}");
            }
        }

        public static void Stop()
        {
            if (_server != null)
            {
                _server.Stop();
                _server = null;
                Debug.Log("[UnityWSServer] Server stopped.");
            }
        }
    }

    public class MCPHandler : WebSocketBehavior
    {
        static readonly JsonSerializerSettings _compact = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
        };

        static readonly object _diagnosticLock = new object();
        static string _lastExceptionDetails;
        static string _lastExceptionCommand;
        static DateTime _lastExceptionAtUtc;

        public static string Compact(object obj) => JsonConvert.SerializeObject(obj, _compact);

        protected override void OnMessage(MessageEventArgs e)
        {
            JObject req;
            try
            {
                req = JObject.Parse(e.Data);
            }
            catch
            {
                Send(Compact(new { success = false, error = "Invalid JSON" }));
                return;
            }

            string cmd = req["cmd"]?.ToString() ?? "";
            int timeoutMs = ClampTimeout(req["timeout_ms"]?.Value<int?>());
            string response = null;
            using var done = new ManualResetEventSlim(false);

            UnityWSServer.Enqueue(() =>
            {
                try
                {
                    object result = DispatchCommand(cmd, req);
                    response = Compact(result);
                }
                catch (Exception ex)
                {
                    RememberException(cmd, ex);
                    response = Compact(new
                    {
                        success = false,
                        cmd,
                        error = ex.Message,
                        error_type = ex.GetType().FullName,
                        exception = ex.ToString(),
                    });
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(timeoutMs))
            {
                response = Compact(BuildTimeoutResponse(cmd, timeoutMs));
            }

            Send(response ?? Compact(new
            {
                success = false,
                cmd,
                error = "No response generated.",
            }));
        }

        object DispatchCommand(string cmd, JObject req)
        {
            return cmd switch
            {
                "ping" => new { success = true, msg = "pong" },
                "play" => HandlePlay(),
                "stop" => HandleStop(),
                "get_console_errors" => HandleGetConsoleErrors(req),
                "describe_actions" => PrefabCommands.DescribeActions(),
                "execute_menu_item" => HandleExecuteMenuItem(req),
                "execute_actions" => HandleExecuteActions(req),
                "preview_actions" => HandlePreviewActions(req),
                "execute_actions_on_prefab_variants" => PrefabCommands.ExecuteActionsOnPrefabVariants(req),
                "inspect_prefab" => PrefabCommands.InspectPrefab(req),
                "get_object_reference" => PrefabCommands.GetObjectReference(req),
                "assert_object_reference" => PrefabCommands.AssertObjectReference(req),
                "search_prefabs" => PrefabCommands.SearchPrefabs(req),
                _ => new { success = false, cmd, error = $"Unknown cmd: {cmd}" },
            };
        }

        object HandleExecuteActions(JObject req)
        {
            var actions = req["actions"] as JArray;
            if (actions == null || actions.Count == 0)
                return new { success = false, error = "actions array required" };

            bool transactional = req["transactional"]?.Value<bool>() ?? false;
            bool rollbackOnFailure = req["rollback_on_failure"]?.Value<bool>() ?? true;
            bool preview = req["preview"]?.Value<bool>() ?? false;
            return PrefabCommands.ExecuteActions(actions, transactional, rollbackOnFailure, preview);
        }

        object HandlePreviewActions(JObject req)
        {
            var actions = req["actions"] as JArray;
            if (actions == null || actions.Count == 0)
                return new { success = false, error = "actions array required" };

            bool rollbackOnFailure = req["rollback_on_failure"]?.Value<bool>() ?? true;
            return PrefabCommands.ExecuteActions(actions, transactional: true, rollbackOnFailure, preview: true);
        }

        object HandleExecuteMenuItem(JObject req)
        {
            string menuPath = req["menu_path"]?.ToString();
            if (string.IsNullOrEmpty(menuPath))
                return new { success = false, error = "menu_path required" };

            bool ok = EditorApplication.ExecuteMenuItem(menuPath);
            return ok
                ? new { success = true }
                : new { success = false, error = $"Menu item not found: {menuPath}" };
        }

        object HandlePlay()
        {
            bool wasPlaying = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            EditorApplication.isPlaying = true;
            return new
            {
                success = true,
                playing = true,
                already_playing = wasPlaying,
            };
        }

        object HandleStop()
        {
            bool wasPlaying = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            EditorApplication.isPlaying = false;
            return new
            {
                success = true,
                playing = false,
                already_stopped = !wasPlaying,
            };
        }

        object HandleGetConsoleErrors(JObject req)
        {
            int limit = req["limit"]?.Value<int>() ?? 50;
            bool includeStackTrace = req["include_stack_trace"]?.Value<bool>() ?? true;
            return UnityWSServer.GetConsoleErrors(limit, includeStackTrace);
        }

        static int ClampTimeout(int? timeoutMs)
        {
            const int defaultTimeout = 15000;
            int value = timeoutMs ?? defaultTimeout;
            if (value < 1000) value = 1000;
            if (value > 300000) value = 300000;
            return value;
        }

        static void RememberException(string cmd, Exception ex)
        {
            lock (_diagnosticLock)
            {
                _lastExceptionCommand = cmd;
                _lastExceptionDetails = ex.ToString();
                _lastExceptionAtUtc = DateTime.UtcNow;
            }
        }

        object BuildTimeoutResponse(string cmd, int timeoutMs)
        {
            string editorLogPath = GetEditorLogPath();
            string editorLogTail = GetEditorLogTail(editorLogPath, 60, 12000);
            string lastException = null;
            string lastExceptionCmd = null;
            double lastExceptionAgeSeconds = -1;

            lock (_diagnosticLock)
            {
                lastException = _lastExceptionDetails;
                lastExceptionCmd = _lastExceptionCommand;
                if (_lastExceptionAtUtc != default)
                {
                    lastExceptionAgeSeconds = (DateTime.UtcNow - _lastExceptionAtUtc).TotalSeconds;
                }
            }

            return new
            {
                success = false,
                cmd,
                error = "Timeout waiting for Unity main thread command completion.",
                timeout_ms = timeoutMs,
                diagnostics = new
                {
                    last_exception_cmd = lastExceptionCmd,
                    last_exception_age_seconds = lastExceptionAgeSeconds,
                    last_exception = lastException,
                    editor_log_path = editorLogPath,
                    editor_log_tail = editorLogTail,
                }
            };
        }

        static string GetEditorLogPath()
        {
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library/Logs/Unity/Editor.log");
            }

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(localAppData, "Unity/Editor/Editor.log");
            }

            string linuxHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(linuxHome, ".config/unity3d/Editor.log");
        }

        static string GetEditorLogTail(string path, int lineCount, int maxChars)
        {
            try
            {
                if (!File.Exists(path))
                    return $"Editor.log not found at {path}";

                var queue = new Queue<string>(lineCount);
                foreach (string line in File.ReadLines(path))
                {
                    if (queue.Count >= lineCount)
                        queue.Dequeue();
                    queue.Enqueue(line);
                }

                string combined = string.Join("\n", queue);
                if (combined.Length > maxChars)
                {
                    combined = combined.Substring(combined.Length - maxChars, maxChars);
                }
                return combined;
            }
            catch (Exception ex)
            {
                return $"Failed to read Editor.log: {ex.Message}";
            }
        }
    }
}
