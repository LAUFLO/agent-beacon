using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AgentTrafficLightNative {
  // Keeps Codex session-log schema changes isolated from the state machine.
  static class CodexEventCompatibility {
    static readonly HashSet<string> ToolCalls = new HashSet<string>(new[] { "custom_tool_call", "function_call" }, StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> ToolOutputs = new HashSet<string>(new[] { "custom_tool_call_output", "function_call_output" }, StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> ExecNames = new HashSet<string>(new[] { "exec", "exec_command", "shell", "run_command" }, StringComparer.OrdinalIgnoreCase);

    public static bool IsToolCall(string payloadType) { return ToolCalls.Contains(payloadType ?? ""); }
    public static bool IsToolOutput(string payloadType) { return ToolOutputs.Contains(payloadType ?? ""); }
    public static bool IsExec(string name) { return ExecNames.Contains(name ?? ""); }
    public static string CallId(IDictionary<string, object> payload) {
      string value = Util.S(payload, "call_id", Util.S(payload, "id", ""));
      return String.IsNullOrWhiteSpace(value) ? "*" : value;
    }
    public static string Input(IDictionary<string, object> payload) {
      return Util.S(payload, "input", Util.S(payload, "arguments", Util.S(payload, "params", "")));
    }
    public static bool IsExplicitInteraction(string name, string input) {
      return Regex.IsMatch(name ?? "", "^(?:request_permissions?|request_user_input|elicitation|approval(?:_request)?)$", RegexOptions.IgnoreCase)
        || Regex.IsMatch(input ?? "", @"tools\s*\.\s*(?:request_permissions?|request_user_input|elicitation|approval(?:_request)?)\s*\(", RegexOptions.IgnoreCase);
    }
    public static bool IsComputerUseAction(string name, string input) {
      bool nodeTool = Regex.IsMatch(name ?? "", @"^(?:js|mcp__node_repl__js|node_repl(?:__|\.)js)$", RegexOptions.IgnoreCase);
      bool directTool = Regex.IsMatch(name ?? "", @"computer[-_]?use", RegexOptions.IgnoreCase);
      if (!nodeTool && !directTool) return false;
      return directTool || Regex.IsMatch(input ?? "", @"\bsky\s*\.\s*(?:launch_app|click|type_text|set_value|press_key|drag|scroll|perform_secondary_action)\s*\(", RegexOptions.IgnoreCase);
    }
  }
}
