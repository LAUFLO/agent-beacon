using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("Agent Beacon")]
[assembly: AssemblyDescription("Pixel traffic-light status monitor for coding agents")]
[assembly: AssemblyProduct("Agent Beacon")]
[assembly: AssemblyCompany("Agent Beacon")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyVersion("1.3.6.0")]
[assembly: AssemblyFileVersion("1.3.6.0")]

namespace AgentTrafficLightNative {
  static class State {
    public const string Complete = "complete";
    public const string Running = "running";
    public const string Attention = "attention";
  }

  sealed class AgentTask {
    public string Id, Source, SessionId, Title, Status, Detail, Evidence;
    public long StartedAt, UpdatedAt;
    public bool ExplicitStart, ReliableStart, PendingExec;
  }

  sealed class AgentRuntimeSnapshot {
    public readonly HashSet<string> Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, long> StartedAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<int> CodexUiProcessIds = new HashSet<int>();
    public long CapturedAt;
  }

  sealed class ScanCycle {
    public List<AgentTask> Tasks;
    public AgentRuntimeSnapshot Runtime;
    public int FilesRead;
    public long DurationMs;
    public bool CodexUiAttention;
    public string Error;
  }

  sealed class CacheEntry {
    public long Size, Ticks;
    public List<AgentTask> Tasks;
  }

  sealed class CodexStreamState {
    public long Offset, Size, Ticks; public string Remainder = "", Current, Session;
    public readonly Dictionary<string, AgentTask> Turns = new Dictionary<string, AgentTask>();
    public readonly HashSet<string> PendingAttentionCalls = new HashSet<string>(StringComparer.Ordinal);
    public readonly HashSet<string> PendingExecCalls = new HashSet<string>(StringComparer.Ordinal);
  }

  sealed class SettingsData {
    public int RefreshMs = 1500;
    public int MaxTasks = 80;
    public bool AutoStart = false;
    public bool TaskbarMode = false;
    public int LampScale = 100;
  }

  static class DiagnosticsHub {
    static readonly object Sync = new object();
    static readonly Dictionary<string, string> Lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static long lastScanAt, lastDuration, lastPersistAt; static int lastFiles; static string lastError = "", lastStateSignature = "", lastPersisted = "";
    static readonly string FilePath = DiagnosticPath();
    static string DiagnosticPath() { string configured = Environment.GetEnvironmentVariable("AGENT_BEACON_DIAGNOSTICS_PATH"); return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight", "diagnostics.txt") : configured; }

    public static void Update(List<AgentTask> agents, ScanCycle cycle) {
      lock (Sync) {
        Lines.Clear(); var signature = new StringBuilder();
        foreach (var task in agents) {
          string state = task.Status == State.Running ? "绿/进行中" : task.Status == State.Attention ? "黄/需处理" : "红/空闲或结束";
          string at = task.UpdatedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(task.UpdatedAt).ToLocalTime().ToString("HH:mm:ss") : "--";
          Lines[task.Source] = String.Format("{0}: {1} · {2} · {3} · {4}", task.Source, state, task.Detail ?? "", task.Evidence ?? "未知来源", at);
          signature.Append(task.Source).Append(':').Append(task.Status).Append(':').Append(task.Detail).Append(':').Append(task.Evidence).Append('|');
        }
        lastScanAt = Util.Now(); lastDuration = cycle == null ? 0 : cycle.DurationMs; lastFiles = cycle == null ? 0 : cycle.FilesRead; lastError = cycle == null ? "" : (cycle.Error ?? "");
        string next = signature.ToString(); bool changed = next != lastStateSignature; lastStateSignature = next; Persist(changed);
      }
    }
    public static void RecordError(string error) { lock (Sync) { lastError = error ?? ""; lastScanAt = Util.Now(); Persist(true); } }
    public static string Summary() {
      lock (Sync) {
        var rows = new List<string>(); foreach (string source in new[] { "TRAE", "Codex", "Claude Code", "OpenCode" }) { string line; rows.Add(Lines.TryGetValue(source, out line) ? line : source + ": 未运行"); }
        return String.Join(Environment.NewLine, rows.ToArray());
      }
    }
    public static string Report() {
      lock (Sync) {
        string at = lastScanAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(lastScanAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "--";
        return "Agent Beacon 1.3.6 诊断（不包含聊天正文）" + Environment.NewLine + "扫描: " + at + " · " + lastDuration + "ms · 读取 " + lastFiles + " 个变化文件" + (String.IsNullOrWhiteSpace(lastError) ? "" : Environment.NewLine + "错误: " + lastError) + Environment.NewLine + Summary();
      }
    }
    static void Persist(bool force) { try { long now = Util.Now(); if (!force && lastPersistAt != 0 && now - lastPersistAt < 30000) return; string report = Report(); if (report == lastPersisted && !force) return; Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, report, new UTF8Encoding(false)); lastPersisted = report; lastPersistAt = now; } catch { } }
  }

  static class Util {
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    public static string Home { get {
      string configured = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_HOME"); if (!String.IsNullOrWhiteSpace(configured)) return configured;
      string profile = Environment.GetEnvironmentVariable("USERPROFILE"); if (!String.IsNullOrWhiteSpace(profile) && Directory.Exists(profile)) return profile;
      string drive = Environment.GetEnvironmentVariable("HOMEDRIVE"), path = Environment.GetEnvironmentVariable("HOMEPATH");
      if (!String.IsNullOrWhiteSpace(drive) && !String.IsNullOrWhiteSpace(path) && Directory.Exists(drive + path)) return drive + path;
      return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    } }
    public static string BridgeDir { get { return Path.Combine(Home, ".agent-traffic-light", "events"); } }
    public static string IntegrationDir { get { return Path.Combine(Home, ".agent-traffic-light", "integrations"); } }
    public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    public static string S(IDictionary<string, object> d, string key, string fallback) {
      object value; return d != null && d.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
    }
    public static IDictionary<string, object> D(IDictionary<string, object> d, string key) {
      object value; return d != null && d.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
    }
    public static long N(IDictionary<string, object> d, string key, long fallback) {
      object value; long result; return d != null && d.TryGetValue(key, out value) && value != null && Int64.TryParse(Convert.ToString(value), out result) ? result : fallback;
    }
    public static string Clean(string text, string fallback) {
      if (String.IsNullOrWhiteSpace(text)) return fallback;
      text = Regex.Replace(text, "<recommended_plugins>[\\s\\S]*?</recommended_plugins>", "", RegexOptions.IgnoreCase);
      text = Regex.Replace(text, "<[^>]+>", " ");
      text = Regex.Replace(text, "\\s+", " ").Trim();
      if (text.Length == 0) return fallback;
      return text.Length > 64 ? text.Substring(0, 61) + "…" : text;
    }
    public static long At(string text, long fallback) {
      DateTimeOffset dto; return DateTimeOffset.TryParse(text, out dto) ? dto.ToUnixTimeMilliseconds() : fallback;
    }
    public static IEnumerable<string> Files(string root, Regex name, DateTime cutoff, int max) {
      var found = new List<Tuple<string, DateTime>>();
      var stack = new Stack<string>(); stack.Push(root); int visited = 0;
      while (stack.Count > 0 && visited++ < 600) {
        var dir = stack.Pop();
        try {
          foreach (var child in Directory.GetDirectories(dir)) { string folder = Path.GetFileName(child); if (!Regex.IsMatch(folder, "^(?:Cache|Code Cache|GPUCache|Crashpad|blob_storage|Service Worker|node_modules|CachedData)$", RegexOptions.IgnoreCase)) stack.Push(child); }
          foreach (var file in Directory.GetFiles(dir)) {
            if (!name.IsMatch(Path.GetFileName(file))) continue;
            var modified = File.GetLastWriteTimeUtc(file);
            if (modified >= cutoff && new FileInfo(file).Length > 0) found.Add(Tuple.Create(file, modified));
          }
        } catch { }
      }
      found.Sort(delegate(Tuple<string, DateTime> a, Tuple<string, DateTime> b) { return b.Item2.CompareTo(a.Item2); });
      for (int i = 0; i < Math.Min(max, found.Count); i++) yield return found[i].Item1;
    }
    public static string Tail(string file, int maxBytes) {
      using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
        long start = Math.Max(0, stream.Length - maxBytes); stream.Position = start;
        var buffer = new byte[stream.Length - start]; int read = stream.Read(buffer, 0, buffer.Length);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        if (start > 0) { int nl = text.IndexOf('\n'); if (nl >= 0) text = text.Substring(nl + 1); }
        return text;
      }
    }
  }

  sealed class MonitorEngine {
    readonly Dictionary<string, CacheEntry> cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, CodexStreamState> codexStreams = new Dictionary<string, CodexStreamState>(StringComparer.OrdinalIgnoreCase);
    List<string> codexFiles = new List<string>(), claudeFiles = new List<string>();
    long lastDiscovery;
    readonly Regex jsonl = new Regex("\\.jsonl$", RegexOptions.IgnoreCase);

    public void InvalidateDiscovery() { Interlocked.Exchange(ref lastDiscovery, 0); }

    public List<AgentTask> Scan(out int filesRead) {
      filesRead = 0; Discover();
      var all = new List<AgentTask>();
      foreach (var file in codexFiles) all.AddRange(CachedCodex(file, ref filesRead));
      foreach (var file in claudeFiles) all.AddRange(Cached(file, ParseClaude, ref filesRead));
      all.AddRange(Bridge(ref filesRead));
      all.Sort(delegate(AgentTask a, AgentTask b) {
        int pa = a.Status == State.Attention ? 0 : a.Status == State.Running ? 1 : 2;
        int pb = b.Status == State.Attention ? 0 : b.Status == State.Running ? 1 : 2;
        return pa != pb ? pa.CompareTo(pb) : b.UpdatedAt.CompareTo(a.UpdatedAt);
      });
      return all;
    }

    void Discover() {
      long now = Util.Now(); if (lastDiscovery != 0 && now - lastDiscovery < 30000) return;
      lastDiscovery = now;
      codexFiles = new List<string>(Util.Files(Path.Combine(Util.Home, ".codex", "sessions"), jsonl, DateTime.UtcNow.AddDays(-3), 12));
      claudeFiles = new List<string>(Util.Files(Path.Combine(Util.Home, ".claude", "projects"), jsonl, DateTime.UtcNow.AddDays(-3), 12));
      var keep = new HashSet<string>(codexFiles, StringComparer.OrdinalIgnoreCase); foreach (var file in claudeFiles) keep.Add(file);
      try { if (Directory.Exists(Util.BridgeDir)) foreach (var file in Directory.GetFiles(Util.BridgeDir, "*.json")) keep.Add(file); } catch { }
      foreach (string old in new List<string>(cache.Keys)) if (!keep.Contains(old)) cache.Remove(old);
      foreach (string old in new List<string>(codexStreams.Keys)) if (!keep.Contains(old)) codexStreams.Remove(old);
    }

    delegate List<AgentTask> Parser(string file, long mtime);
    List<AgentTask> Cached(string file, Parser parser, ref int filesRead) {
      try {
        var info = new FileInfo(file); CacheEntry old;
        if (cache.TryGetValue(file, out old) && old.Size == info.Length && old.Ticks == info.LastWriteTimeUtc.Ticks) return old.Tasks;
        var tasks = parser(file, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds()); filesRead++;
        string evidence = parser.Method.Name == "ParseCodex" ? "Codex 会话事件" : parser.Method.Name == "ParseClaude" ? "Claude 会话事件" : "本地事件";
        foreach (var task in tasks) if (String.IsNullOrWhiteSpace(task.Evidence)) task.Evidence = evidence;
        cache[file] = new CacheEntry { Size = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Tasks = tasks }; return tasks;
      } catch { return new List<AgentTask>(); }
    }

    List<AgentTask> CachedCodex(string file, ref int filesRead) {
      try {
        var info = new FileInfo(file); CodexStreamState state;
        if (!codexStreams.TryGetValue(file, out state)) { state = new CodexStreamState(); codexStreams[file] = state; }
        if (state.Ticks == info.LastWriteTimeUtc.Ticks && state.Size == info.Length) { RefreshCodexPendingAttention(state); return new List<AgentTask>(state.Turns.Values); }

        bool reset = state.Ticks == 0 || info.Length < state.Offset || info.Length - state.Offset > 4 * 1024 * 1024;
        long start = reset ? Math.Max(0, info.Length - 512 * 1024) : state.Offset;
        if (reset) {
          state.Offset = start; state.Remainder = ""; state.Current = null; state.Session = Path.GetFileNameWithoutExtension(file); state.Turns.Clear(); state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear();
        }
        string appended = ""; long end;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
          if (start > stream.Length) start = 0;
          stream.Position = start; int count = checked((int)Math.Min(Int32.MaxValue, stream.Length - start)); var buffer = new byte[count]; int used = 0, read;
          while (used < count && (read = stream.Read(buffer, used, count - used)) > 0) used += read;
          end = stream.Position; appended = Encoding.UTF8.GetString(buffer, 0, used);
        }
        if (reset && start > 0) { int first = appended.IndexOf('\n'); appended = first >= 0 ? appended.Substring(first + 1) : ""; }
        string combined = state.Remainder + appended; int cursor = 0, newline;
        while ((newline = combined.IndexOf('\n', cursor)) >= 0) {
          string line = combined.Substring(cursor, newline - cursor).TrimEnd('\r'); cursor = newline + 1;
          if (!String.IsNullOrWhiteSpace(line)) ParseCodexLine(state, line, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
        }
        state.Remainder = cursor < combined.Length ? combined.Substring(cursor) : "";
        if (state.Remainder.Length > 1024 * 1024) state.Remainder = "";
        state.Offset = end; state.Size = end; state.Ticks = info.LastWriteTimeUtc.Ticks; filesRead++;
        RefreshCodexPendingAttention(state);
        TrimCodexTurns(state);
        var result = new List<AgentTask>(state.Turns.Values); foreach (var task in result) task.Evidence = "Codex 增量会话事件"; return result;
      } catch { return new List<AgentTask>(); }
    }

    void TrimCodexTurns(CodexStreamState state) {
      if (state.Turns.Count <= 16) return;
      var removable = new List<AgentTask>(state.Turns.Values); removable.Sort(delegate(AgentTask a, AgentTask b) { return a.UpdatedAt.CompareTo(b.UpdatedAt); });
      foreach (var task in removable) {
        if (state.Turns.Count <= 12) break; if (task.SessionId == state.Current || task.Id.EndsWith(":" + state.Current)) continue;
        string key = null; foreach (var pair in state.Turns) if (Object.ReferenceEquals(pair.Value, task)) { key = pair.Key; break; } if (key != null) state.Turns.Remove(key);
      }
    }

    void RefreshCodexPendingAttention(CodexStreamState state) {
      AgentTask task; if (state.Current == null || !state.Turns.TryGetValue(state.Current, out task) || task.Status == State.Complete) return;
      task.PendingExec = state.PendingExecCalls.Count > 0;
      bool explicitRequest = state.PendingAttentionCalls.Count > 0;
      if (explicitRequest) { task.Status = State.Attention; task.Detail = "等待你的确认或输入"; }
      else if (task.Status == State.Attention) { task.Status = State.Running; task.Detail = "正在执行"; }
    }

    void ParseCodexLine(CodexStreamState state, string line, long mtime) {
      IDictionary<string, object> row; try { row = Util.Json.DeserializeObject(line) as IDictionary<string, object>; } catch { return; } if (row == null) return;
      var payload = Util.D(row, "payload"); string type = Util.S(row, "type", ""); long at = Util.At(Util.S(row, "timestamp", ""), mtime);
      if (type == "session_meta") { state.Session = Util.S(payload, "session_id", Util.S(payload, "id", state.Session)); return; }
      string ptype = Util.S(payload, "type", "");
      if (type == "event_msg" && ptype == "task_started") {
        state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear();
        state.Current = Util.S(payload, "turn_id", Guid.NewGuid().ToString("N"));
        state.Turns[state.Current] = new AgentTask { Id = "codex:" + state.Session + ":" + state.Current, Source = "Codex", SessionId = state.Session, Title = "Codex 任务", Status = State.Running, Detail = "正在执行", StartedAt = at, UpdatedAt = at }; return;
      }
      bool activity = type == "response_item" || (type == "event_msg" && Regex.IsMatch(ptype, "user_message|agent_message|token_count|patch_apply|task_complete|turn_aborted", RegexOptions.IgnoreCase));
      if (state.Current == null && activity) {
        state.Current = "tail-latest:" + at;
        state.Turns[state.Current] = new AgentTask { Id = "codex:" + state.Session + ":" + state.Current, Source = "Codex", SessionId = state.Session, Title = "Codex 任务", Status = State.Running, Detail = "检测到实时活动", StartedAt = at, UpdatedAt = at };
      }
      AgentTask task; if (state.Current == null || !state.Turns.TryGetValue(state.Current, out task)) return;
      task.UpdatedAt = Math.Max(task.UpdatedAt, at);
      if (type == "event_msg" && ptype == "user_message") task.Title = Util.Clean(Util.S(payload, "message", ""), task.Title);
      else if (type == "event_msg" && ptype == "task_complete") { task.Status = State.Complete; task.Detail = "任务已结束"; task.PendingExec = false; state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear(); state.Current = null; }
      else if (type == "event_msg" && ptype == "turn_aborted") { task.Status = State.Complete; task.Detail = "任务已中断"; task.PendingExec = false; state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear(); state.Current = null; }
      else if (type == "response_item" && (ptype == "custom_tool_call" || ptype == "function_call")) {
        string name = Util.S(payload, "name", ""), input = Util.S(payload, "input", Util.S(payload, "arguments", ""));
        string callId = Util.S(payload, "call_id", Util.S(payload, "id", "")); callId = String.IsNullOrWhiteSpace(callId) ? "*" : callId;
        if (String.Equals(name, "exec", StringComparison.OrdinalIgnoreCase)) { state.PendingExecCalls.Add(callId); task.PendingExec = true; }
        bool explicitRequest = Regex.IsMatch(name, "^(?:request_permissions?|request_user_input|elicitation|approval(?:_request)?)$", RegexOptions.IgnoreCase)
          || Regex.IsMatch(input, @"tools\s*\.\s*(?:request_permissions?|request_user_input|elicitation|approval(?:_request)?)\s*\(", RegexOptions.IgnoreCase)
          || (String.Equals(name, "exec", StringComparison.OrdinalIgnoreCase) && CodexInputRequiresEscalation(input));
        if (explicitRequest) {
          state.PendingAttentionCalls.Add(callId);
          RefreshCodexPendingAttention(state);
        }
      } else if (type == "response_item" && (ptype == "custom_tool_call_output" || ptype == "function_call_output")) {
        string callId = Util.S(payload, "call_id", Util.S(payload, "id", ""));
        state.PendingExecCalls.Remove("*"); if (!String.IsNullOrWhiteSpace(callId)) state.PendingExecCalls.Remove(callId); task.PendingExec = state.PendingExecCalls.Count > 0;
        bool resolved = state.PendingAttentionCalls.Remove("*"); if (!String.IsNullOrWhiteSpace(callId)) resolved = state.PendingAttentionCalls.Remove(callId) || resolved;
        if (resolved) { RefreshCodexPendingAttention(state); if (task.Status == State.Running) task.Detail = "已确认，继续执行"; }
      }
    }

    static bool CodexInputRequiresEscalation(string input) {
      if (String.IsNullOrWhiteSpace(input)) return false;
      int cursor = 0;
      while (cursor < input.Length) {
        SkipCodexInputTrivia(input, ref cursor); if (cursor >= input.Length) break;
        string token; if (!ReadCodexInputToken(input, ref cursor, out token)) { cursor++; continue; }
        if (!String.Equals(token, "sandbox_permissions", StringComparison.OrdinalIgnoreCase)) continue;
        SkipCodexInputTrivia(input, ref cursor); if (cursor >= input.Length || input[cursor] != ':') continue;
        cursor++; SkipCodexInputTrivia(input, ref cursor);
        string value; if (ReadCodexInputToken(input, ref cursor, out value) && String.Equals(value, "require_escalated", StringComparison.OrdinalIgnoreCase)) return true;
      }
      return false;
    }

    static void SkipCodexInputTrivia(string input, ref int cursor) {
      while (cursor < input.Length) {
        if (Char.IsWhiteSpace(input[cursor])) { cursor++; continue; }
        if (cursor + 1 < input.Length && input[cursor] == '/' && input[cursor + 1] == '/') {
          cursor += 2; while (cursor < input.Length && input[cursor] != '\r' && input[cursor] != '\n') cursor++; continue;
        }
        if (cursor + 1 < input.Length && input[cursor] == '/' && input[cursor + 1] == '*') {
          cursor += 2; while (cursor + 1 < input.Length && !(input[cursor] == '*' && input[cursor + 1] == '/')) cursor++;
          if (cursor + 1 < input.Length) cursor += 2; continue;
        }
        break;
      }
    }

    static bool ReadCodexInputToken(string input, ref int cursor, out string token) {
      token = null; if (cursor >= input.Length) return false; char first = input[cursor];
      if (first == '\"' || first == '\'' || first == '`') {
        char quote = first; cursor++; var value = new StringBuilder();
        while (cursor < input.Length) {
          char current = input[cursor++];
          if (current == quote) { token = value.ToString(); return true; }
          if (current == '\\' && cursor < input.Length) { value.Append(input[cursor++]); continue; }
          value.Append(current);
        }
        return false;
      }
      if (!(Char.IsLetterOrDigit(first) || first == '_' || first == '$' || first == '-')) return false;
      int start = cursor++; while (cursor < input.Length) { char current = input[cursor]; if (!(Char.IsLetterOrDigit(current) || current == '_' || current == '$' || current == '-')) break; cursor++; }
      token = input.Substring(start, cursor - start); return true;
    }

    List<AgentTask> ParseCodex(string file, long mtime) {
      var state = new CodexStreamState { Session = Path.GetFileNameWithoutExtension(file) }; string recent = Util.Tail(file, 512 * 1024);
      foreach (var line in recent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) if (!String.IsNullOrWhiteSpace(line)) ParseCodexLine(state, line, mtime);
      RefreshCodexPendingAttention(state);
      return new List<AgentTask>(state.Turns.Values);
    }

    List<AgentTask> ParseClaude(string file, long mtime) {
      string session = Path.GetFileNameWithoutExtension(file); AgentTask task = null; string recent = Util.Tail(file, 512 * 1024);
      foreach (var line in recent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
        IDictionary<string, object> row; try { row = Util.Json.DeserializeObject(line) as IDictionary<string, object>; } catch { continue; } if (row == null) continue;
        string type = Util.S(row, "type", ""), raw = line; long at = Util.At(Util.S(row, "timestamp", ""), mtime); string sid = Util.S(row, "sessionId", Util.S(row, "session_id", session)); if (!String.IsNullOrWhiteSpace(sid)) session = sid;
        bool userMessage = type == "user" && raw.IndexOf("tool_result", StringComparison.OrdinalIgnoreCase) < 0;
        bool toolResult = type == "user" && raw.IndexOf("tool_result", StringComparison.OrdinalIgnoreCase) >= 0;
        bool assistant = type == "assistant";
        if (!userMessage && !toolResult && !assistant) continue;
        if (task == null) task = new AgentTask { Id = "claude:" + session, Source = "Claude Code", SessionId = session, Title = "Claude Code 任务", Status = State.Running, Detail = "正在执行", StartedAt = at, UpdatedAt = at };
        task.Id = "claude:" + session; task.SessionId = session; task.UpdatedAt = Math.Max(task.UpdatedAt, at);
        bool manual = Regex.IsMatch(raw, "\\\"name\\\"\\s*:\\s*\\\"(?:AskUserQuestion|request_user_input|PermissionRequest)\\\"|approval[_ -]?(?:required|pending)|waiting[_ -]?(?:for[_ -]?)?(?:user|input|approval)", RegexOptions.IgnoreCase);
        bool complete = assistant && Regex.IsMatch(raw, "\\\"stop_reason\\\"\\s*:\\s*\\\"(?:end_turn|stop_sequence)\\\"", RegexOptions.IgnoreCase);
        bool running = userMessage || toolResult || (assistant && Regex.IsMatch(raw, "\\\"type\\\"\\s*:\\s*\\\"tool_use\\\"|\\\"stop_reason\\\"\\s*:\\s*\\\"tool_use\\\"", RegexOptions.IgnoreCase));
        if (running) { task.Status = State.Running; task.Detail = toolResult ? "工具执行完成，继续处理" : "正在执行"; }
        if (manual) { task.Status = State.Attention; task.Detail = "等待你的确认或输入"; }
        if (complete) { task.Status = State.Complete; task.Detail = "任务已完成"; }
      }
      if (task == null) return new List<AgentTask>(); if (task.Status == State.Running && Util.Now() - task.UpdatedAt > 1800000) { task.Status = State.Complete; task.Detail = "超过 30 分钟无活动，视为空闲"; } return new List<AgentTask> { task };
    }

    List<AgentTask> Bridge(ref int filesRead) {
      var tasks = new List<AgentTask>(); Directory.CreateDirectory(Util.BridgeDir);
      foreach (var file in Directory.GetFiles(Util.BridgeDir, "*.json")) {
        try {
          var info = new FileInfo(file);
          if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-14)) { try { File.Delete(file); } catch { } continue; }
          List<AgentTask> rows = Cached(file, ParseBridge, ref filesRead); tasks.AddRange(rows);
        } catch { }
      }
      return tasks;
    }

    List<AgentTask> ParseBridge(string file, long mtime) {
      var result = new List<AgentTask>(); IDictionary<string, object> row;
      try { row = Util.Json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>; } catch { return result; }
      string source = Util.S(row, "source", ""); if (source != "TRAE" && source != "Claude Code" && source != "OpenCode") return result;
      if (source == "TRAE" && (!String.Equals(Util.S(row, "integration", ""), "mcp", StringComparison.OrdinalIgnoreCase) || !Util.S(row, "id", "").StartsWith("trae-mcp:", StringComparison.OrdinalIgnoreCase))) return result;
      string status = Util.S(row, "status", ""); if (status != State.Running && status != State.Attention && status != State.Complete) return result;
      string sid = source == "OpenCode" ? Util.S(row, "sessionId", "") : Util.S(row, "sessionId", Util.S(row, "id", "")); if (sid.Length == 0) return result;
      if ((source == "OpenCode" || source == "TRAE") && (String.Equals(sid, "opencode-session", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(sid, "^[a-z0-9][a-z0-9_.:-]{2,127}$", RegexOptions.IgnoreCase))) return result;
      long updated = Util.N(row, "updatedAt", mtime);
      string evidence = source == "TRAE" ? "TRAE 本地 MCP 事件" : source == "OpenCode" ? "OpenCode Plugin 事件" : "Claude Hook 事件";
      var task = new AgentTask { Id = Util.S(row, "id", source + ":" + sid), Source = source, SessionId = sid, Title = Util.Clean(Util.S(row, "title", ""), source + " 任务"), Status = status, Detail = Util.Clean(Util.S(row, "detail", ""), status == State.Complete ? "任务已完成" : "正在执行"), Evidence = evidence, StartedAt = Util.N(row, "startedAt", updated), UpdatedAt = updated, ExplicitStart = source == "TRAE" || Util.S(row, "explicitStart", "").Equals("True", StringComparison.OrdinalIgnoreCase), ReliableStart = source == "TRAE" || Util.S(row, "reliableStart", "").Equals("True", StringComparison.OrdinalIgnoreCase) };
      if (source != "TRAE" && task.Status == State.Running && Util.Now() - updated > 1800000) { task.Status = State.Complete; task.Detail = "超过 30 分钟无事件，视为空闲"; }
      result.Add(task); return result;
    }
  }

  sealed class MonitorWatchers : IDisposable {
    readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>(); readonly Action<bool> changed;
    public MonitorWatchers(Action<bool> onChanged) { changed = onChanged; Start(); }
    void Start() {
      var roots = new List<string> {
        Path.Combine(Util.Home, ".codex", "sessions"), Path.Combine(Util.Home, ".claude", "projects"), Util.BridgeDir
      };
      var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (string root in roots) if (unique.Add(root) && Directory.Exists(root)) try {
        var watcher = new FileSystemWatcher(root); watcher.IncludeSubdirectories = true; watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size; watcher.InternalBufferSize = 8192;
        FileSystemEventHandler contentEvent = delegate(object sender, FileSystemEventArgs e) { if (Relevant(e.FullPath)) changed(false); };
        FileSystemEventHandler layoutEvent = delegate(object sender, FileSystemEventArgs e) { if (Relevant(e.FullPath)) changed(true); };
        RenamedEventHandler renameEvent = delegate(object sender, RenamedEventArgs e) { if (Relevant(e.FullPath)) changed(true); };
        watcher.Changed += contentEvent; watcher.Created += layoutEvent; watcher.Deleted += layoutEvent; watcher.Renamed += renameEvent; watcher.Error += delegate { changed(true); }; watcher.EnableRaisingEvents = true; watchers.Add(watcher);
      } catch { }
    }
    static bool Relevant(string path) { string ext = Path.GetExtension(path ?? ""); return Regex.IsMatch(ext, "^\\.(?:json|jsonl|log|txt)$", RegexOptions.IgnoreCase); }
    public void Dispose() { foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); }
  }

  static class AgentProcesses {
    const int ProcessQueryLimitedInformation = 0x1000, ProcessCommandLineInformation = 60;
    [StructLayout(LayoutKind.Sequential)] struct FileTime { public uint Low, High; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int informationLength, out int returnLength);
    static readonly object SnapshotSync = new object(); static AgentRuntimeSnapshot cachedSnapshot;
    public static AgentRuntimeSnapshot Snapshot() {
      var snapshot = new AgentRuntimeSnapshot { CapturedAt = Util.Now() };
      string forced = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_TEST_AGENTS"), forcedFile = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_TEST_AGENTS_FILE");
      if (!String.IsNullOrWhiteSpace(forcedFile)) { try { forced = File.ReadAllText(forcedFile, Encoding.UTF8).Trim(); } catch { forced = ""; } }
      if (forced != null) { foreach (string item in forced.Split(',')) AddCanonical(snapshot.Sources, item.Trim()); foreach (string source in snapshot.Sources) snapshot.StartedAt[source] = snapshot.CapturedAt; return snapshot; }
      lock (SnapshotSync) if (cachedSnapshot != null && snapshot.CapturedAt - cachedSnapshot.CapturedAt < 1500) return cachedSnapshot;
      foreach (var process in Process.GetProcesses()) {
        try {
          string name = process.ProcessName; var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase); MatchName(matched, name);
          string lowerName = (name ?? "").Trim().ToLowerInvariant(); if (lowerName == "chatgpt" || lowerName == "codex") snapshot.CodexUiProcessIds.Add(process.Id);
          if (name.Equals("node", StringComparison.OrdinalIgnoreCase) || name.Equals("bun", StringComparison.OrdinalIgnoreCase)) MatchCommandLine(matched, QueryCommandLine(process.Id));
          if (matched.Count == 0) continue;
          long started = 0; IntPtr probe = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
          if (probe != IntPtr.Zero) try { FileTime creation, exit, kernel, user; if (GetProcessTimes(probe, out creation, out exit, out kernel, out user)) { long ticks = ((long)creation.High << 32) | creation.Low; started = DateTimeOffset.FromFileTime(ticks).ToUnixTimeMilliseconds(); } } finally { CloseHandle(probe); }
          foreach (string source in matched) {
            snapshot.Sources.Add(source); long old; if (started > 0 && (!snapshot.StartedAt.TryGetValue(source, out old) || old == 0 || started < old)) snapshot.StartedAt[source] = started;
          }
        } catch { } finally { process.Dispose(); }
      }
      lock (SnapshotSync) cachedSnapshot = snapshot; return snapshot;
    }
    public static HashSet<string> RunningSources() { return Snapshot().Sources; }
    static void MatchName(HashSet<string> result, string name) {
      string lower = (name ?? "").Trim().ToLowerInvariant(); if (lower.Length == 0 || lower.Contains("agent-traffic-light")) return;
      if (lower == "codex" || lower.StartsWith("codex.")) result.Add("Codex");
      if (lower == "trae" || lower.StartsWith("trae ") || lower.StartsWith("trae-") || lower.Contains("trae solo")) result.Add("TRAE");
      if (lower == "claude" || lower.Contains("claude-code")) result.Add("Claude Code");
      if (lower == "opencode" || lower.StartsWith("opencode-")) result.Add("OpenCode");
    }
    static void MatchCommandLine(HashSet<string> result, string commandLine) { string cmd = commandLine ?? ""; if (Regex.IsMatch(cmd, "@anthropic-ai[\\\\/]claude-code|claude-code[\\\\/](?:cli|bin)|[\\\\/]claude(?:\\.js)?(?:\"|\\s|$)", RegexOptions.IgnoreCase)) result.Add("Claude Code"); if (Regex.IsMatch(cmd, "(?:^|[\\\\/\\s])opencode(?:\\.cmd|\\.exe|\\.js)?(?:\\s|$)|@opencode-ai[\\\\/]", RegexOptions.IgnoreCase)) result.Add("OpenCode"); }
    static string QueryCommandLine(int processId) {
      IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId); if (process == IntPtr.Zero) return "";
      try {
        int needed; NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out needed); if (needed <= 0 || needed > 1024 * 1024) return "";
        IntPtr buffer = Marshal.AllocHGlobal(needed); try { if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, needed, out needed) != 0) return ""; int length = Marshal.ReadInt16(buffer); IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4); return text == IntPtr.Zero || length <= 0 ? "" : Marshal.PtrToStringUni(text, length / 2); } finally { Marshal.FreeHGlobal(buffer); }
      } finally { CloseHandle(process); }
    }
    static int QueryParent(int processId) {
      IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId); if (process == IntPtr.Zero) return 0;
      try { int returned; IntPtr buffer = Marshal.AllocHGlobal(IntPtr.Size * 6); try { if (NtQueryInformationProcess(process, 0, buffer, IntPtr.Size * 6, out returned) != 0) return 0; return Marshal.ReadIntPtr(buffer, IntPtr.Size * 5).ToInt32(); } finally { Marshal.FreeHGlobal(buffer); } } finally { CloseHandle(process); }
    }
    public static bool ClaudeHasActiveToolProcess(long since) {
      var parents = new Dictionary<int, int>(); var names = new Dictionary<int, string>(); var started = new Dictionary<int, long>(); var commands = new Dictionary<int, string>(); var claude = new HashSet<int>();
      foreach (var process in Process.GetProcesses()) { try { int id = process.Id; string name = process.ProcessName; parents[id] = QueryParent(id); names[id] = name; try { started[id] = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds(); } catch { started[id] = 0; } if (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.IndexOf("claude-code", StringComparison.OrdinalIgnoreCase) >= 0) claude.Add(id); } catch { } finally { process.Dispose(); } }
      if (claude.Count == 0) return false;
      foreach (int id in new List<int>(parents.Keys)) {
        string name; if (!names.TryGetValue(id, out name) || name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.IndexOf("conhost", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("crashpad", StringComparison.OrdinalIgnoreCase) >= 0) continue;
        long at; if (!started.TryGetValue(id, out at) || at < since - 3000) continue; int ancestor = id;
        for (int depth = 0; depth < 6; depth++) { int parent; if (!parents.TryGetValue(ancestor, out parent) || parent == 0) break; if (claude.Contains(parent)) { string command; if (!commands.TryGetValue(id, out command)) { command = QueryCommandLine(id); commands[id] = command; } if (!Regex.IsMatch(command ?? "", "playwright-mcp|chrome-devtools-mcp|alibabacloud-devops-mcp-server", RegexOptions.IgnoreCase)) return true; break; } ancestor = parent; }
      }
      return false;
    }
    static class CodexAutomationConditions {
      public static readonly Condition Buttons = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
      public static readonly Condition Prompts = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));
    }
    public static bool IsCodexApprovalPromptText(string text) {
      string value = (text ?? "").Trim(); if (value.Length == 0 || value.Length > 240) return false;
      bool chineseApproval = value.IndexOf("允许", StringComparison.Ordinal) >= 0
        && (value.IndexOf("ChatGPT", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0)
        && Regex.IsMatch(value, "编辑|运行|执行|访问");
      return chineseApproval || Regex.IsMatch(value, "需要(?:你|您).{0,12}(?:确认|批准|选择)|do you want to allow|approval required|requires your approval", RegexOptions.IgnoreCase);
    }
    public static bool IsCodexApprovalActionText(string text) {
      string value = (text ?? "").Trim();
      return Regex.IsMatch(value, "^(?:允许一次|本次允许|始终允许|允许|批准|拒绝|不允许|取消|allow once|allow this time|always allow|approve|deny|reject|cancel)(?:\\s*(?:[⌄▼▾˅v]|展开|更多选项|菜单|menu))*$", RegexOptions.IgnoreCase);
    }
    static bool CodexAutomationRootNeedsAttention(AutomationElement root) {
      if (root == null) return false; bool allow = false;
      var buttons = root.FindAll(TreeScope.Descendants, CodexAutomationConditions.Buttons); int buttonLimit = Math.Min(buttons.Count, 200);
      for (int i = 0; i < buttonLimit; i++) try {
        var current = buttons[i].Current; if (current.IsOffscreen) continue;
        if (IsCodexApprovalActionText(current.Name) && !Regex.IsMatch(current.Name ?? "", "拒绝|不允许|取消|deny|reject|cancel", RegexOptions.IgnoreCase)) { allow = true; break; }
      } catch { }
      if (!allow) return false;
      var prompts = root.FindAll(TreeScope.Descendants, CodexAutomationConditions.Prompts); int promptLimit = Math.Min(prompts.Count, 600);
      for (int i = 0; i < promptLimit; i++) try {
        var current = prompts[i].Current; if (current.IsOffscreen) continue;
        if (IsCodexApprovalPromptText(String.Join(" ", new[] { current.Name ?? "", current.HelpText ?? "", current.ItemStatus ?? "" }))) return true;
      } catch { }
      return false;
    }
    static long nextCodexUiScan;
    public static bool CodexNeedsUserAttention(AgentRuntimeSnapshot snapshot) {
      long now = Util.Now(); if (snapshot == null || snapshot.CodexUiProcessIds.Count == 0 || now < Interlocked.Read(ref nextCodexUiScan)) return false;
      var conditions = new List<Condition>(); var directRoots = new List<AutomationElement>();
      foreach (int processId in snapshot.CodexUiProcessIds) {
        Process process = null;
        try {
          process = Process.GetProcessById(processId); conditions.Add(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
          if (process.MainWindowHandle != IntPtr.Zero) { var direct = AutomationElement.FromHandle(process.MainWindowHandle); if (direct != null) directRoots.Add(direct); }
        } catch { } finally { if (process != null) process.Dispose(); }
      }
      foreach (AutomationElement root in directRoots) try { if (CodexAutomationRootNeedsAttention(root)) { Interlocked.Exchange(ref nextCodexUiScan, now + 500); return true; } } catch { }
      if (conditions.Count > 0) try {
        Condition windowCondition = conditions.Count == 1 ? conditions[0] : new OrCondition(conditions.ToArray());
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, windowCondition);
        for (int i = 0; i < windows.Count; i++) try { if (CodexAutomationRootNeedsAttention(windows[i])) { Interlocked.Exchange(ref nextCodexUiScan, now + 500); return true; } } catch { }
      } catch { }
      Interlocked.Exchange(ref nextCodexUiScan, now + 1000); return false;
    }
    static void AddCanonical(HashSet<string> result, string name) {
      if (name.Equals("trae", StringComparison.OrdinalIgnoreCase)) result.Add("TRAE");
      else if (name.Equals("codex", StringComparison.OrdinalIgnoreCase)) result.Add("Codex");
      else if (name.Equals("claude", StringComparison.OrdinalIgnoreCase) || name.Equals("claude code", StringComparison.OrdinalIgnoreCase)) result.Add("Claude Code");
      else if (name.Equals("opencode", StringComparison.OrdinalIgnoreCase)) result.Add("OpenCode");
    }
  }

  static class AgentStateRules {
    public static AgentTask LatestForSource(string source, List<AgentTask> sourceTasks) {
      if (sourceTasks == null || sourceTasks.Count == 0) return null;
      sourceTasks.Sort(delegate(AgentTask a, AgentTask b) { return b.UpdatedAt.CompareTo(a.UpdatedAt); });
      long newestAt = sourceTasks[0].UpdatedAt;
      var simultaneous = sourceTasks.FindAll(delegate(AgentTask t) { return newestAt - t.UpdatedAt <= 1000; });
      AgentTask selected = simultaneous.Find(delegate(AgentTask t) { return t.Status == State.Attention; });
      if (selected == null) selected = simultaneous.Find(delegate(AgentTask t) { return t.UpdatedAt == newestAt; });
      if (selected == null) selected = sourceTasks[0];
      var result = Clone(selected); result.Source = source; result.UpdatedAt = newestAt; return result;
    }
    public static AgentTask ResolveForRuntime(string source, AgentTask candidate, long runtimeStartedAt, long sourceSeenAt, AgentTask previous) {
      if (candidate == null) return previous != null && (previous.Id ?? "").StartsWith("idle:", StringComparison.OrdinalIgnoreCase) ? Clone(previous) : Idle(source);
      bool traeMcp = source == "TRAE" && (candidate.Id ?? "").StartsWith("trae-mcp:", StringComparison.OrdinalIgnoreCase);
      if (traeMcp) {
        if (previous != null && previous.Status == State.Complete && candidate.Status == State.Running) {
          bool newTask = !String.Equals(previous.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) && candidate.UpdatedAt > previous.UpdatedAt;
          if (!newTask) return Clone(previous);
        }
        if (previous != null && previous.Status == State.Attention && candidate.Status == State.Running && candidate.UpdatedAt <= previous.UpdatedAt) return Clone(previous);
        return Clone(candidate);
      }
      if (previous != null && previous.Id == candidate.Id && previous.Status == State.Complete && candidate.Status != State.Complete && candidate.UpdatedAt <= previous.UpdatedAt) return Clone(previous);
      if (previous != null && previous.Id == candidate.Id && previous.Status == State.Attention && candidate.Status == State.Running && candidate.UpdatedAt <= previous.UpdatedAt) return Clone(previous);
      long baseline = runtimeStartedAt > 0 ? runtimeStartedAt : sourceSeenAt;
      if (candidate.Status == State.Running && baseline > 0 && candidate.UpdatedAt < baseline - 5000) return previous != null && (previous.Id ?? "").StartsWith("idle:", StringComparison.OrdinalIgnoreCase) ? Clone(previous) : Idle(source);
      return Clone(candidate);
    }
    public static AgentTask Idle(string source) { long now = Util.Now(); return new AgentTask { Id = "idle:" + source, Source = source, SessionId = "", Title = source, Status = State.Complete, Detail = "Agent 已启动，等待新任务", Evidence = "进程检测 · 未发现本次启动后的任务事件", StartedAt = now, UpdatedAt = now }; }
    public static AgentTask Clone(AgentTask task) { return task == null ? null : new AgentTask { Id = task.Id, Source = task.Source, SessionId = task.SessionId, Title = task.Title, Status = task.Status, Detail = task.Detail, Evidence = task.Evidence, StartedAt = task.StartedAt, UpdatedAt = task.UpdatedAt, ExplicitStart = task.ExplicitStart, ReliableStart = task.ReliableStart, PendingExec = task.PendingExec }; }
  }

  sealed class PixelPoleControl : Control {
    public static readonly Color KeyColor = Color.Fuchsia;
    readonly List<AgentTask> agents = new List<AgentTask>();
    readonly System.Windows.Forms.Timer blinkTimer = new System.Windows.Forms.Timer(); bool blinkOn = true; float scaleFactor = 1f;
    public float ScaleFactor { get { return scaleFactor; } set { scaleFactor = Math.Max(1f, Math.Min(2f, value)); Invalidate(); } }
    public Rectangle SettingsRect, CloseRect;
    public event EventHandler SettingsClicked, CloseClicked;
    public PixelPoleControl() { Dock = DockStyle.Fill; BackColor = KeyColor; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); blinkTimer.Interval = 500; blinkTimer.Tick += delegate { blinkOn = !blinkOn; Invalidate(); }; }
    public void SetAgents(List<AgentTask> value) { agents.Clear(); agents.AddRange(value); bool attention = agents.Exists(delegate(AgentTask task) { return task.Status == State.Attention; }); if (attention) blinkTimer.Start(); else { blinkTimer.Stop(); blinkOn = true; } Invalidate(); }
    Point ToLogical(Point point) { return new Point((int)Math.Round(point.X * 4f / scaleFactor), (int)Math.Round(point.Y * 4f / scaleFactor)); }
    public bool IsButton(Point point) { Point logical = ToLogical(point); return SettingsRect.Contains(logical) || CloseRect.Contains(logical); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = IsButton(e.Location) ? Cursors.Hand : Cursors.SizeAll; }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); Point logical = ToLogical(e.Location); if (SettingsRect.Contains(logical) && SettingsClicked != null) SettingsClicked(this, EventArgs.Empty); else if (CloseRect.Contains(logical) && CloseClicked != null) CloseClicked(this, EventArgs.Empty); }
    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half; g.Clear(KeyColor); g.ScaleTransform(0.25f * scaleFactor, 0.25f * scaleFactor);
      int center = (int)Math.Round(Width * 2f / scaleFactor), headY = 8, barY = 168, baseY = (int)Math.Round(Height * 4f / scaleFactor) - 18;
      if (agents.Count == 0) {
        DrawPole(g, new Point(center, 70), new Point(center, baseY)); DrawLoginDisplay(g, center, headY); DrawBase(g, center, baseY); DrawControls(g, center, baseY - 92); return;
      }
      int count = Math.Min(4, agents.Count);
      int[] xs = HeadCenters(count, center);
      if (count == 1) DrawPole(g, new Point(center, headY + 122), new Point(center, baseY));
      else {
        DrawPole(g, new Point(xs[0], barY), new Point(xs[count - 1], barY));
        for (int i = 0; i < count; i++) DrawPole(g, new Point(xs[i], headY + 122), new Point(xs[i], barY));
        DrawPole(g, new Point(center, barY), new Point(center, baseY));
      }
      for (int i = 0; i < count; i++) DrawHead(g, xs[i] - 27, headY, agents[i].Status);
      DrawBase(g, center, baseY); DrawControls(g, center, baseY - 92);
    }
    int[] HeadCenters(int count, int center) {
      if (count == 1) return new[] { center };
      if (count == 2) return new[] { center - 50, center + 50 };
      if (count == 3) return new[] { center - 78, center, center + 78 };
      return new[] { center - 102, center - 34, center + 34, center + 102 };
    }
    void DrawPole(Graphics g, Point from, Point to) {
      using (var border = new Pen(Color.FromArgb(8, 11, 17), 18)) { border.StartCap = System.Drawing.Drawing2D.LineCap.Square; border.EndCap = System.Drawing.Drawing2D.LineCap.Square; g.DrawLine(border, from, to); }
      using (var fill = new Pen(Color.FromArgb(42, 50, 62), 12)) { fill.StartCap = System.Drawing.Drawing2D.LineCap.Square; fill.EndCap = System.Drawing.Drawing2D.LineCap.Square; g.DrawLine(fill, from, to); }
      using (var shine = new Pen(Color.FromArgb(71, 82, 98), 3)) {
        if (from.Y == to.Y) g.DrawLine(shine, from.X, from.Y - 3, to.X, to.Y - 3);
        else g.DrawLine(shine, from.X - 3, from.Y, to.X - 3, to.Y);
      }
    }
    void DrawHead(Graphics g, int x, int y, string status) {
      Point[] body = { new Point(x + 7, y), new Point(x + 47, y), new Point(x + 54, y + 7), new Point(x + 54, y + 115), new Point(x + 47, y + 122), new Point(x + 7, y + 122), new Point(x, y + 115), new Point(x, y + 7) };
      using (var edge = new SolidBrush(Color.FromArgb(7, 10, 16))) g.FillPolygon(edge, body);
      Point[] inner = { new Point(x + 9, y + 5), new Point(x + 45, y + 5), new Point(x + 49, y + 9), new Point(x + 49, y + 113), new Point(x + 44, y + 117), new Point(x + 10, y + 117), new Point(x + 5, y + 112), new Point(x + 5, y + 10) };
      using (var shell = new SolidBrush(Color.FromArgb(47, 56, 68))) g.FillPolygon(shell, inner);
      using (var highlight = new SolidBrush(Color.FromArgb(79, 91, 108))) g.FillRectangle(highlight, x + 9, y + 7, 4, 106);
      DrawLamp(g, x + 14, y + 12, Color.FromArgb(255, 48, 60), status == State.Complete);
      DrawLamp(g, x + 14, y + 46, Color.FromArgb(255, 205, 32), status == State.Attention && blinkOn);
      DrawLamp(g, x + 14, y + 80, Color.FromArgb(38, 231, 103), status == State.Running);
    }
    void DrawLamp(Graphics g, int x, int y, Color color, bool active) {
      Point[] lens = { new Point(x + 8, y), new Point(x + 20, y), new Point(x + 28, y + 8), new Point(x + 28, y + 20), new Point(x + 20, y + 28), new Point(x + 8, y + 28), new Point(x, y + 20), new Point(x, y + 8) };
      using (var rim = new SolidBrush(Color.FromArgb(5, 7, 11))) g.FillPolygon(rim, lens);
      Point[] face = { new Point(x + 9, y + 4), new Point(x + 19, y + 4), new Point(x + 24, y + 9), new Point(x + 24, y + 19), new Point(x + 19, y + 24), new Point(x + 9, y + 24), new Point(x + 4, y + 19), new Point(x + 4, y + 9) };
      Color faceColor = active ? color : Color.FromArgb(20 + color.R / 14, 22 + color.G / 18, 24 + color.B / 14);
      using (var brush = new SolidBrush(faceColor)) g.FillPolygon(brush, face);
      if (active) { using (var shine = new SolidBrush(Color.White)) g.FillRectangle(shine, x + 8, y + 7, 5, 5); using (var glow = new Pen(Color.FromArgb(185, color), 2)) g.DrawPolygon(glow, face); }
    }
    void DrawControls(Graphics g, int center, int y) {
      SettingsRect = new Rectangle(center - 18, y, 36, 34); CloseRect = new Rectangle(center - 18, y + 38, 36, 34);
      DrawButton(g, SettingsRect, false); DrawButton(g, CloseRect, true);
      int cx = SettingsRect.Left + 18, cy = SettingsRect.Top + 17;
      using (var gear = new SolidBrush(Color.FromArgb(184, 194, 207))) { g.FillRectangle(gear, cx - 3, cy - 11, 6, 22); g.FillRectangle(gear, cx - 11, cy - 3, 22, 6); g.FillRectangle(gear, cx - 8, cy - 8, 16, 16); }
      using (var hole = new SolidBrush(Color.FromArgb(38, 46, 57))) g.FillRectangle(hole, cx - 3, cy - 3, 6, 6);
      using (var cross = new Pen(Color.FromArgb(255, 82, 82), 4)) { g.DrawLine(cross, CloseRect.Left + 10, CloseRect.Top + 9, CloseRect.Right - 10, CloseRect.Bottom - 9); g.DrawLine(cross, CloseRect.Right - 10, CloseRect.Top + 9, CloseRect.Left + 10, CloseRect.Bottom - 9); }
    }
    void DrawButton(Graphics g, Rectangle rect, bool close) { using (var border = new SolidBrush(Color.FromArgb(7, 10, 16))) g.FillRectangle(border, rect); using (var fill = new SolidBrush(Color.FromArgb(45, 54, 66))) g.FillRectangle(fill, rect.Left + 4, rect.Top + 4, rect.Width - 8, rect.Height - 8); using (var top = new SolidBrush(Color.FromArgb(82, 94, 111))) g.FillRectangle(top, rect.Left + 6, rect.Top + 6, rect.Width - 12, 3); }
    void DrawBase(Graphics g, int center, int y) { using (var dark = new SolidBrush(Color.FromArgb(7, 10, 16))) g.FillRectangle(dark, center - 43, y - 2, 86, 16); using (var fill = new SolidBrush(Color.FromArgb(45, 54, 66))) g.FillRectangle(fill, center - 36, y + 2, 72, 8); using (var shine = new SolidBrush(Color.FromArgb(80, 92, 108))) g.FillRectangle(shine, center - 30, y + 2, 60, 3); }
    void DrawLoginDisplay(Graphics g, int center, int y) {
      using (var edge = new SolidBrush(Color.FromArgb(6, 9, 14))) g.FillRectangle(edge, center - 70, y, 140, 62);
      using (var shell = new SolidBrush(Color.FromArgb(47, 56, 68))) g.FillRectangle(shell, center - 64, y + 6, 128, 50);
      using (var screen = new SolidBrush(Color.FromArgb(7, 22, 19))) g.FillRectangle(screen, center - 58, y + 12, 116, 38);
      DrawPixelLogin(g, center - 62, y + 20);
    }
    void DrawPixelLogin(Graphics g, int x, int y) {
      const string text = "LOGIN..."; int cursor = x;
      using (var brush = new SolidBrush(Color.FromArgb(67, 255, 155))) foreach (char c in text) {
        string[] pattern = PixelPattern(c); for (int row = 0; row < pattern.Length; row++) for (int col = 0; col < pattern[row].Length; col++) if (pattern[row][col] == '1') g.FillRectangle(brush, cursor + col * 4, y + row * 4, 4, 4);
        cursor += 16;
      }
    }
    string[] PixelPattern(char c) {
      if (c == 'L') return new[] { "100", "100", "100", "100", "111" };
      if (c == 'O') return new[] { "111", "101", "101", "101", "111" };
      if (c == 'G') return new[] { "111", "100", "101", "101", "111" };
      if (c == 'I') return new[] { "111", "010", "010", "010", "111" };
      if (c == 'N') return new[] { "101", "111", "111", "111", "101" };
      return new[] { "000", "000", "000", "000", "010" };
    }
    protected override void Dispose(bool disposing) { if (disposing) blinkTimer.Dispose(); base.Dispose(disposing); }
  }

  sealed class PixelButton : Control {
    bool hover, pressed, active, danger;
    public bool Active { get { return active; } set { active = value; Invalidate(); } }
    public bool Danger { get { return danger; } set { danger = value; Invalidate(); } }
    public PixelButton() {
      SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
      Cursor = Cursors.Hand; TabStop = true; Font = new Font("Microsoft YaHei UI", 8, FontStyle.Bold); ForeColor = Color.FromArgb(226, 232, 240);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
      Color border = danger && hover ? Color.FromArgb(255, 56, 72) : active ? Color.FromArgb(67, 255, 155) : hover ? Color.FromArgb(96, 165, 250) : Color.FromArgb(71, 85, 105);
      Color fill = pressed ? Color.FromArgb(7, 10, 16) : active ? Color.FromArgb(14, 57, 43) : danger && hover ? Color.FromArgb(71, 23, 31) : hover ? Color.FromArgb(25, 42, 66) : Color.FromArgb(20, 29, 43);
      using (var black = new SolidBrush(Color.FromArgb(5, 8, 13))) g.FillRectangle(black, ClientRectangle);
      using (var edge = new SolidBrush(border)) g.FillRectangle(edge, 2, 2, Width - 4, Height - 4);
      using (var body = new SolidBrush(fill)) g.FillRectangle(body, 4, 4, Width - 8, Height - 8);
      using (var shine = new SolidBrush(Color.FromArgb(45, 55, 72))) g.FillRectangle(shine, 6, 6, Math.Max(0, Width - 12), 2);
      TextRenderer.DrawText(g, Text, Font, new Rectangle(5, 4, Width - 10, Height - 8), ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
      if (Focused) using (var focus = new Pen(Color.FromArgb(148, 163, 184))) g.DrawRectangle(focus, 6, 6, Width - 13, Height - 13);
    }
  }

  sealed class PixelToggle : Control {
    bool isChecked, hover;
    public bool Checked { get { return isChecked; } set { if (isChecked == value) return; isChecked = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
    public event EventHandler CheckedChanged;
    public PixelToggle() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true); Cursor = Cursors.Hand; TabStop = true; Height = 24; Font = new Font("Microsoft YaHei UI", 9); ForeColor = Color.FromArgb(226, 232, 240); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
      using (var outer = new SolidBrush(Color.FromArgb(5, 8, 13))) g.FillRectangle(outer, 0, 3, 18, 18);
      using (var edge = new SolidBrush(hover ? Color.FromArgb(96, 165, 250) : Color.FromArgb(71, 85, 105))) g.FillRectangle(edge, 2, 5, 14, 14);
      using (var well = new SolidBrush(Color.FromArgb(12, 18, 28))) g.FillRectangle(well, 4, 7, 10, 10);
      if (Checked) using (var on = new SolidBrush(Color.FromArgb(67, 255, 155))) { g.FillRectangle(on, 6, 9, 6, 6); g.FillRectangle(on, 8, 7, 2, 10); }
      TextRenderer.DrawText(g, Text, Font, new Rectangle(28, 0, Width - 28, Height), ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
      if (Focused) using (var focus = new Pen(Color.FromArgb(100, 116, 139))) g.DrawRectangle(focus, 25, 2, Width - 27, Height - 5);
    }
  }

  sealed class SettingsForm : Form {
    bool dragging; Point dragOrigin;
    public SettingsForm(SettingsData settings, Action<int> intervalChanged, Action<bool> modeChanged, Action<int> scaleChanged) {
      Text = "Agent Beacon 设置"; ClientSize = new Size(430, 640); FormBorderStyle = FormBorderStyle.None; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(10, 15, 25); ForeColor = Color.White; Font = new Font("Microsoft YaHei UI", 9); AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;
      var title = new Label { Text = "AGENT BEACON // 设置", AutoSize = false, Location = new Point(52, 12), Size = new Size(290, 24), ForeColor = Color.FromArgb(226, 232, 240), BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }; Controls.Add(title);
      var close = new PixelButton { Text = "X", Location = new Point(389, 9), Size = new Size(28, 27), Danger = true }; close.Click += delegate { Close(); }; Controls.Add(close);
      var auto = new PixelToggle { Text = "开机自启动", Checked = settings.AutoStart, Location = new Point(22, 57), Width = 380 }; auto.CheckedChanged += delegate { settings.AutoStart = auto.Checked; Program.SetAutoStart(auto.Checked); Program.SaveSettings(settings); }; Controls.Add(auto);
      var compact = new PixelToggle { Text = "任务栏紧凑模式（只显示红绿灯和短灯杆）", Checked = settings.TaskbarMode, Location = new Point(22, 88), Width = 390 }; compact.CheckedChanged += delegate { settings.TaskbarMode = compact.Checked; Program.SaveSettings(settings); modeChanged(settings.TaskbarMode); }; Controls.Add(compact);
      Controls.Add(new Label { Text = "刷新频率", AutoSize = false, Location = new Point(22, 126), Size = new Size(72, 26), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(148, 163, 184), BackColor = Color.Transparent });
      int[] values = { 1500, 2000, 3000, 5000, 10000 }; string[] labels = { "1.5S", "2S", "3S", "5S", "10S" }; var intervalButtons = new List<PixelButton>();
      for (int i = 0; i < values.Length; i++) {
        int index = i; var choice = new PixelButton { Text = labels[i], Location = new Point(100 + i * 61, 124), Size = new Size(55, 30), Active = settings.RefreshMs == values[i] }; intervalButtons.Add(choice);
        choice.Click += delegate { settings.RefreshMs = values[index]; foreach (var item in intervalButtons) item.Active = false; choice.Active = true; Program.SaveSettings(settings); intervalChanged(settings.RefreshMs); }; Controls.Add(choice);
      }
      Controls.Add(new Label { Text = "桌面灯大小", AutoSize = false, Location = new Point(22, 177), Size = new Size(72, 26), TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(148, 163, 184), BackColor = Color.Transparent });
      int[] scales = { 100, 150, 200 }; string[] scaleLabels = { "小 1X", "中 1.5X", "大 2X" }; var scaleButtons = new List<PixelButton>();
      for (int i = 0; i < scales.Length; i++) {
        int index = i; var choice = new PixelButton { Text = scaleLabels[i], Location = new Point(100 + i * 91, 175), Size = new Size(83, 30), Active = settings.LampScale == scales[i] }; scaleButtons.Add(choice);
        choice.Click += delegate { settings.LampScale = scales[index]; foreach (var item in scaleButtons) item.Active = false; choice.Active = true; Program.SaveSettings(settings); scaleChanged(settings.LampScale); }; Controls.Add(choice);
      }
      var traeState = StatusLabel(282, 243); var ruleState = StatusLabel(282, 289); var claudeState = StatusLabel(282, 335); var openCodeState = StatusLabel(282, 381); Controls.Add(traeState); Controls.Add(ruleState); Controls.Add(claudeState); Controls.Add(openCodeState);
      Action refresh = delegate { SetTraeStatus(traeState); ruleState.Text = "■ 粘贴到全局规则"; ruleState.ForeColor = Color.FromArgb(148, 163, 184); SetStatus(claudeState, Integration.IsClaudeInstalled()); SetStatus(openCodeState, Integration.IsOpenCodeInstalled()); }; refresh();
      var trae = new PixelButton { Text = "复制 TRAE MCP 配置", Location = new Point(22, 232), Size = new Size(238, 36) }; trae.Click += delegate { string message = Integration.InstallTraeMcp(); refresh(); MessageBox.Show(message, "Agent Beacon"); }; Controls.Add(trae);
      var rule = new PixelButton { Text = "复制 TRAE 状态规则", Location = new Point(22, 278), Size = new Size(238, 36) }; rule.Click += delegate { string message = Integration.CopyTraeRule(); MessageBox.Show(message, "Agent Beacon"); }; Controls.Add(rule);
      var claude = new PixelButton { Text = "安装 / 更新 CLAUDE HOOKS", Location = new Point(22, 324), Size = new Size(238, 36) }; claude.Click += delegate { string message = Integration.InstallClaude(); refresh(); MessageBox.Show(message, "Agent Beacon"); }; Controls.Add(claude);
      var openCode = new PixelButton { Text = "安装 / 更新 OPENCODE 插件", Location = new Point(22, 370), Size = new Size(238, 36) }; openCode.Click += delegate { string message = Integration.InstallOpenCode(); refresh(); MessageBox.Show(message, "Agent Beacon"); }; Controls.Add(openCode);
      Controls.Add(new Label { Text = "实时状态诊断（不显示聊天正文）", AutoSize = false, Location = new Point(22, 425), Size = new Size(260, 22), ForeColor = Color.FromArgb(148, 163, 184), BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold) });
      var diagnostic = new Label { Text = DiagnosticsHub.Summary(), AutoSize = false, Location = new Point(22, 449), Size = new Size(386, 80), ForeColor = Color.FromArgb(203, 213, 225), BackColor = Color.FromArgb(12, 18, 28), BorderStyle = BorderStyle.FixedSingle, Font = new Font("Microsoft YaHei UI", 8), Padding = new Padding(7, 5, 5, 3) }; Controls.Add(diagnostic);
      var refreshDiagnostic = new PixelButton { Text = "刷新诊断", Location = new Point(22, 539), Size = new Size(112, 32) }; refreshDiagnostic.Click += delegate { diagnostic.Text = DiagnosticsHub.Summary(); refresh(); }; Controls.Add(refreshDiagnostic);
      var copyDiagnostic = new PixelButton { Text = "复制诊断", Location = new Point(146, 539), Size = new Size(112, 32) }; copyDiagnostic.Click += delegate { try { Clipboard.SetText(DiagnosticsHub.Report()); copyDiagnostic.Text = "已复制"; } catch { copyDiagnostic.Text = "复制失败"; } }; Controls.Add(copyDiagnostic);
      Controls.Add(new Label { Text = "TRAE MCP 需在 TRAE 设置中粘贴配置和全局规则 · 任务栏图标大小由 WINDOWS 固定", AutoSize = false, Location = new Point(22, 586), Size = new Size(390, 38), ForeColor = Color.FromArgb(100, 116, 139), BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 8) });
      MouseDown += BeginDrag; MouseMove += ContinueDrag; MouseUp += EndDrag; title.MouseDown += BeginDrag; title.MouseMove += ContinueDrag; title.MouseUp += EndDrag;
    }
    void BeginDrag(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { dragging = true; dragOrigin = Cursor.Position; } }
    void ContinueDrag(object sender, MouseEventArgs e) { if (!dragging) return; Point now = Cursor.Position; Location = new Point(Left + now.X - dragOrigin.X, Top + now.Y - dragOrigin.Y); dragOrigin = now; }
    void EndDrag(object sender, MouseEventArgs e) { dragging = false; }
    Label StatusLabel(int x, int y) { return new Label { AutoSize = false, Location = new Point(x, y), Size = new Size(126, 24), BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }; }
    void SetStatus(Label label, bool installed) { label.Text = installed ? "■ 已安装" : "■ 未安装"; label.ForeColor = installed ? Color.FromArgb(67, 255, 155) : Color.FromArgb(255, 199, 35); }
    void SetTraeStatus(Label label) { label.Text = Integration.TraeMcpStatus(); label.ForeColor = Integration.IsTraeMcpReadyAndConnected() ? Color.FromArgb(67, 255, 155) : Color.FromArgb(255, 199, 35); }
    protected override void OnPaint(PaintEventArgs e) {
      Graphics g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
      using (var edge = new SolidBrush(Color.FromArgb(5, 8, 13))) { g.FillRectangle(edge, 0, 0, Width, 5); g.FillRectangle(edge, 0, Height - 5, Width, 5); g.FillRectangle(edge, 0, 0, 5, Height); g.FillRectangle(edge, Width - 5, 0, 5, Height); }
      using (var rim = new Pen(Color.FromArgb(71, 85, 105), 2)) g.DrawRectangle(rim, 5, 5, Width - 11, Height - 11);
      using (var header = new SolidBrush(Color.FromArgb(15, 23, 38))) g.FillRectangle(header, 7, 7, Width - 14, 36);
      using (var rule = new SolidBrush(Color.FromArgb(31, 41, 55))) { g.FillRectangle(rule, 14, 112, Width - 28, 2); g.FillRectangle(rule, 14, 166, Width - 28, 2); g.FillRectangle(rule, 14, 217, Width - 28, 2); g.FillRectangle(rule, 14, 414, Width - 28, 2); g.FillRectangle(rule, 14, 576, Width - 28, 2); }
      using (var red = new SolidBrush(Color.FromArgb(255, 56, 72))) g.FillRectangle(red, 17, 18, 7, 7);
      using (var yellow = new SolidBrush(Color.FromArgb(255, 199, 35))) g.FillRectangle(yellow, 28, 18, 7, 7);
      using (var green = new SolidBrush(Color.FromArgb(67, 255, 155))) g.FillRectangle(green, 39, 18, 7, 7);
      base.OnPaint(e);
    }
  }

  sealed class MainForm : Form {
    readonly MonitorEngine engine = new MonitorEngine(); readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer(); readonly SettingsData settings; readonly PixelPoleControl widget = new PixelPoleControl(); readonly NotifyIcon tray = new NotifyIcon();
    readonly System.Windows.Forms.Timer taskbarBlinkTimer = new System.Windows.Forms.Timer(), eventDebounceTimer = new System.Windows.Forms.Timer(); readonly Dictionary<string, NotifyIcon> taskbarLights = new Dictionary<string, NotifyIcon>(); readonly NotifyIcon taskbarLogin = new NotifyIcon(); readonly ContextMenuStrip taskbarMenu = new ContextMenuStrip();
    readonly Dictionary<string, Icon> iconCache = new Dictionary<string, Icon>(); readonly Dictionary<string, long> sourceSeenAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); readonly Dictionary<string, AgentTask> resolvedStates = new Dictionary<string, AgentTask>(StringComparer.OrdinalIgnoreCase);
    long lastScanStartedAt; MonitorWatchers watchers; List<AgentTask> currentAgents = new List<AgentTask>(), lastGoodTasks = new List<AgentTask>(); bool quitting, dragging, scanning, pendingRescan, taskbarBlinkOn = true; Point dragOrigin; string lastSignature = null, taskbarLayoutSignature = null;
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    public MainForm(SettingsData loaded) {
      settings = loaded; if (settings.LampScale != 150 && settings.LampScale != 200) settings.LampScale = 100; float initialScale = settings.LampScale / 100f;
      Text = "Agent Beacon v1.3.6"; Name = "AgentBeaconWindow"; Width = (int)Math.Round(38 * initialScale); Height = (int)Math.Round(88 * initialScale); BackColor = PixelPoleControl.KeyColor; TransparencyKey = PixelPoleControl.KeyColor; ForeColor = Color.White; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - Width - 20, Screen.PrimaryScreen.WorkingArea.Top + 20); TopMost = true; DoubleBuffered = true;
      BuildUi(); BuildTray(); BuildTaskbarMenu(); timer.Interval = settings.RefreshMs; timer.Tick += delegate { RefreshTasks(); }; timer.Start(); taskbarBlinkTimer.Interval = 500; taskbarBlinkTimer.Tick += delegate { taskbarBlinkOn = !taskbarBlinkOn; UpdateTaskbarBlink(); }; eventDebounceTimer.Interval = 900; eventDebounceTimer.Tick += delegate { eventDebounceTimer.Stop(); RefreshTasks(); };
      Shown += delegate { if (watchers == null) watchers = new MonitorWatchers(delegate(bool layoutChanged) { if (layoutChanged) engine.InvalidateDiscovery(); try { if (!IsDisposed && IsHandleCreated) BeginInvoke(new Action(delegate { if (!eventDebounceTimer.Enabled) eventDebounceTimer.Start(); })); } catch { } }); if (settings.TaskbarMode) Hide(); RefreshTasks(); };
      FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!quitting) { e.Cancel = true; Hide(); tray.ShowBalloonTip(900, "Agent Beacon", "仍在托盘监控，双击灯标可恢复。", ToolTipIcon.None); } };
    }
    void BuildUi() {
      Controls.Add(widget); widget.ScaleFactor = settings.LampScale / 100f; widget.SetAgents(new List<AgentTask>());
      widget.SettingsClicked += delegate { ShowSettings(); };
      widget.CloseClicked += delegate { Hide(); };
      widget.MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left && !widget.IsButton(e.Location)) { dragging = true; dragOrigin = e.Location; } };
      widget.MouseMove += delegate(object s, MouseEventArgs e) { if (dragging) Location = new Point(Left + e.X - dragOrigin.X, Top + e.Y - dragOrigin.Y); };
      widget.MouseUp += delegate { dragging = false; };
    }
    void BuildTray() {
      tray.Icon = CachedCircleIcon("idle", Color.FromArgb(100, 116, 139)); tray.Visible = true; tray.Text = "Agent Beacon"; tray.DoubleClick += delegate { Show(); Activate(); };
      var menu = new ContextMenuStrip(); menu.Items.Add("显示红绿灯", null, delegate { Show(); Activate(); }); menu.Items.Add("立即刷新", null, delegate { RefreshTasks(); }); menu.Items.Add("设置", null, delegate { ShowSettings(); }); menu.Items.Add("退出", null, delegate { ExitApplication(); }); tray.ContextMenuStrip = menu;
    }
    void BuildTaskbarMenu() {
      taskbarMenu.Items.Add("切换到桌面灯杆", null, delegate { settings.TaskbarMode = false; Program.SaveSettings(settings); ApplyDisplayMode(); });
      taskbarMenu.Items.Add("立即刷新", null, delegate { RefreshTasks(); }); taskbarMenu.Items.Add("设置", null, delegate { ShowSettings(); }); taskbarMenu.Items.Add("退出", null, delegate { ExitApplication(); });
    }
    void ShowSettings() { using (var dialog = new SettingsForm(settings, delegate(int ms) { timer.Interval = ms; }, delegate(bool enabled) { settings.TaskbarMode = enabled; ApplyDisplayMode(); }, delegate(int scale) { ApplyLampScale(); })) { if (settings.TaskbarMode) dialog.ShowDialog(); else dialog.ShowDialog(this); } }
    int BaseWidthForCount(int count) { count = Math.Max(1, Math.Min(4, count)); return count == 1 ? 38 : count == 2 ? 53 : count == 3 ? 68 : 75; }
    void ResizeWidgetForCount(int count) {
      float scale = settings.LampScale / 100f; int right = Right;
      widget.ScaleFactor = scale; Size = new Size((int)Math.Round(BaseWidthForCount(count) * scale), (int)Math.Round(88 * scale)); Left = right - Width;
    }
    void ApplyLampScale() { ResizeWidgetForCount(currentAgents.Count); widget.SetAgents(currentAgents); }
    void ApplyDisplayMode() {
      if (settings.TaskbarMode) { tray.Visible = false; Hide(); UpdateTaskbarLights(); }
      else { taskbarBlinkTimer.Stop(); ClearTaskbarLights(); tray.Visible = true; Show(); Activate(); }
    }
    Icon MakeIcon(Color color) { using (var bmp = new Bitmap(32, 32)) { using (var g = Graphics.FromImage(bmp)) { g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Color.Transparent); using (var b = new SolidBrush(color)) g.FillEllipse(b, 4, 4, 24, 24); } IntPtr handle = bmp.GetHicon(); var icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon; } }
    Icon CachedCircleIcon(string key, Color color) { Icon icon; string cacheKey = "circle:" + key; if (!iconCache.TryGetValue(cacheKey, out icon)) { icon = MakeIcon(color); iconCache[cacheKey] = icon; } return icon; }
    Icon MakeTaskbarIcon(string status, bool blink) {
      using (var bmp = new Bitmap(32, 32)) { using (var g = Graphics.FromImage(bmp)) {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None; g.Clear(Color.Transparent);
        using (var body = new SolidBrush(Color.FromArgb(27, 34, 43))) g.FillRectangle(body, 8, 1, 16, 25);
        using (var edge = new Pen(Color.FromArgb(5, 8, 12), 2)) g.DrawRectangle(edge, 8, 1, 16, 25);
        DrawTaskbarLamp(g, 4, Color.FromArgb(255, 48, 60), status == State.Complete);
        DrawTaskbarLamp(g, 11, Color.FromArgb(255, 205, 32), status == State.Attention && blink);
        DrawTaskbarLamp(g, 18, Color.FromArgb(38, 231, 103), status == State.Running);
        using (var pole = new SolidBrush(Color.FromArgb(55, 65, 78))) g.FillRectangle(pole, 14, 27, 4, 5);
      } IntPtr handle = bmp.GetHicon(); var icon = (Icon)Icon.FromHandle(handle).Clone(); DestroyIcon(handle); return icon; }
    }
    void DrawTaskbarLamp(Graphics g, int y, Color color, bool active) { using (var brush = new SolidBrush(active ? color : Color.FromArgb(31, 38, 39))) g.FillRectangle(brush, 13, y, 6, 6); }
    string StatusText(string status) { return status == State.Running ? "进行中" : status == State.Attention ? "需要手动处理" : status == State.Complete ? "已结束" : "等待任务"; }
    string TaskTooltip(AgentTask task) { string value = task.Source + " · " + StatusText(task.Status) + " · " + (task.Detail ?? ""); return value.Length > 63 ? value.Substring(0, 63) : value; }
    Icon CachedTaskbarIcon(string status, bool blink) { string key = "taskbar:" + (status ?? "") + ":" + blink; Icon icon; if (!iconCache.TryGetValue(key, out icon)) { icon = MakeTaskbarIcon(status, blink); iconCache[key] = icon; } return icon; }
    void SetTaskbarIcon(NotifyIcon light, string status, bool blink) { light.Icon = CachedTaskbarIcon(status, blink); }
    void UpdateTaskbarLights() {
      if (!settings.TaskbarMode) return; bool attention = false;
      string layout = String.Join("|", currentAgents.ConvertAll(delegate(AgentTask task) { return task.Source; }).ToArray()); if (layout != taskbarLayoutSignature) { ClearTaskbarLights(); taskbarLayoutSignature = layout; }
      var wanted = new HashSet<string>(currentAgents.ConvertAll(delegate(AgentTask task) { return task.Source; }), StringComparer.OrdinalIgnoreCase);
      foreach (string source in new List<string>(taskbarLights.Keys)) if (!wanted.Contains(source)) { var stale = taskbarLights[source]; stale.Visible = false; stale.Icon = null; stale.Dispose(); taskbarLights.Remove(source); }
      if (currentAgents.Count == 0) {
        foreach (var light in taskbarLights.Values) light.Visible = false;
        if (taskbarLogin.Icon == null) SetTaskbarIcon(taskbarLogin, "", true); taskbarLogin.Text = "Agent Beacon · LOGIN..."; taskbarLogin.ContextMenuStrip = taskbarMenu; taskbarLogin.Visible = true;
      } else {
        taskbarLogin.Visible = false; taskbarLogin.Icon = null;
        foreach (var task in currentAgents) { NotifyIcon light; if (!taskbarLights.TryGetValue(task.Source, out light)) { light = new NotifyIcon(); light.ContextMenuStrip = taskbarMenu; taskbarLights[task.Source] = light; } SetTaskbarIcon(light, task.Status, taskbarBlinkOn); light.Text = TaskTooltip(task); light.Visible = true; if (task.Status == State.Attention) attention = true; }
      }
      if (attention) taskbarBlinkTimer.Start(); else { taskbarBlinkTimer.Stop(); taskbarBlinkOn = true; }
    }
    void UpdateTaskbarBlink() { if (!settings.TaskbarMode) return; foreach (var task in currentAgents) if (task.Status == State.Attention) { NotifyIcon light; if (taskbarLights.TryGetValue(task.Source, out light)) SetTaskbarIcon(light, task.Status, taskbarBlinkOn); } }
    void ClearTaskbarLights() {
      foreach (var light in taskbarLights.Values) { light.Visible = false; light.Icon = null; light.Dispose(); } taskbarLights.Clear();
      taskbarLogin.Visible = false; taskbarLogin.Icon = null;
    }
    void RefreshTasks() {
      if (scanning) { pendingRescan = true; return; } long now = Util.Now(); if (lastScanStartedAt != 0 && now - lastScanStartedAt < 800) return; lastScanStartedAt = now; scanning = true;
      ThreadPool.QueueUserWorkItem(delegate {
        var cycle = new ScanCycle(); var watch = Stopwatch.StartNew();
        try {
          cycle.Runtime = AgentProcesses.Snapshot(); cycle.Tasks = engine.Scan(out cycle.FilesRead);
          var codexTasks = cycle.Tasks.FindAll(delegate(AgentTask task) { return task.Source == "Codex"; });
          AgentTask latestCodex = AgentStateRules.LatestForSource("Codex", codexTasks);
          bool codexPendingExec = latestCodex != null && latestCodex.PendingExec && latestCodex.Status != State.Complete;
          bool codexAlreadyAttention = latestCodex != null && latestCodex.Status == State.Attention;
          cycle.CodexUiAttention = codexPendingExec && !codexAlreadyAttention && AgentProcesses.CodexNeedsUserAttention(cycle.Runtime);
          if (cycle.CodexUiAttention) { long eventAt = Util.Now(); cycle.Tasks.Add(new AgentTask { Id = "codex-ui-attention", Source = "Codex", SessionId = "codex-ui", Title = "Codex", Status = State.Attention, Detail = "Codex 正在等待你的确认", Evidence = "Codex 当前可见审批卡", StartedAt = eventAt, UpdatedAt = eventAt }); }
        } catch (Exception ex) { cycle.Error = ex.GetType().Name + ": " + ex.Message; }
        watch.Stop(); cycle.DurationMs = watch.ElapsedMilliseconds;
        try { if (!IsDisposed) BeginInvoke(new Action<ScanCycle>(FinishCycle), cycle); else scanning = false; } catch { scanning = false; }
      });
    }
    void FinishCycle(ScanCycle cycle) {
      scanning = false;
      if (!String.IsNullOrWhiteSpace(cycle.Error)) DiagnosticsHub.RecordError(cycle.Error); else { lastGoodTasks = cycle.Tasks; ApplyTasks(cycle); }
      if (pendingRescan) { pendingRescan = false; eventDebounceTimer.Stop(); eventDebounceTimer.Start(); }
    }
    void ApplyTasks(ScanCycle cycle) {
      var tasks = cycle.Tasks ?? lastGoodTasks; var detected = LatestPerAgent(tasks); var runtime = cycle.Runtime ?? new AgentRuntimeSnapshot(); var agents = new List<AgentTask>();
      foreach (string source in new[] { "TRAE", "Codex", "Claude Code", "OpenCode" }) if (runtime.Sources.Contains(source)) {
        long seenAt; if (!sourceSeenAt.TryGetValue(source, out seenAt)) { seenAt = runtime.CapturedAt > 0 ? runtime.CapturedAt : Util.Now(); sourceSeenAt[source] = seenAt; }
        long runtimeStarted = 0; runtime.StartedAt.TryGetValue(source, out runtimeStarted);
        var candidate = detected.Find(delegate(AgentTask t) { return t.Source == source; }); AgentTask previous = null; resolvedStates.TryGetValue(source, out previous);
        var resolved = AgentStateRules.ResolveForRuntime(source, candidate, runtimeStarted, seenAt, previous); agents.Add(resolved); resolvedStates[source] = AgentStateRules.Clone(resolved);
      }
      foreach (string source in new List<string>(sourceSeenAt.Keys)) if (!runtime.Sources.Contains(source)) { sourceSeenAt.Remove(source); resolvedStates.Remove(source); }
      var claudeTask = agents.Find(delegate(AgentTask task) { return task.Source == "Claude Code"; }); if (claudeTask != null && claudeTask.Status == State.Attention && AgentProcesses.ClaudeHasActiveToolProcess(claudeTask.UpdatedAt)) { claudeTask.Status = State.Running; claudeTask.Detail = "Shell 或工具正在执行"; }
      currentAgents = agents;
      int red = agents.FindAll(delegate(AgentTask t) { return t.Status == State.Complete; }).Count, yellow = agents.FindAll(delegate(AgentTask t) { return t.Status == State.Attention; }).Count, green = agents.FindAll(delegate(AgentTask t) { return t.Status == State.Running; }).Count;
      if (yellow > 0) tray.Icon = CachedCircleIcon("attention", Color.FromArgb(255, 199, 35)); else if (green > 0) tray.Icon = CachedCircleIcon("running", Color.FromArgb(35, 220, 105)); else if (red > 0) tray.Icon = CachedCircleIcon("complete", Color.FromArgb(255, 56, 72)); else tray.Icon = CachedCircleIcon("idle", Color.FromArgb(100, 116, 139));
      tray.Text = agents.Count == 0 ? "未检测到 Agent · LOGIN..." : String.Format("结束/空闲(红) {0} · 手动(黄) {1} · 进行(绿) {2}", red, yellow, green); DiagnosticsHub.Update(agents, cycle);
      var signature = new StringBuilder(); foreach (var task in agents) signature.Append(task.Source).Append(':').Append(task.Status).Append('|');
      string next = signature.ToString(); if (next == lastSignature) return; lastSignature = next;
      ResizeWidgetForCount(agents.Count); widget.SetAgents(agents);
      if (settings.TaskbarMode) { tray.Visible = false; Hide(); UpdateTaskbarLights(); }
    }
    List<AgentTask> LatestPerAgent(List<AgentTask> tasks) {
      var result = new List<AgentTask>();
      foreach (string source in new[] { "TRAE", "Codex", "Claude Code", "OpenCode" }) {
        var sourceTasks = tasks.FindAll(delegate(AgentTask t) { return t.Source == source; }); if (sourceTasks.Count == 0) continue;
        result.Add(AgentStateRules.LatestForSource(source, sourceTasks));
      }
      return result;
    }
    public void ExitApplication() { quitting = true; tray.Visible = false; taskbarBlinkTimer.Stop(); eventDebounceTimer.Stop(); ClearTaskbarLights(); Application.Exit(); }
    protected override void Dispose(bool disposing) { if (disposing) { tray.Visible = false; tray.Icon = null; taskbarBlinkTimer.Stop(); eventDebounceTimer.Stop(); if (watchers != null) watchers.Dispose(); ClearTaskbarLights(); taskbarLogin.Dispose(); taskbarMenu.Dispose(); tray.Dispose(); timer.Dispose(); taskbarBlinkTimer.Dispose(); eventDebounceTimer.Dispose(); foreach (var icon in iconCache.Values) icon.Dispose(); iconCache.Clear(); } base.Dispose(disposing); }
  }

  static class Integration {
    static void Extract(string resource, string target) { Directory.CreateDirectory(Path.GetDirectoryName(target)); using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)) using (var output = File.Create(target)) input.CopyTo(output); }
    static string TraeMcpDirectory { get { string configured = Environment.GetEnvironmentVariable("AGENT_BEACON_TRAE_MCP_DIR"); return String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight", "integrations") : Path.GetFullPath(configured); } }
    static string TraeMcpExecutable { get { return Path.Combine(TraeMcpDirectory, "Agent-Beacon-MCP.exe"); } }
    static string TraeMcpPendingExecutable { get { return Path.Combine(TraeMcpDirectory, "Agent-Beacon-MCP.pending.exe"); } }
    static string LegacyTraeMcpExecutable { get { return Path.Combine(TraeMcpDirectory, "Agent-Beacon-MCP-1.3.0.exe"); } }
    static string TraeMcpConfigPath { get { return Path.Combine(TraeMcpDirectory, "trae-mcp-config.json"); } }
    static string TraeRulePath { get { return Path.Combine(TraeMcpDirectory, "trae-agent-beacon-rule.md"); } }
    static bool SameFile(string left, string right) {
      try {
        var a = new FileInfo(left); var b = new FileInfo(right); if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
        using (var sha = SHA256.Create()) using (var first = File.OpenRead(left)) using (var second = File.OpenRead(right)) {
          byte[] x = sha.ComputeHash(first), y = sha.ComputeHash(second); if (x.Length != y.Length) return false; for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false; return true;
        }
      } catch { return false; }
    }
    static bool TryActivateTraeMcp(string source) {
      try {
        if (!File.Exists(TraeMcpExecutable)) File.Move(source, TraeMcpExecutable);
        else File.Replace(source, TraeMcpExecutable, null, true);
        return true;
      } catch { return false; }
    }
    static void DeleteLegacyTraeMcp() { try { if (File.Exists(LegacyTraeMcpExecutable)) File.Delete(LegacyTraeMcpExecutable); } catch { } }
    static string EnsureTraeMcpHelper(bool installIfMissing) {
      bool installed = File.Exists(TraeMcpExecutable) || File.Exists(TraeMcpPendingExecutable) || File.Exists(TraeMcpConfigPath) || File.Exists(LegacyTraeMcpExecutable);
      if (!installIfMissing && !installed) return "not-installed";
      Directory.CreateDirectory(TraeMcpDirectory);
      string fresh = Path.Combine(TraeMcpDirectory, "Agent-Beacon-MCP." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N") + ".tmp");
      try {
        Extract("trae-mcp-host.exe", fresh);
        if (File.Exists(TraeMcpExecutable) && SameFile(fresh, TraeMcpExecutable)) {
          File.Delete(fresh); try { if (File.Exists(TraeMcpPendingExecutable)) File.Delete(TraeMcpPendingExecutable); } catch { }
          DeleteLegacyTraeMcp(); return "current";
        }
        if (File.Exists(TraeMcpPendingExecutable)) {
          if (SameFile(fresh, TraeMcpPendingExecutable)) File.Delete(fresh);
          else { File.Delete(TraeMcpPendingExecutable); File.Move(fresh, TraeMcpPendingExecutable); }
        } else File.Move(fresh, TraeMcpPendingExecutable);
        if (TryActivateTraeMcp(TraeMcpPendingExecutable)) { DeleteLegacyTraeMcp(); return "updated"; }
        return "pending";
      } finally { try { if (File.Exists(fresh)) File.Delete(fresh); } catch { } }
    }
    static bool IsTraeMcpConfigCurrent() {
      try {
        if (!File.Exists(TraeMcpConfigPath)) return false;
        var root = Util.Json.DeserializeObject(File.ReadAllText(TraeMcpConfigPath, Encoding.UTF8)) as IDictionary<string, object>;
        IDictionary<string, object> servers = Util.D(root, "mcpServers"); IDictionary<string, object> server = Util.D(servers, "agent_beacon"); string command = Util.S(server, "command", "");
        return !String.IsNullOrWhiteSpace(command) && String.Equals(Path.GetFullPath(command), Path.GetFullPath(TraeMcpExecutable), StringComparison.OrdinalIgnoreCase);
      } catch { return false; }
    }
    public static void RefreshTraeMcpHelper() { try { EnsureTraeMcpHelper(false); } catch { } }
    public static bool IsTraeMcpUpdatePending() { return File.Exists(TraeMcpPendingExecutable); }
    public static bool IsTraeMcpPrepared() { return File.Exists(TraeMcpExecutable) && IsTraeMcpConfigCurrent() && !IsTraeMcpUpdatePending(); }
    public static bool IsTraeMcpConnected() {
      try {
        if (!Directory.Exists(Util.BridgeDir)) return false;
        foreach (string file in Directory.GetFiles(Util.BridgeDir, "trae-mcp-*.json")) {
          if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) continue;
          var row = Util.Json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>;
          if (row != null && Util.S(row, "source", "") == "TRAE" && Util.S(row, "integration", "") == "mcp") return true;
        }
      } catch { }
      return false;
    }
    public static bool IsTraeMcpReadyAndConnected() { return IsTraeMcpPrepared() && IsTraeMcpConnected(); }
    public static string TraeMcpStatus() {
      if (IsTraeMcpUpdatePending()) return "■ 关闭 TRAE 后重启灯，完成 MCP 更新";
      if (File.Exists(TraeMcpExecutable) && !IsTraeMcpConfigCurrent()) return "■ 需重新配置 TRAE MCP";
      return IsTraeMcpReadyAndConnected() ? "■ 已连接" : IsTraeMcpPrepared() ? "■ 待添加到 TRAE" : "■ 未配置";
    }
    public static string InstallTraeMcp() {
      try {
        Directory.CreateDirectory(TraeMcpDirectory);
        string target = Path.GetFullPath(TraeMcpExecutable), update = EnsureTraeMcpHelper(true);
        var server = new Dictionary<string, object>(); server["command"] = target; server["args"] = new[] { "--mcp-server" };
        var servers = new Dictionary<string, object>(); servers["agent_beacon"] = server;
        var root = new Dictionary<string, object>(); root["mcpServers"] = servers;
        string config = Util.Json.Serialize(root); File.WriteAllText(TraeMcpConfigPath, config, new UTF8Encoding(false)); Clipboard.SetText(config);
        if (update == "pending") return "新版 TRAE MCP 已暂存，但旧 Helper 正被占用。\n\n请先关闭 TRAE，再重启 Agent Beacon，然后重新点击此按钮复制配置。";
        return "TRAE MCP 配置已复制，固定路径为：\n" + target + "\n\n请删除 TRAE 中旧的 agent_beacon MCP，重新创建本地手动配置并粘贴。然后在 设置 > 对话流 中开启“自动运行 MCP”。\n\n最后点击“复制 TRAE 状态规则”，并粘贴到 TRAE 全局规则。";
      } catch (Exception ex) { return "准备 TRAE MCP 失败：" + ex.Message + "\n如果旧 MCP 正在运行，请先关闭 TRAE 后重试。"; }
    }
    public static string CopyTraeRule() {
      try {
        Directory.CreateDirectory(TraeMcpDirectory); string rule =
          "使用 agent_beacon_report_state 上报状态，每个用户任务使用新的 session_id，任务内保持不变。时序规则：开始前报 running；准备确认消息或执行延时期间保持 running，只有所有准备完成且下一步就是显示需要用户回复的确认消息时才报 waiting，waiting 后禁止思考、等待、执行命令或调用其他工具，必须立即显示确认；上一状态为 waiting 时，收到用户任何回复后的第一步必须用相同 session_id 报 running；所有工作结束且最终答复内容已完全准备好后，报 completed 作为最后一次工具调用并立即输出最终答复，此后禁止继续处理；失败/取消前报 failed/cancelled。中间步骤不得报 completed。";
        File.WriteAllText(TraeRulePath, rule, new UTF8Encoding(false)); Clipboard.SetText(rule);
        return "TRAE 状态规则已复制。\n\n请打开 TRAE Work：设置 > 规则，创建一条本地全局规则并粘贴。\n启用规则后，新任务将通过 MCP 主动上报绿、黄、红状态。";
      } catch (Exception ex) { return "复制 TRAE 规则失败：" + ex.Message; }
    }
    public static bool IsOpenCodeInstalled() { return File.Exists(Path.Combine(Util.Home, ".config", "opencode", "plugins", "agent-traffic-light.js")); }
    public static bool IsClaudeInstalled() {
      string hook = Path.Combine(Util.IntegrationDir, "claude-hook.cjs"), settings = Path.Combine(Util.Home, ".claude", "settings.json"); if (!File.Exists(hook) || !File.Exists(settings)) return false;
      try { string text = File.ReadAllText(settings, Encoding.UTF8); return text.IndexOf("claude-hook.cjs", StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("agent-traffic-light", StringComparison.OrdinalIgnoreCase) >= 0; } catch { return false; }
    }
    public static void RefreshClaudeScript() { try { if (IsClaudeInstalled()) Extract("claude-hook.cjs", Path.Combine(Util.IntegrationDir, "claude-hook.cjs")); } catch { } }
    public static void RefreshOpenCodeScript() { try { if (IsOpenCodeInstalled()) Extract("opencode-plugin.js", Path.Combine(Util.Home, ".config", "opencode", "plugins", "agent-traffic-light.js")); } catch { } }
    public static string InstallOpenCode() {
      try { string target = Path.Combine(Util.Home, ".config", "opencode", "plugins", "agent-traffic-light.js"); Extract("opencode-plugin.js", target); return "OpenCode 插件已安装到：\n" + target + "\n请重启 OpenCode。"; } catch (Exception ex) { return "安装失败：" + ex.Message; }
    }
    public static string InstallClaude() {
      try {
        string hook = Path.Combine(Util.IntegrationDir, "claude-hook.cjs"); Extract("claude-hook.cjs", hook);
        string settings = Path.Combine(Util.Home, ".claude", "settings.json"); IDictionary<string, object> root = new Dictionary<string, object>();
        if (File.Exists(settings)) { try { root = Util.Json.DeserializeObject(File.ReadAllText(settings, Encoding.UTF8)) as IDictionary<string, object>; } catch { return "现有 Claude Code 配置无法解析，为避免覆盖，安装已停止：\n" + settings; } }
        if (root == null) root = new Dictionary<string, object>(); object hooksObj; IDictionary<string, object> hooks;
        if (!root.TryGetValue("hooks", out hooksObj)) { hooks = new Dictionary<string, object>(); root["hooks"] = hooks; } else { hooks = hooksObj as IDictionary<string, object>; if (hooks == null) return "hooks 配置不是对象，未做修改。"; }
        string command = "node \"" + hook.Replace("\"", "\\\"") + "\"";
        foreach (string evt in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "Notification", "SubagentStart", "SubagentStop", "Stop", "StopFailure", "SessionEnd" }) {
          object old; var list = new ArrayList(); if (hooks.TryGetValue(evt, out old) && old is IEnumerable) foreach (object item in (IEnumerable)old) list.Add(item);
          if (!Util.Json.Serialize(list).Contains(hook.Replace("\\", "\\\\"))) { var commandHook = new Dictionary<string, object>(); commandHook["type"] = "command"; commandHook["command"] = command; commandHook["timeout"] = 5; var inner = new ArrayList(); inner.Add(commandHook); var group = new Dictionary<string, object>(); group["hooks"] = inner; list.Add(group); }
          hooks[evt] = list;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(settings)); if (File.Exists(settings)) File.Copy(settings, settings + ".agent-traffic-light.bak", true); File.WriteAllText(settings, Util.Json.Serialize(root), new UTF8Encoding(false));
        return "Claude Code Hooks 已安装。\n已保留备份（如原文件存在），请重启 Claude Code。";
      } catch (Exception ex) { return "安装失败：" + ex.Message; }
    }
  }

  static class Program {
    const string MutexName = "Local\\AgentBeaconV136", ShowEventName = "Local\\AgentBeaconShowV136", ExitEventName = "Local\\AgentBeaconExitV136"; static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight");
    static readonly string[] PreviousExitEvents = {
      "Local\\AgentBeaconExitV135",
      "Local\\AgentBeaconExitV134",
      "Local\\AgentBeaconExitV133",
      "Local\\AgentBeaconExitV132",
      "Local\\AgentBeaconExitV131",
      "Local\\AgentBeaconExitV130",
      "Local\\AgentBeaconExitV123",
      "Local\\AgentBeaconExitV122",
      "Local\\AgentBeaconExitV121",
      "Local\\AgentBeaconExitV120",
      "Local\\AgentBeaconExitV1111",
      "Local\\AgentBeaconExitV1110",
      "Local\\AgentBeaconExitV119",
      "Local\\AgentBeaconExitV118",
      "Local\\AgentBeaconExitV117",
      "Local\\AgentBeaconExitV116",
      "Local\\AgentBeaconExitV115",
      "Local\\AgentBeaconExitV114",
      "Local\\AgentBeaconExitV110R1",
      "Local\\AgentBeaconExitV110", "Local\\AgentBeaconExitV103", "Local\\AgentBeaconExitV102", "Local\\AgentBeaconExitV101", "Local\\AgentBeaconExitV100",
      "Local\\AgentTrafficLightNativeExitV077", "Local\\AgentTrafficLightNativeExitV076", "Local\\AgentTrafficLightNativeExitV075",
      "Local\\AgentTrafficLightNativeExitV074", "Local\\AgentTrafficLightNativeExitV073", "Local\\AgentTrafficLightNativeExitV072"
    };

    static void StopPreviousVersions() { foreach (string name in PreviousExitEvents) try { EventWaitHandle.OpenExisting(name).Set(); } catch { } }

    [STAThread] public static void Main(string[] args) {
      if (Array.IndexOf(args, "--exit") >= 0) { try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { } return; }
      StopPreviousVersions();
      bool created; using (var mutex = new Mutex(true, MutexName, out created)) {
        if (!created) { try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { } return; }
        try {
          Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); var loaded = LoadSettings(); if (loaded.RefreshMs < 1500) loaded.RefreshMs = 1500;
          SaveSettings(loaded); if (loaded.AutoStart) SetAutoStart(true); Integration.RefreshTraeMcpHelper(); Integration.RefreshClaudeScript(); Integration.RefreshOpenCodeScript(); if (Array.IndexOf(args, "--taskbar") >= 0) loaded.TaskbarMode = true; var form = new MainForm(loaded);
          using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName)) using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName)) {
            var listener = new Thread(new ThreadStart(delegate { while (!form.IsDisposed) { int signal = WaitHandle.WaitAny(new WaitHandle[] { showEvent, exitEvent }); if (!form.IsDisposed && form.IsHandleCreated) { if (signal == 0) form.BeginInvoke(new Action(delegate { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); })); else { form.BeginInvoke(new Action(form.ExitApplication)); return; } } } })); listener.IsBackground = true; listener.Start();
            if (Array.IndexOf(args, "--hidden") >= 0) form.Load += delegate { form.BeginInvoke(new Action(form.Hide)); }; Application.Run(form);
          }
        }
        catch (Exception ex) { DiagnosticsHub.RecordError(ex.ToString()); try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "agent-beacon.log"), DateTime.Now + " " + ex + Environment.NewLine, Encoding.UTF8); } catch { } MessageBox.Show(ex.ToString(), "Agent Beacon 启动失败"); }
      }
    }

    public static SettingsData LoadSettings() { try { return Util.Json.Deserialize<SettingsData>(File.ReadAllText(Path.Combine(DataDir, "settings.json"), Encoding.UTF8)) ?? new SettingsData(); } catch { return new SettingsData(); } }
    public static void SaveSettings(SettingsData settings) { try { Directory.CreateDirectory(DataDir); File.WriteAllText(Path.Combine(DataDir, "settings.json"), Util.Json.Serialize(settings), new UTF8Encoding(false)); } catch { } }
    public static void SetAutoStart(bool enabled) { using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) { if (enabled) { key.SetValue("AgentBeacon", "\"" + Application.ExecutablePath + "\" --hidden"); key.DeleteValue("AgentTrafficLight", false); } else { key.DeleteValue("AgentBeacon", false); key.DeleteValue("AgentTrafficLight", false); } } }
  }
}
