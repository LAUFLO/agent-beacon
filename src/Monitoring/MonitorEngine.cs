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
}
