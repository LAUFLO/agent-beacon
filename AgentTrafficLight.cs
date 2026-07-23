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

namespace AgentTrafficLightNative {
  static class State {
    public const string Complete = "complete";
    public const string Running = "running";
    public const string Attention = "attention";
  }

  sealed class AgentTask {
    public string Id, Source, SessionId, Title, Status, Detail, Evidence, InteractionId;
    public string Cwd, Phase, HealthState, HealthDetail;
    public long StartedAt, UpdatedAt, LastActivityAt;
    public int Progress = -1;
    public bool ExplicitStart, ReliableStart, PendingExec, Restored, Stalled;
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
    public long DurationMs, PrivateMemoryMb;
    public int EffectiveIntervalMs;
    public bool CodexUiAttention;
    public string Error;
  }

  sealed class CacheEntry {
    public long Size, Ticks;
    public List<AgentTask> Tasks;
  }

  sealed class CodexStreamState {
    public long Offset, Size, Ticks; public string Remainder = "", Current, Session, Cwd;
    public bool AfterTerminal;
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
    public bool AutoCheckUpdates = true;
    public bool NotificationsEnabled = true;
    public bool NotifyAttention = true;
    public bool NotifyComplete = true;
    public bool QuietHoursEnabled = false;
    public int QuietStartHour = 22;
    public int QuietEndHour = 8;
    public string NotificationAgents = "TRAE|Codex|Claude Code|OpenCode";
    public bool AdaptiveScanning = true;
    public int AttentionNotifyDelaySeconds = 0;
    public int LongRunningReminderMinutes = 0;
  }

  static class DiagnosticsHub {
    static readonly object Sync = new object();
    static readonly Dictionary<string, string> Lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static long lastScanAt, lastDuration, lastMemoryMb, lastPersistAt; static int lastFiles, lastInterval; static string lastError = "", lastStateSignature = "", lastPersisted = "";
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
        lastScanAt = Util.Now(); lastDuration = cycle == null ? 0 : cycle.DurationMs; lastMemoryMb = cycle == null ? 0 : cycle.PrivateMemoryMb; lastFiles = cycle == null ? 0 : cycle.FilesRead; lastInterval = cycle == null ? 0 : cycle.EffectiveIntervalMs; lastError = cycle == null ? "" : (cycle.Error ?? "");
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
        return "Agent Beacon " + AppInfo.Version + " 诊断（不包含聊天正文）" + Environment.NewLine + "扫描: " + at + " · " + lastDuration + "ms · 读取 " + lastFiles + " 个变化文件 · 间隔 " + lastInterval + "ms · 内存 " + lastMemoryMb + "MB" + (String.IsNullOrWhiteSpace(lastError) ? "" : Environment.NewLine + "错误: " + lastError) + Environment.NewLine + Summary();
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
          state.Offset = start; state.Remainder = ""; state.Current = null; state.Session = Path.GetFileNameWithoutExtension(file); state.AfterTerminal = false; state.Turns.Clear(); state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear();
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
      if (type == "session_meta") { state.Session = Util.S(payload, "session_id", Util.S(payload, "id", state.Session)); state.Cwd = Util.S(payload, "cwd", state.Cwd); return; }
      if (type == "turn_context") { state.Cwd = Util.S(payload, "cwd", state.Cwd); return; }
      string ptype = Util.S(payload, "type", "");
      if (type == "event_msg" && ptype == "task_started") {
        state.AfterTerminal = false;
        state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear();
        state.Current = Util.S(payload, "turn_id", Guid.NewGuid().ToString("N"));
        state.Turns[state.Current] = new AgentTask { Id = "codex:" + state.Session + ":" + state.Current, Source = "Codex", SessionId = state.Session, Title = "Codex 任务", Status = State.Running, Detail = "正在执行", Phase = "开始处理", Cwd = state.Cwd, StartedAt = at, UpdatedAt = at, LastActivityAt = at }; return;
      }
      if (state.AfterTerminal) {
        bool newUserTurn = type == "event_msg" && ptype == "user_message";
        if (!newUserTurn) return;
        state.AfterTerminal = false;
      }
      bool activity = type == "response_item" || (type == "event_msg" && Regex.IsMatch(ptype, "user_message|agent_message|token_count|patch_apply|task_complete|turn_aborted", RegexOptions.IgnoreCase));
      if (state.Current == null && activity) {
        state.Current = "tail-latest:" + at;
        state.Turns[state.Current] = new AgentTask { Id = "codex:" + state.Session + ":" + state.Current, Source = "Codex", SessionId = state.Session, Title = "Codex 任务", Status = State.Running, Detail = "检测到实时活动", Phase = "处理中", Cwd = state.Cwd, StartedAt = at, UpdatedAt = at, LastActivityAt = at };
      }
      AgentTask task; if (state.Current == null || !state.Turns.TryGetValue(state.Current, out task)) return;
      task.UpdatedAt = Math.Max(task.UpdatedAt, at); task.LastActivityAt = Math.Max(task.LastActivityAt, at); if (String.IsNullOrWhiteSpace(task.Cwd)) task.Cwd = state.Cwd;
      if (type == "event_msg" && ptype == "user_message") task.Title = Util.Clean(Util.S(payload, "message", ""), task.Title);
      else if (type == "event_msg" && ptype == "agent_message") task.Phase = "整理结果";
      else if (type == "event_msg" && ptype == "patch_apply") task.Phase = "应用修改";
      else if (type == "event_msg" && ptype == "task_complete") { task.Status = State.Complete; task.Detail = "任务已结束"; task.Phase = "已结束"; task.PendingExec = false; state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear(); state.Current = null; state.AfterTerminal = true; }
      else if (type == "event_msg" && ptype == "turn_aborted") { task.Status = State.Complete; task.Detail = "任务已中断"; task.Phase = "已中断"; task.PendingExec = false; state.PendingAttentionCalls.Clear(); state.PendingExecCalls.Clear(); state.Current = null; state.AfterTerminal = true; }
      else if (type == "response_item" && CodexEventCompatibility.IsToolCall(ptype)) {
        string name = Util.S(payload, "name", ""), input = CodexEventCompatibility.Input(payload);
        string callId = CodexEventCompatibility.CallId(payload);
        bool execCall = CodexEventCompatibility.IsExec(name);
        task.Phase = execCall ? "执行命令" : "调用工具";
        if (execCall) { state.PendingExecCalls.Add(callId); task.PendingExec = true; }
        bool explicitRequest = CodexEventCompatibility.IsExplicitInteraction(name, input)
          || CodexEventCompatibility.IsComputerUseAction(name, input)
          || (execCall && CodexInputRequiresEscalation(input));
        if (explicitRequest) {
          state.PendingAttentionCalls.Add(callId); task.InteractionId = callId;
          RefreshCodexPendingAttention(state);
        }
      } else if (type == "response_item" && CodexEventCompatibility.IsToolOutput(ptype)) {
        task.Phase = "处理工具结果";
        string callId = CodexEventCompatibility.CallId(payload); if (callId == "*") callId = "";
        state.PendingExecCalls.Remove("*"); if (!String.IsNullOrWhiteSpace(callId)) state.PendingExecCalls.Remove(callId); task.PendingExec = state.PendingExecCalls.Count > 0;
        bool resolved = state.PendingAttentionCalls.Remove("*"); if (!String.IsNullOrWhiteSpace(callId)) resolved = state.PendingAttentionCalls.Remove(callId) || resolved;
        if (resolved) { if (state.PendingAttentionCalls.Count == 0) task.InteractionId = ""; RefreshCodexPendingAttention(state); if (task.Status == State.Running) { task.Detail = "已确认，继续执行"; task.Phase = "继续处理"; } }
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
        if (task == null) task = new AgentTask { Id = "claude:" + session, Source = "Claude Code", SessionId = session, Title = "Claude Code 任务", Status = State.Running, Detail = "正在执行", Phase = "处理中", StartedAt = at, UpdatedAt = at, LastActivityAt = at };
        task.Id = "claude:" + session; task.SessionId = session; task.UpdatedAt = Math.Max(task.UpdatedAt, at); task.LastActivityAt = Math.Max(task.LastActivityAt, at); task.Cwd = Util.S(row, "cwd", task.Cwd);
        bool manual = Regex.IsMatch(raw, "\\\"name\\\"\\s*:\\s*\\\"(?:AskUserQuestion|request_user_input|PermissionRequest)\\\"|approval[_ -]?(?:required|pending)|waiting[_ -]?(?:for[_ -]?)?(?:user|input|approval)", RegexOptions.IgnoreCase);
        bool complete = assistant && Regex.IsMatch(raw, "\\\"stop_reason\\\"\\s*:\\s*\\\"(?:end_turn|stop_sequence)\\\"", RegexOptions.IgnoreCase);
        bool running = userMessage || toolResult || (assistant && Regex.IsMatch(raw, "\\\"type\\\"\\s*:\\s*\\\"tool_use\\\"|\\\"stop_reason\\\"\\s*:\\s*\\\"tool_use\\\"", RegexOptions.IgnoreCase));
        if (running) { task.Status = State.Running; task.Detail = toolResult ? "工具执行完成，继续处理" : "正在执行"; task.Phase = toolResult ? "处理工具结果" : "处理中"; }
        if (manual) { task.Status = State.Attention; task.Detail = "等待你的确认或输入"; task.Phase = "等待确认"; }
        if (complete) { task.Status = State.Complete; task.Detail = "任务已完成"; task.Phase = "已完成"; }
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
      int progress = -1; Int32.TryParse(Util.S(row, "progress", "-1"), out progress); if (progress < 0 || progress > 100) progress = -1;
      var task = new AgentTask { Id = Util.S(row, "id", source + ":" + sid), Source = source, SessionId = sid, Title = Util.Clean(Util.S(row, "title", ""), source + " 任务"), Status = status, Detail = Util.Clean(Util.S(row, "detail", ""), status == State.Complete ? "任务已完成" : "正在执行"), Evidence = evidence, Cwd = Util.S(row, "cwd", ""), Phase = Util.Clean(Util.S(row, "phase", ""), ""), Progress = progress, StartedAt = Util.N(row, "startedAt", updated), UpdatedAt = updated, LastActivityAt = Util.N(row, "lastActivityAt", updated), ExplicitStart = source == "TRAE" || Util.S(row, "explicitStart", "").Equals("True", StringComparison.OrdinalIgnoreCase), ReliableStart = source == "TRAE" || Util.S(row, "reliableStart", "").Equals("True", StringComparison.OrdinalIgnoreCase) };
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
      return chineseApproval || Regex.IsMatch(value, "^(?:是否|要不要)允许|需要(?:你|您).{0,12}(?:确认|批准|选择)|do you want to allow|approval required|requires your approval", RegexOptions.IgnoreCase);
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
    public static AgentTask SelectCodexUiAttentionTarget(List<AgentTask> sourceTasks) {
      var running = new List<AgentTask>();
      foreach (var task in sourceTasks ?? new List<AgentTask>()) {
        if (task == null || !String.Equals(task.Source, "Codex", StringComparison.OrdinalIgnoreCase) || task.Status == State.Complete) continue;
        running.Add(task);
      }
      if (running.Count == 0) return null;
      var pendingExec = running.FindAll(delegate(AgentTask task) { return task.PendingExec; });
      var candidates = pendingExec.Count > 0 ? pendingExec : running;
      candidates.Sort(delegate(AgentTask left, AgentTask right) {
        int updated = left.UpdatedAt.CompareTo(right.UpdatedAt); if (updated != 0) return updated;
        return left.StartedAt.CompareTo(right.StartedAt);
      });
      return Clone(candidates[0]);
    }

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
    public static AgentTask Idle(string source) { long now = Util.Now(); return new AgentTask { Id = "idle:" + source, Source = source, SessionId = "", Title = source, Status = State.Complete, Detail = "Agent 已启动，等待新任务", Phase = "等待任务", Evidence = "进程检测 · 未发现本次启动后的任务事件", StartedAt = now, UpdatedAt = now, LastActivityAt = now }; }
    public static AgentTask Clone(AgentTask task) { return task == null ? null : new AgentTask { Id = task.Id, Source = task.Source, SessionId = task.SessionId, Title = task.Title, Status = task.Status, Detail = task.Detail, Evidence = task.Evidence, InteractionId = task.InteractionId, Cwd = task.Cwd, Phase = task.Phase, HealthState = task.HealthState, HealthDetail = task.HealthDetail, StartedAt = task.StartedAt, UpdatedAt = task.UpdatedAt, LastActivityAt = task.LastActivityAt, Progress = task.Progress, ExplicitStart = task.ExplicitStart, ReliableStart = task.ReliableStart, PendingExec = task.PendingExec, Restored = task.Restored, Stalled = task.Stalled }; }
  }



  static class Program {
    const string MutexName = "Local\\AgentBeaconStable", ShowEventName = "Local\\AgentBeaconShowStable", ExitEventName = "Local\\AgentBeaconExitStable"; static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTrafficLight");
    static readonly string[] PreviousExitEvents = {
      "Local\\AgentBeaconExitV136",
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
      DpiSupport.Enable(); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
      if (Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1" && Array.IndexOf(args, "--progress-preview") >= 0) { using (var preview = new PixelProgressForm("自动更新 v" + AppInfo.Version, true)) { preview.Shown += delegate { preview.Report(64, "正在下载…"); }; Application.Run(preview); } return; }
      if (Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1" && Array.IndexOf(args, "--update-confirm-preview") >= 0) { PixelDialog.Show(null, "发现 v" + AppInfo.Version + "，是否立即更新？", "发现新版本", PixelDialogButtons.YesNo); return; }
      if (Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1" && Array.IndexOf(args, "--history-preview") >= 0) {
        long now = Util.Now(); var tasks = new List<AgentTask> {
          new AgentTask { Id = "preview:codex:wait", Source = "Codex", SessionId = "codex-wait", Status = State.Attention, Detail = "等待命令确认", Phase = "等待确认", Cwd = @"D:\agent-beacon", StartedAt = now - 392000, UpdatedAt = now - 1000, LastActivityAt = now - 1000 },
          new AgentTask { Id = "preview:codex:run", Source = "Codex", SessionId = "codex-run", Status = State.Running, Detail = "正在测试", Phase = "正在测试", Cwd = @"D:\api-service", StartedAt = now - 291000, UpdatedAt = now, LastActivityAt = now },
          new AgentTask { Id = "preview:trae:run", Source = "TRAE", SessionId = "trae-run", Status = State.Running, Detail = "正在执行", Phase = "正在执行", Cwd = @"D:\client-desktop", StartedAt = now - 724000, UpdatedAt = now, LastActivityAt = now },
          new AgentTask { Id = "preview:open:run", Source = "OpenCode", SessionId = "open-run", Status = State.Running, Detail = "正在分析", Phase = "正在分析", Cwd = @"D:\docs", StartedAt = now - 135000, UpdatedAt = now, LastActivityAt = now }
        };
        var health = new List<TaskSourceHealth> { new TaskSourceHealth { Source = "Codex", State = "attention", Detail = "事件源正常，任务正在等待处理", Trusted = true, LastEventAt = now }, new TaskSourceHealth { Source = "TRAE", State = "healthy", Detail = "事件源正常", Trusted = true, LastEventAt = now }, new TaskSourceHealth { Source = "Claude Code", State = "idle", Detail = "最近无任务事件", Trusted = true, LastEventAt = now }, new TaskSourceHealth { Source = "OpenCode", State = "healthy", Detail = "事件源正常", Trusted = true, LastEventAt = now } };
        TaskCenterState.Update(tasks, health); using (var preview = new HistoryForm()) Application.Run(preview); return;
      }
      if (Environment.GetEnvironmentVariable("AGENT_BEACON_UI_TEST") == "1" && Array.IndexOf(args, "--task-center-preview") >= 0) {
        long now = Util.Now(); var tasks = new List<AgentTask> {
          new AgentTask { Id = "preview:wait", Source = "Codex", SessionId = "codex1234", Status = State.Attention, Detail = "等待你的确认", Phase = "等待确认", Cwd = @"D:\agent-beacon", StartedAt = now - 360000, UpdatedAt = now - 360000, LastActivityAt = now - 360000 },
          new AgentTask { Id = "preview:run", Source = "OpenCode", SessionId = "open5678", Status = State.Running, Detail = "正在执行", Phase = "执行测试", Progress = 42, Cwd = @"D:\web-portal", StartedAt = now - 180000, UpdatedAt = now, LastActivityAt = now }
        };
        var health = new List<TaskSourceHealth> { new TaskSourceHealth { Source = "Codex", State = "attention", Detail = "事件源正常，任务正在等待处理", Trusted = true, LastEventAt = now }, new TaskSourceHealth { Source = "OpenCode", State = "healthy", Detail = "事件源正常", Trusted = true, LastEventAt = now } };
        using (var preview = new TaskQueuePopup(delegate(AgentTask task) { }, delegate { })) { preview.StartPosition = FormStartPosition.CenterScreen; preview.ShowInTaskbar = true; preview.AutoCloseOnDeactivate = false; preview.UpdateData(tasks, health); Application.Run(preview); } return;
      }
      if (UpdateService.TryApplyFromArguments(args)) return;
      if (Array.IndexOf(args, "--exit") >= 0) { try { EventWaitHandle.OpenExisting(ExitEventName).Set(); } catch { } return; }
      StopPreviousVersions();
      bool created; using (var mutex = new Mutex(true, MutexName, out created)) {
        if (!created) { try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { } return; }
        try {
          UpdateService.CleanupOldUpdate(); var loaded = LoadSettings(); if (loaded.RefreshMs < 1500) loaded.RefreshMs = 1500;
          SaveSettings(loaded); if (loaded.AutoStart) SetAutoStart(true); Integration.RefreshTraeMcpHelper(); Integration.RefreshClaudeScript(); Integration.RefreshOpenCodeScript(); if (Array.IndexOf(args, "--taskbar") >= 0) loaded.TaskbarMode = true; var form = new MainForm(loaded); if (Array.IndexOf(args, "--settings") >= 0) form.Shown += delegate { form.BeginInvoke(new Action(form.OpenSettings)); };
          using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName)) using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName)) {
            var listener = new Thread(new ThreadStart(delegate { while (!form.IsDisposed) { int signal = WaitHandle.WaitAny(new WaitHandle[] { showEvent, exitEvent }); if (!form.IsDisposed && form.IsHandleCreated) { if (signal == 0) form.BeginInvoke(new Action(delegate { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); })); else { form.BeginInvoke(new Action(form.ExitApplication)); return; } } } })); listener.IsBackground = true; listener.Start();
            if (Array.IndexOf(args, "--hidden") >= 0) form.Load += delegate { form.BeginInvoke(new Action(form.Hide)); }; Application.Run(form);
          }
        }
        catch (Exception ex) { DiagnosticsHub.RecordError(ex.ToString()); try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "agent-beacon.log"), DateTime.Now + " " + ex + Environment.NewLine, Encoding.UTF8); } catch { } PixelDialog.Show(ex.ToString(), "Agent Beacon 启动失败"); }
      }
    }

    public static SettingsData LoadSettings() { try { return Util.Json.Deserialize<SettingsData>(File.ReadAllText(Path.Combine(DataDir, "settings.json"), Encoding.UTF8)) ?? new SettingsData(); } catch { return new SettingsData(); } }
    public static void SaveSettings(SettingsData settings) { try { Directory.CreateDirectory(DataDir); File.WriteAllText(Path.Combine(DataDir, "settings.json"), Util.Json.Serialize(settings), new UTF8Encoding(false)); } catch { } }
    public static void SetAutoStart(bool enabled) { using (var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")) { if (enabled) { key.SetValue("AgentBeacon", "\"" + Application.ExecutablePath + "\" --hidden"); key.DeleteValue("AgentTrafficLight", false); } else { key.DeleteValue("AgentBeacon", false); key.DeleteValue("AgentTrafficLight", false); } } }
  }
}
