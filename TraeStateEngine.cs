using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentTrafficLightNative {
  // TRAE's files are mutable snapshots, not an append-only event stream. This
  // module converts every snapshot into one ordered, request-scoped state and
  // remembers terminal evidence so background model-state rewrites cannot reopen it.
  sealed class TraeStateEngine {
    enum Phase { None, Running, Attention, Settled }
    sealed class TerminalRecord { public string Fingerprint; public int ResponseCount; public long At; public string Detail; }
    readonly Dictionary<string, TerminalRecord> terminals = new Dictionary<string, TerminalRecord>(StringComparer.OrdinalIgnoreCase);

    public List<AgentTask> ParseSession(string file, long mtime) {
      var result = new List<AgentTask>(); IDictionary<string, object> root = LoadRoot(file); if (root == null) return result;
      object requestsValue; if (!root.TryGetValue("requests", out requestsValue)) return result;
      List<object> requests = Items(requestsValue); int index; IDictionary<string, object> request = LatestRequest(requests, out index); if (request == null) return result;
      string session = Util.S(root, "sessionId", Path.GetFileNameWithoutExtension(file)); if (String.IsNullOrWhiteSpace(session)) return result;
      string requestId = Util.S(request, "requestId", ""); bool realId = !String.IsNullOrWhiteSpace(requestId); if (!realId) requestId = "request-" + (index + 1);
      long requestAt = RequestClock(request, index, mtime), updated = Math.Max(requestAt, Util.N(root, "lastMessageDate", 0)); if (updated <= 0) updated = mtime;
      string key = session + ":" + requestId; AgentTask task = Evaluate(root, request, key, updated, mtime);
      task.Id = "trae-chat:" + key; task.Source = "TRAE"; task.SessionId = session; task.Title = Util.Clean(Util.S(root, "customTitle", ""), "TRAE Work");
      task.StartedAt = requestAt; task.UpdatedAt = updated; task.ExplicitStart = realId && requestAt > 0; task.ReliableStart = realId && HasRequestClock(request);
      task.Evidence = "TRAE 状态机 · 结构化会话"; result.Add(task); return result;
    }

    AgentTask Evaluate(IDictionary<string, object> root, IDictionary<string, object> request, string key, long updated, long mtime) {
      object responseValue; request.TryGetValue("response", out responseValue); List<object> response = Items(responseValue);
      Phase phase = Phase.None; object decisive = null;
      foreach (object item in response) {
        Phase next = Classify(item); if (next != Phase.None) { phase = next; decisive = item; }
      }
      string latestText = decisive == null ? "" : NodeText(decisive); string allText = NodeText(responseValue);
      IDictionary<string, object> model = Util.D(request, "modelState"); long modelValue = Util.N(model, "value", -1);
      string raw = Util.Json.Serialize(request); bool canceled = Regex.IsMatch(raw, "\\\"(?:code|reason)\\\"\\s*:\\s*\\\"cancel(?:ed|led)\\\"|\\\"isCanceled\\\"\\s*:\\s*true", RegexOptions.IgnoreCase);
      bool rootPending = PendingNode(request, 0), interactive = InteractiveText(String.IsNullOrWhiteSpace(latestText) ? allText : latestText), continuing = ContinuationText(latestText);
      string status, detail;
      if (modelValue == 1) { status = State.Complete; detail = "任务已完成"; }
      else if (modelValue == 3) { status = State.Complete; detail = "任务失败并结束"; }
      else if (canceled) { status = State.Complete; detail = "任务已取消"; }
      else if (phase == Phase.Attention || modelValue == 4 || (phase != Phase.Running && interactive)) { status = State.Attention; detail = "等待你的确认或回答"; }
      // TRAE Work frequently leaves modelState.value at 0 after the final
      // assistant response has already replaced the last progress item. Treat
      // the ordered response tail as newer evidence than that mutable summary
      // field. TraeDisplayStateMachine still requires this terminal candidate
      // to remain stable for five seconds, so a subsequently appended tool or
      // progress item cancels it before the lamp can flash red.
      else if (phase == Phase.Running || continuing) { status = State.Running; detail = "正在执行"; }
      else if (phase == Phase.Settled) { status = State.Complete; detail = modelValue == 2 ? "任务已完成（响应流结束）" : "任务已完成（最终响应已稳定）"; }
      else if (modelValue == 0) { status = State.Running; detail = "已确认，继续执行"; }
      else if (rootPending && response.Count == 0) { status = State.Attention; detail = "等待你的确认或回答"; }
      else { status = State.Running; detail = "正在执行"; }

      string fingerprint = ResponseFingerprint(responseValue); TerminalRecord remembered;
      if (status == State.Complete) terminals[key] = new TerminalRecord { Fingerprint = fingerprint, ResponseCount = response.Count, At = updated, Detail = detail };
      else if (terminals.TryGetValue(key, out remembered)) {
        bool backgroundRewrite = response.Count <= remembered.ResponseCount && phase == Phase.None;
        if (String.Equals(remembered.Fingerprint, fingerprint, StringComparison.Ordinal) || backgroundRewrite) { status = State.Complete; detail = remembered.Detail + " · 已锁定"; updated = Math.Max(updated, remembered.At); }
        else terminals.Remove(key); // A genuinely new response item may resume or ask within the same request.
      }
      return new AgentTask { Status = status, Detail = detail, UpdatedAt = updated };
    }

    IDictionary<string, object> LatestRequest(List<object> requests, out int selectedIndex) {
      IDictionary<string, object> selected = null; long best = Int64.MinValue; selectedIndex = -1;
      for (int i = 0; i < requests.Count; i++) {
        var row = requests[i] as IDictionary<string, object>; if (row == null) continue; long clock = RequestClock(row, i, 0);
        if (selected == null || clock > best || (clock == best && i > selectedIndex)) { selected = row; best = clock; selectedIndex = i; }
      }
      return selected;
    }

    bool HasRequestClock(IDictionary<string, object> request) { return Util.N(request, "timestamp", 0) > 0 || Util.N(request, "createdAt", 0) > 0 || Util.N(request, "updatedAt", 0) > 0; }
    long RequestClock(IDictionary<string, object> request, int index, long fallback) {
      long value = Math.Max(Util.N(request, "timestamp", 0), Math.Max(Util.N(request, "createdAt", 0), Util.N(request, "updatedAt", 0)));
      var model = Util.D(request, "modelState"); value = Math.Max(value, Util.N(model, "completedAt", 0)); object response; if (request.TryGetValue("response", out response)) value = Math.Max(value, NodeClock(response, 0));
      if (value <= 0) value = fallback > 0 ? fallback + index : index; return value;
    }
    long NodeClock(object node, int depth) {
      if (node == null || depth > 12) return 0; long result = 0; var row = node as IDictionary<string, object>;
      if (row != null) { result = Math.Max(Util.N(row, "timestamp", 0), Math.Max(Util.N(row, "createdAt", 0), Util.N(row, "updatedAt", 0))); foreach (object value in row.Values) result = Math.Max(result, NodeClock(value, depth + 1)); return result; }
      foreach (object item in Items(node)) result = Math.Max(result, NodeClock(item, depth + 1)); return result;
    }

    Phase Classify(object item) {
      if (PendingNode(item, 0)) return Phase.Attention;
      var row = item as IDictionary<string, object>; if (row == null) return Phase.None;
      string kind = Util.S(row, "kind", Util.S(row, "type", "")), status = Util.S(row, "status", Util.S(row, "state", ""));
      string text = NodeText(item);
      // Recent TRAE Work builds sometimes wrap both confirmation cards and the
      // final assistant message in progressMessage. Semantic state must win
      // over that generic renderer kind.
      if (InteractiveText(text)) return Phase.Attention;
      if (TerminalText(text) && !ContinuationText(text)) return Phase.Settled;
      if (Regex.IsMatch(kind, "progress|tool|command|execution|terminal|thinking|stream|shell", RegexOptions.IgnoreCase) || Regex.IsMatch(status, "running|in[_ -]?progress|executing|streaming|busy", RegexOptions.IgnoreCase)) return Phase.Running;
      if (Regex.IsMatch(kind, "markdown|text|answer|assistant|final", RegexOptions.IgnoreCase) && !String.IsNullOrWhiteSpace(text)) return Phase.Settled;
      return Phase.None;
    }
    bool PendingNode(object node, int depth) {
      if (node == null || depth > 14) return false; var row = node as IDictionary<string, object>;
      if (row != null) {
        string kind = Util.S(row, "kind", Util.S(row, "type", "")), status = Util.S(row, "status", Util.S(row, "state", ""));
        bool used = Bool(row, "isUsed", false), resolved = Bool(row, "resolved", false) || Bool(row, "answered", false);
        if (!used && !resolved && Regex.IsMatch(kind, "^(?:confirmation|question|elicitation2?|permission|approval)$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(status, "pending|waiting[_ -]?(?:user|reply|answer|approval|confirmation)|needs?[_ -]?(?:input|approval)", RegexOptions.IgnoreCase)) return true;
        foreach (var pair in row) if (Regex.IsMatch(pair.Key ?? "", "confirmation|question|elicitation|permission|approval", RegexOptions.IgnoreCase)) {
          var child = pair.Value as IDictionary<string, object>;
          if (child != null && !Bool(child, "isUsed", false) && !Bool(child, "resolved", false) && !Bool(child, "answered", false)) return true;
          if (PendingNode(pair.Value, depth + 1)) return true;
        }
        return false;
      }
      foreach (object item in Items(node)) if (PendingNode(item, depth + 1)) return true; return false;
    }
    bool Bool(IDictionary<string, object> row, string key, bool fallback) { object value; bool parsed; return row != null && row.TryGetValue(key, out value) && value != null && Boolean.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback; }
    bool InteractiveText(string value) { return Regex.IsMatch(value ?? "", "正在向用户提问|等待(?:你|您|用户)的?(?:回复|回答|确认|选择)|请(?:你|您)?(?:回复|回答|确认|选择|输入)|(?:你|您)(?:确认|是否)|是否(?:要|需要|允许|继续)|waiting for (?:your|user).{0,12}(?:reply|response|answer|confirmation|choice)|please (?:reply|answer|confirm|choose|enter)|do you (?:want|confirm)|asking the user|awaiting your response", RegexOptions.IgnoreCase); }
    bool TerminalText(string value) { return Regex.IsMatch(value ?? "", "(?:全|整个|全部)?流程.{0,16}(?:已|顺利)?(?:完成|结束)|任务.{0,12}(?:已|顺利)?(?:完成|结束)|(?:全部|所有).{0,16}(?:完成|处理完毕)|一切正常|处理完毕|测试通过|all (?:done|finished|complete)|completed successfully|task (?:is )?(?:complete|finished)", RegexOptions.IgnoreCase); }
    bool ContinuationText(string value) { return Regex.IsMatch(value ?? "", "未完成|尚未完成|没有完成|无法完成|完成(?:后|前|时|之前)|等待.{0,12}完成|正在(?:完成|执行|运行|处理|调用|生成|修改|测试|检查|读取|分析|构建|安装)|接下来|下一步|继续(?:执行|处理|操作)|后续(?:步骤|任务|处理)|还(?:需要|要|需)|after .{0,24}(?:complete|finish)|next step|continue (?:running|processing)|(?:currently|still) (?:running|working|processing)", RegexOptions.IgnoreCase); }
    string NodeText(object node) { var text = new StringBuilder(); CollectText(node, text, 0); return text.ToString(); }
    void CollectText(object node, StringBuilder text, int depth) {
      if (node == null || depth > 14 || text.Length >= 32768) return; string scalar = node as string; if (scalar != null) { text.Append(' ').Append(scalar); return; }
      var row = node as IDictionary<string, object>; if (row != null) { foreach (object value in row.Values) CollectText(value, text, depth + 1); return; }
      foreach (object item in Items(node)) CollectText(item, text, depth + 1);
    }
    string ResponseFingerprint(object response) {
      string raw = Util.Json.Serialize(response), tail = raw.Length > 4096 ? raw.Substring(raw.Length - 4096) : raw; unchecked { uint hash = 2166136261; foreach (char c in tail) { hash ^= c; hash *= 16777619; } return raw.Length + ":" + hash.ToString("x8"); }
    }

    public List<AgentTask> ParseLegacyLog(string file, long mtime) {
      int tailBytes = Path.GetFileName(file).IndexOf("ai-agent", StringComparison.OrdinalIgnoreCase) >= 0 ? 512 * 1024 : 256 * 1024; string text = Util.Tail(file, tailBytes);
      var sessions = new Dictionary<string, AgentTask>(); var sidRe = new Regex("(?:session_id|sessionId|chat_session_id|conversation_id|conversationId|task_id|taskId)[\\\"'\\s:=()]+([0-9a-z_-]{8,80})", RegexOptions.IgnoreCase);
      foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
        long parsedAt; bool reliable = TryLineAt(line, out parsedAt); long at = reliable ? parsedAt : mtime;
        bool start = Regex.IsMatch(line, "\\[ChatService\\]\\s*chat start|method[=:]\\s*start_chat|start_chat|task[^\\r\\n]{0,40}(?:start|creating)|status[\\\"' :=]+Creating|user[_ -]?(?:message|prompt)|prompt[_ -]?(?:submit|sent)|send[_ -]?(?:message|prompt)", RegexOptions.IgnoreCase);
        bool attention = Regex.IsMatch(line, "entering confirm flow|manual_approval_required|approval_required|permission_required|ask[_ -]?(?:user|human)|elicitation|waiting[_ -]?(?:for[_ -]?)?(?:user|approval|confirmation|reply|response|answer)|need(?:s|ed)?[_ -]?(?:user[_ -]?)?(?:input|confirm|approval|reply|response|answer)|正在向用户提问|等待(?:你|您|用户)的?(?:回复|回答|确认|选择)", RegexOptions.IgnoreCase);
        bool decision = Regex.IsMatch(line, "send_user_decision|approval[_ -]?(?:granted|accepted)|permission[_ -]?(?:granted|accepted)|user_decision[^\\r\\n]{0,40}(?:confirm|approve|accept)", RegexOptions.IgnoreCase);
        bool failed = Regex.IsMatch(line, "chat finished with error|:task_failed:|status[\\\"' :=]+(?:Failed|Error|Cancelled|Canceled)", RegexOptions.IgnoreCase);
        bool complete = Regex.IsMatch(line, "\\[ChatContextEntity\\]\\s*chat finished|chat finished|task[^\\r\\n]{0,40}(?:completed|finished|done)|status[\\\"' :=]+(?:Completed|Complete|Succeeded|Success|Done)", RegexOptions.IgnoreCase);
        bool running = Regex.IsMatch(line, "status[\\\"' :=]+(?:Running|InProgress|In_Progress)|tool[_ -]?(?:call|use)|toolcall|agent[^\\r\\n]{0,30}(?:run|step|stream)|message[_ -]?(?:delta|stream)|token[_ -]?(?:usage|count)", RegexOptions.IgnoreCase);
        if (!start && !attention && !decision && !failed && !complete && !running) continue; var match = sidRe.Match(line); string sid = match.Success ? match.Groups[1].Value : "file-" + Math.Abs(file.ToLowerInvariant().GetHashCode()).ToString("x");
        AgentTask task; if (!sessions.TryGetValue(sid, out task)) { task = new AgentTask { Id = "trae-log:" + sid, Source = "TRAE", SessionId = sid, Title = "TRAE Work", Status = State.Running, Detail = "正在执行", StartedAt = at, UpdatedAt = at, ExplicitStart = start, ReliableStart = start && reliable, Evidence = "TRAE 状态机 · 时间戳日志兜底" }; sessions[sid] = task; }
        if (at < task.UpdatedAt || (task.Status == State.Complete && !start)) continue; task.UpdatedAt = at;
        if (start) { task.Id = "trae-log:" + sid + ":" + at; task.StartedAt = at; task.ExplicitStart = true; task.ReliableStart = reliable; }
        if (start || running || decision) { task.Status = State.Running; task.Detail = decision ? "已确认，继续执行" : "正在执行"; }
        if (attention) { task.Status = State.Attention; task.Detail = "等待你的确认或输入"; }
        if (complete || failed) { task.Status = State.Complete; task.Detail = failed ? "任务失败并结束" : "任务已完成"; }
      }
      return new List<AgentTask>(sessions.Values);
    }
    bool TryLineAt(string line, out long at) { var match = Regex.Match(line, "(?<!\\d)(20\\d{2}-\\d{2}-\\d{2}[T ][0-2]\\d:[0-5]\\d:[0-5]\\d(?:\\.\\d+)?(?:Z|[+-][0-2]\\d:?[0-5]\\d)?)"); if (!match.Success) { at = 0; return false; } at = Util.At(match.Groups[1].Value, 0); return at > 0; }

    public IDictionary<string, object> LoadRoot(string file) {
      try {
        if (!file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) { var info = new FileInfo(file); if (info.Length <= 0 || info.Length > 32 * 1024 * 1024) return null; return Util.Json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>; }
        IDictionary<string, object> root = null; using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var reader = new StreamReader(stream, Encoding.UTF8, true, 16384)) {
          string line; while ((line = reader.ReadLine()) != null) { if (String.IsNullOrWhiteSpace(line)) continue; IDictionary<string, object> entry; try { entry = Util.Json.DeserializeObject(line) as IDictionary<string, object>; } catch { continue; } if (entry == null) continue;
            int kind = (int)Util.N(entry, "kind", -1); object value; entry.TryGetValue("v", out value); if (kind == 0) { root = value as IDictionary<string, object>; if (root == null) root = new Dictionary<string, object>(); RememberSnapshot(root, file); continue; }
            if (root == null) root = new Dictionary<string, object>(); object path; entry.TryGetValue("k", out path); ApplyPatch(root, kind, Items(path), value); RememberSnapshot(root, file);
          }
        }
        if (root != null && String.IsNullOrWhiteSpace(Util.S(root, "sessionId", ""))) root["sessionId"] = Path.GetFileNameWithoutExtension(file); return root;
      } catch { return null; }
    }
    void RememberSnapshot(IDictionary<string, object> root, string file) {
      object value; if (root == null || !root.TryGetValue("requests", out value)) return; int index; var request = LatestRequest(Items(value), out index); if (request == null) return;
      string session = Util.S(root, "sessionId", Path.GetFileNameWithoutExtension(file)), requestId = Util.S(request, "requestId", "request-" + (index + 1)); string key = session + ":" + requestId;
      IDictionary<string, object> model = Util.D(request, "modelState"); long state = Util.N(model, "value", -1); string raw = Util.Json.Serialize(request); bool terminal = state == 1 || state == 3 || Regex.IsMatch(raw, "\\\"(?:code|reason)\\\"\\s*:\\s*\\\"cancel(?:ed|led)\\\"|\\\"isCanceled\\\"\\s*:\\s*true", RegexOptions.IgnoreCase);
      object response; request.TryGetValue("response", out response); if (terminal) terminals[key] = new TerminalRecord { Fingerprint = ResponseFingerprint(response), ResponseCount = Items(response).Count, At = RequestClock(request, index, 0), Detail = state == 3 ? "任务失败并结束" : "任务已完成" };
    }
    public List<object> Items(object value) { var result = new List<object>(); if (value == null || value is string) return result; var enumerable = value as IEnumerable; if (enumerable != null) foreach (object item in enumerable) result.Add(item); return result; }
    int PathIndex(object key) { int value; return Int32.TryParse(Convert.ToString(key), out value) ? value : -1; }
    object PathGet(object container, object key) { var row = container as IDictionary<string, object>; if (row != null) { object value; return row.TryGetValue(Convert.ToString(key), out value) ? value : null; } var list = container as ArrayList; int index = PathIndex(key); return list != null && index >= 0 && index < list.Count ? list[index] : null; }
    void PathSet(object container, object key, object value) { var row = container as IDictionary<string, object>; if (row != null) { row[Convert.ToString(key)] = value; return; } var list = container as ArrayList; int index = PathIndex(key); if (list == null || index < 0) return; while (list.Count <= index) list.Add(null); list[index] = value; }
    object PathParent(IDictionary<string, object> root, List<object> path) { object current = root; for (int i = 0; i + 1 < path.Count; i++) { object next = PathGet(current, path[i]); if (next == null) { next = PathIndex(path[i + 1]) >= 0 ? (object)new ArrayList() : new Dictionary<string, object>(); PathSet(current, path[i], next); } current = next; } return current; }
    void ApplyPatch(IDictionary<string, object> root, int kind, List<object> path, object value) { if (root == null || path.Count == 0) return; object parent = PathParent(root, path), leaf = path[path.Count - 1]; if (kind == 1) { PathSet(parent, leaf, value); return; } if (kind == 2) { object target = PathGet(parent, leaf); var list = target as ArrayList; if (list == null) { list = new ArrayList(); PathSet(parent, leaf, list); } var values = value as IEnumerable; if (values != null && !(value is string) && !(value is IDictionary<string, object>)) foreach (object item in values) list.Add(item); else list.Add(value); return; } if (kind == 3) { var row = parent as IDictionary<string, object>; if (row != null) row.Remove(Convert.ToString(leaf)); var list = parent as ArrayList; int index = PathIndex(leaf); if (list != null && index >= 0 && index < list.Count) list.RemoveAt(index); } }

    public void AppendSanitizedSchema(object node, string path, int depth, StringBuilder text) {
      if (node == null || depth > 10 || text.Length > 32768) return; var row = node as IDictionary<string, object>;
      if (row != null) { text.Append(path).Append(" keys=[").Append(String.Join(",", new List<string>(row.Keys).ToArray())).Append("]\r\n"); foreach (var pair in row) { string key = pair.Key ?? "", lower = key.ToLowerInvariant(); if (Regex.IsMatch(lower, "^(?:text|content|prompt|message|options?|title|description|markdown|value)$")) continue; object value = pair.Value; string scalar = value as string; if (scalar != null) { if (Regex.IsMatch(lower, "kind|type|status|state|reason|name|tool|phase|result|requestid")) text.Append(path).Append('.').Append(key).Append('=').Append(Util.Clean(scalar, "")).Append("\r\n"); continue; } if (value is bool || value is int || value is long || value is double || value is decimal) { text.Append(path).Append('.').Append(key).Append('=').Append(Convert.ToString(value)).Append("\r\n"); continue; } AppendSanitizedSchema(value, path + "." + key, depth + 1, text); } return; }
      List<object> items = Items(node); for (int i = Math.Max(0, items.Count - 4); i < items.Count; i++) AppendSanitizedSchema(items[i], path + "[" + i + "]", depth + 1, text);
    }
  }

  // Display reducer for TRAE only. It is intentionally independent from the
  // generic Agent reducer because TRAE snapshots can move backwards while a
  // request is being compacted or rewritten in the background.
  sealed class TraeDisplayStateMachine {
    const long TerminalStableMs = 5000; string pendingTerminalId; long pendingTerminalSince;
    public AgentTask Resolve(AgentTask candidate, AgentTask previous, long runtimeStartedAt, long sourceSeenAt, long now) {
      if (candidate == null) { ResetPending(); return previous != null && (previous.Id ?? "").StartsWith("idle:", StringComparison.OrdinalIgnoreCase) ? AgentStateRules.Clone(previous) : AgentStateRules.Idle("TRAE"); }
      if (candidate.Status == State.Attention) { ResetPending(); return AgentStateRules.Clone(candidate); }
      bool explicitMcp = (candidate.Id ?? "").StartsWith("trae-mcp:", StringComparison.OrdinalIgnoreCase);
      if (explicitMcp) {
        if (previous != null && previous.Status == State.Complete && candidate.Status == State.Running) {
          bool newMcpTask = !String.Equals(previous.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) && candidate.UpdatedAt > previous.UpdatedAt;
          if (!newMcpTask) { ResetPending(); return AgentStateRules.Clone(previous); }
        }
        if (previous != null && previous.Status == State.Attention && candidate.Status == State.Running && candidate.UpdatedAt <= previous.UpdatedAt) { ResetPending(); return AgentStateRules.Clone(previous); }
        // MCP is an explicit lifecycle source. Unlike mutable log snapshots, a
        // running event remains valid when Agent Beacon itself is restarted.
        ResetPending(); return AgentStateRules.Clone(candidate);
      }
      long baseline = Math.Max(runtimeStartedAt, sourceSeenAt);
      if (candidate.Status == State.Running && baseline > 0 && candidate.UpdatedAt < baseline) { ResetPending(); return previous != null && (previous.Id ?? "").StartsWith("idle:", StringComparison.OrdinalIgnoreCase) ? AgentStateRules.Clone(previous) : AgentStateRules.Idle("TRAE"); }
      if (previous != null && previous.Status == State.Complete && candidate.Status == State.Running) {
        bool newRequest = candidate.ExplicitStart && candidate.ReliableStart && candidate.StartedAt > previous.UpdatedAt; if (!newRequest) { ResetPending(); return AgentStateRules.Clone(previous); }
      }
      if (previous != null && previous.Status == State.Attention && candidate.Status == State.Running && candidate.UpdatedAt <= previous.UpdatedAt) { ResetPending(); return AgentStateRules.Clone(previous); }
      if (candidate.Status != State.Complete || previous == null || previous.Status == State.Complete) { ResetPending(); return AgentStateRules.Clone(candidate); }
      string id = candidate.Id ?? "trae-terminal"; if (!String.Equals(id, pendingTerminalId, StringComparison.OrdinalIgnoreCase)) { pendingTerminalId = id; pendingTerminalSince = now; return AgentStateRules.Clone(previous); }
      if (now - pendingTerminalSince < TerminalStableMs) return AgentStateRules.Clone(previous); ResetPending(); return AgentStateRules.Clone(candidate);
    }
    void ResetPending() { pendingTerminalId = null; pendingTerminalSince = 0; }
    public void Reset() { ResetPending(); }
  }
}
