using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AgentBeaconCodexHook {
  // Console-subsystem helper embedded in the Agent Beacon desktop EXE.
  // Codex invokes it as a lifecycle hook; it reads the event JSON from
  // stdin and writes a state snapshot to the shared bridge directory.
  // For Stop events it outputs empty JSON on stdout so Codex continues
  // its normal turn lifecycle.
  static class CodexHookHost {
    const string Version = "1.6.2";
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 512 * 1024 };
    static readonly string ProcessId = Process.GetCurrentProcess().Id + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    static string Home { get {
      string configured = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_HOME");
      if (!String.IsNullOrWhiteSpace(configured)) return configured;
      string profile = Environment.GetEnvironmentVariable("USERPROFILE");
      return String.IsNullOrWhiteSpace(profile) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : profile;
    } }
    static string BridgeDir { get { return Path.Combine(Home, ".agent-traffic-light", "events"); } }

    static string SafeId(string value, string fallback) {
      if (String.IsNullOrWhiteSpace(value)) return fallback;
      var text = new StringBuilder();
      foreach (char c in value.Trim())
        if (Char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ':') text.Append(c);
      if (text.Length < 3) return fallback;
      return text.Length > 128 ? text.ToString(0, 128) : text.ToString();
    }

    static string Hash(string value) {
      using (var sha = SHA256.Create()) {
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var text = new StringBuilder();
        for (int i = 0; i < 10; i++) text.Append(bytes[i].ToString("x2"));
        return text.ToString();
      }
    }

    static void AtomicWrite(string path, string content) {
      Directory.CreateDirectory(Path.GetDirectoryName(path));
      string temp = path + "." + Process.GetCurrentProcess().Id + ".tmp";
      File.WriteAllText(temp, content, new UTF8Encoding(false));
      if (File.Exists(path)) { try { File.Replace(temp, path, null); return; } catch { } }
      if (File.Exists(path)) File.Delete(path);
      File.Move(temp, path);
    }

    static string S(IDictionary<string, object> d, string key, string fallback) {
      object value; return d != null && d.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
    }

    static long N(IDictionary<string, object> d, string key, long fallback) {
      object value; long result;
      return d != null && d.TryGetValue(key, out value) && value != null && Int64.TryParse(Convert.ToString(value), out result) ? result : fallback;
    }

    // Determine the phase label from tool name and event context.
    static string PhaseForTool(string toolName) {
      if (String.IsNullOrWhiteSpace(toolName)) return "处理中";
      if (toolName == "Bash" || toolName == "exec_command" || toolName == "shell") return "执行命令";
      if (toolName == "apply_patch" || toolName == "Edit" || toolName == "Write") return "应用修改";
      if (toolName.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return "调用 MCP";
      return "执行工具";
    }

    public static int Main(string[] args) {
      Console.InputEncoding = Encoding.UTF8;
      Console.OutputEncoding = new UTF8Encoding(false);

      // Read entire stdin (hook payload).
      string body;
      try {
        using (var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8)) {
          body = reader.ReadToEnd();
        }
      } catch {
        // Cannot read stdin at all – exit silently.
        return 0;
      }

      if (String.IsNullOrWhiteSpace(body)) return 0;

      IDictionary<string, object> input;
      try {
        input = Json.DeserializeObject(body) as IDictionary<string, object>;
      } catch {
        return 0;
      }
      if (input == null) return 0;

      string eventName = S(input, "hook_event_name", "");
      string sessionId = SafeId(S(input, "session_id", "codex-session"), "codex-session");
      string cwd = S(input, "cwd", "");
      long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

      // Map event → status, detail, phase.
      string status = "running";
      string detail = "正在执行";
      string phase = "处理中";
      string toolName = "";

      switch (eventName) {
        case "SessionStart":
          status = "running";
          detail = "会话已开始";
          phase = "处理中";
          break;

        case "PreToolUse":
          toolName = S(input, "tool_name", "");
          status = "running";
          detail = "正在执行";
          phase = PhaseForTool(toolName);
          break;

        case "PermissionRequest":
          toolName = S(input, "tool_name", "");
          status = "attention";
          detail = "等待你的确认或输入";
          phase = "等待确认";
          break;

        case "PostToolUse":
          toolName = S(input, "tool_name", "");
          status = "running";
          detail = "工具执行完成，继续处理";
          phase = "处理工具结果";
          break;

        case "UserPromptSubmit":
          status = "running";
          detail = "正在处理你的输入";
          phase = "处理中";
          break;

        case "Stop":
          // Turn ended. Write complete status to bridge so any prior
          // attention (PermissionRequest) is explicitly cleared.
          // The 30-min idle timeout in ParseBridge serves as a second
          // layer; this write is the primary cleanup path.
          status = "complete";
          detail = "当前轮次已完成";
          phase = "已结束";
          break;

        default:
          // Unknown event – skip.
          break;
      }

      // Handle Stop: acknowledge Codex and let bridge write proceed.
      // The bridge write with status=complete will overwrite any prior
      // attention state from the same session, clearing the yellow light.
      if (eventName == "Stop") {
        // Output empty JSON object as required by Stop hook contract.
        Console.WriteLine("{}");
        // Fall through to write complete status to bridge file.
      }

      // Skip if we didn't map a meaningful state.
      if (String.IsNullOrWhiteSpace(status)) return 0;

      // Build bridge file path (same pattern as other integrations).
      string safeSession = Regex.Replace(sessionId, "[^a-z0-9_.-]+", "_", RegexOptions.IgnoreCase);
      if (safeSession.Length > 120) safeSession = safeSession.Substring(0, 120);
      string bridgePath = Path.Combine(BridgeDir, "codex-hook-" + safeSession + ".json");

      // Merge with previous state.
      IDictionary<string, object> previous = null;
      try {
        if (File.Exists(bridgePath)) {
          previous = Json.DeserializeObject(File.ReadAllText(bridgePath, Encoding.UTF8)) as IDictionary<string, object>;
        }
      } catch { }

      long startedAt = now;
      string previousInteraction = null;
      if (previous != null) {
        startedAt = N(previous, "startedAt", now);
        previousInteraction = S(previous, "interactionId", null);
      }

      // For PermissionRequest: set interactionId so PostToolUse can clear it.
      string interactionId = previousInteraction;
      if (eventName == "PermissionRequest") {
        interactionId = S(input, "tool_use_id", S(input, "turn_id", ""));
      } else if (eventName == "PostToolUse" || eventName == "Stop") {
        // Clear pending interaction on tool completion or turn end.
        interactionId = null;
      }

      var row = new Dictionary<string, object> {
        ["source"] = "Codex",
        ["integration"] = "hook",
        ["helperVersion"] = Version,
        ["id"] = "codex-hook:" + safeSession,
        ["sessionId"] = sessionId,
        ["title"] = "Codex 任务",
        ["status"] = status,
        ["detail"] = detail,
        ["phase"] = phase,
        ["eventType"] = eventName,
        ["cwd"] = cwd,
        ["startedAt"] = startedAt,
        ["lastActivityAt"] = now,
        ["updatedAt"] = now,
        ["explicitStart"] = true,
        ["reliableStart"] = true
      };

      if (!String.IsNullOrWhiteSpace(toolName)) row["toolName"] = toolName;
      if (interactionId != null) row["interactionId"] = interactionId;

      // Copy previous fields that should persist.
      if (previous != null) {
        string prevTitle = S(previous, "title", null);
        if (prevTitle != null && prevTitle != "Codex 任务") row["title"] = prevTitle;

        // If previous was attention and we're now running, record the resolution.
        if (S(previous, "status", "") == "attention" && status == "running" && eventName != "PermissionRequest") {
          row["resolvedInteraction"] = S(previous, "interactionId", "");
          row["detail"] = "已确认，继续执行";
          row["phase"] = "继续处理";
        }
      }

      AtomicWrite(bridgePath, Json.Serialize(row));

      // SessionStart and UserPromptSubmit can optionally supply developer
      // context – return empty for now.
      if (eventName == "SessionStart" || eventName == "UserPromptSubmit") {
        Console.WriteLine("{}");
      }

      return 0;
    }
  }
}
