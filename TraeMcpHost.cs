using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AgentBeaconTraeMcp {
  // Console-subsystem MCP helper embedded in the Agent Beacon desktop EXE.
  // The settings installer extracts it locally, and TRAE communicates with it
  // through newline-delimited JSON-RPC over stdio.
  static class TraeMcpHost {
    const string Version = "1.6.0";
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    static readonly string ProcessSession = "mcp-" + Process.GetCurrentProcess().Id + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string Home { get { string configured = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_HOME"); if (!String.IsNullOrWhiteSpace(configured)) return configured; string profile = Environment.GetEnvironmentVariable("USERPROFILE"); return String.IsNullOrWhiteSpace(profile) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : profile; } }
    static string BridgeDir { get { return Path.Combine(Home, ".agent-traffic-light", "events"); } }

    public static int Main(string[] args) {
      Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
      try {
        using (var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
        using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }) {
          string line;
          while ((line = input.ReadLine()) != null) {
            if (String.IsNullOrWhiteSpace(line)) continue;
            IDictionary<string, object> request; try { request = Json.DeserializeObject(line) as IDictionary<string, object>; } catch { continue; }
            if (request == null) continue; object id; bool hasId = request.TryGetValue("id", out id); if (!hasId) continue;
            string method = S(request, "method", ""); TouchHealth(true, method);
            try { Write(output, Success(id, Handle(method, D(request, "params")))); }
            catch (ArgumentException ex) { Write(output, Error(id, -32602, ex.Message)); }
            catch (NotSupportedException ex) { Write(output, Error(id, -32601, ex.Message)); }
            catch (Exception ex) { Write(output, Error(id, -32603, "Agent Beacon MCP error: " + ex.Message)); }
          }
        }
      } finally { TouchHealth(false, "disconnect"); }
      return 0;
    }

    static object Handle(string method, IDictionary<string, object> parameters) {
      if (method == "initialize") {
        string requested = S(parameters, "protocolVersion", "2024-11-05");
        var info = Obj("name", "agent-beacon-trae", "version", Version);
        var tools = Obj("listChanged", false); var caps = Obj("tools", tools);
        return Obj("protocolVersion", requested, "capabilities", caps, "serverInfo", info);
      }
      if (method == "ping") return new Dictionary<string, object>();
      if (method == "tools/list") {
        var stateProperty = Obj("type", "string", "enum", new[] { "running", "waiting", "completed", "failed", "cancelled" });
        var properties = Obj("state", stateProperty, "session_id", Obj("type", "string"));
        var schema = Obj("type", "object", "properties", properties, "required", new[] { "state", "session_id" }, "additionalProperties", false);
        var tool = Obj("name", "agent_beacon_report_state", "description", "上报任务状态。", "inputSchema", schema);
        return Obj("tools", new object[] { tool });
      }
      if (method == "tools/call") return CallTool(parameters);
      if (method == "resources/list") return Obj("resources", new object[0]);
      if (method == "prompts/list") return Obj("prompts", new object[0]);
      throw new NotSupportedException("Unsupported method: " + method);
    }

    static object CallTool(IDictionary<string, object> parameters) {
      if (S(parameters, "name", "") != "agent_beacon_report_state") throw new ArgumentException("Unknown tool name");
      var arguments = D(parameters, "arguments") ?? new Dictionary<string, object>();
      string input = S(arguments, "state", "").Trim().ToLowerInvariant(), status, fallback;
      if (input == "running") { status = "running"; fallback = "正在执行"; }
      else if (input == "waiting") { status = "attention"; fallback = "等待你的确认或回答"; }
      else if (input == "completed") { status = "complete"; fallback = "任务已完成"; }
      else if (input == "failed") { status = "complete"; fallback = "任务失败并结束"; }
      else if (input == "cancelled") { status = "complete"; fallback = "任务已取消"; }
      else throw new ArgumentException("state must be running, waiting, completed, failed or cancelled");

      string session = SafeId(S(arguments, "session_id", ProcessSession), ProcessSession), taskId = session;
      long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), started = now; string path = Path.Combine(BridgeDir, "trae-mcp-" + Hash(session) + ".json");
      try { if (File.Exists(path)) { var old = Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IDictionary<string, object>; if (old != null && S(old, "sessionId", "") == session) started = N(old, "startedAt", now); } } catch { }
      string phase = input == "running" ? "处理中" : input == "waiting" ? "等待确认" : input == "completed" ? "已完成" : input == "failed" ? "已失败" : "已取消";
      var row = Obj("source", "TRAE", "integration", "mcp", "helperVersion", Version, "id", "trae-mcp:" + taskId, "sessionId", session, "title", "TRAE Work", "status", status, "detail", fallback, "phase", phase, "startedAt", started, "lastActivityAt", now, "updatedAt", now, "explicitStart", true, "reliableStart", true, "reportedState", input);
      AtomicWrite(path, Json.Serialize(row));
      string reply = input == "waiting" ? "ok; 立即显示确认；回复后先 running" : input == "completed" ? "ok; 立即输出最终答复" : "ok";
      var content = Obj("type", "text", "text", reply);
      return Obj("content", new object[] { content }, "isError", false);
    }

    static Dictionary<string, object> Obj(params object[] pairs) { var value = new Dictionary<string, object>(); for (int i = 0; i + 1 < pairs.Length; i += 2) value[Convert.ToString(pairs[i])] = pairs[i + 1]; return value; }
    static string S(IDictionary<string, object> row, string key, string fallback) { object value; return row != null && row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback; }
    static IDictionary<string, object> D(IDictionary<string, object> row, string key) { object value; return row != null && row.TryGetValue(key, out value) ? value as IDictionary<string, object> : null; }
    static long N(IDictionary<string, object> row, string key, long fallback) { object value; long parsed; return row != null && row.TryGetValue(key, out value) && value != null && Int64.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback; }
    static string SafeId(string value, string fallback) { if (String.IsNullOrWhiteSpace(value)) return fallback; var text = new StringBuilder(); foreach (char c in value.Trim()) if (Char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ':') text.Append(c); if (text.Length < 3) return fallback; return text.Length > 128 ? text.ToString(0, 128) : text.ToString(); }
    static string Hash(string value) { using (var sha = SHA256.Create()) { byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value)); var text = new StringBuilder(); for (int i = 0; i < 10; i++) text.Append(bytes[i].ToString("x2")); return text.ToString(); } }
    static void TouchHealth(bool connected, string activity) { try { long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var row = Obj("source", "TRAE", "integration", "mcp-health", "helperVersion", Version, "processId", Process.GetCurrentProcess().Id, "processSession", ProcessSession, "connected", connected, "activity", activity, "updatedAt", now); AtomicWrite(Path.Combine(BridgeDir, "trae-mcp-health.json"), Json.Serialize(row)); } catch { } }
    static void AtomicWrite(string path, string content) { Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp = path + "." + Process.GetCurrentProcess().Id + ".tmp"; File.WriteAllText(temp, content, new UTF8Encoding(false)); if (File.Exists(path)) { try { File.Replace(temp, path, null); return; } catch { } } if (File.Exists(path)) File.Delete(path); File.Move(temp, path); }
    static IDictionary<string, object> Success(object id, object result) { return Obj("jsonrpc", "2.0", "id", id, "result", result); }
    static IDictionary<string, object> Error(object id, int code, string message) { return Obj("jsonrpc", "2.0", "id", id, "error", Obj("code", code, "message", message)); }
    static void Write(StreamWriter output, object value) { output.WriteLine(Json.Serialize(value)); }
  }
}
