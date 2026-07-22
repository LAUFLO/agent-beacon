using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
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
        Process[] helpers = Process.GetProcessesByName("Agent-Beacon-MCP"); bool alive = helpers.Length > 0; foreach (Process helper in helpers) helper.Dispose(); if (alive) return true;
        return IsTraeMcpRecent(TraeMcpLastSeen(), Util.Now());
      } catch { }
      return false;
    }
    public static long TraeMcpLastSeen() {
      long latest = 0;
      try {
        if (!Directory.Exists(Util.BridgeDir)) return 0;
        foreach (string file in Directory.GetFiles(Util.BridgeDir, "trae-mcp-*.json")) {
          try {
            var row = Util.Json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>; if (row == null || Util.S(row, "source", "") != "TRAE") continue;
            string integration = Util.S(row, "integration", ""); if (integration != "mcp" && integration != "mcp-health") continue;
            latest = Math.Max(latest, Util.N(row, "updatedAt", new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds()));
          } catch { }
        }
      } catch { }
      return latest;
    }
    public static bool IsTraeMcpRecent(long seen, long now) { return seen > 0 && now >= seen && now - seen <= 10 * 60 * 1000; }
    public static bool IsTraeMcpReadyAndConnected() { return IsTraeMcpPrepared() && IsTraeMcpConnected(); }
    public static string TraeMcpStatus() {
      if (IsTraeMcpUpdatePending()) return "■ 关闭 TRAE 后重启灯，完成 MCP 更新";
      if (File.Exists(TraeMcpExecutable) && !IsTraeMcpConfigCurrent()) return "■ 需重新配置 TRAE MCP";
      if (!IsTraeMcpPrepared()) return "■ 未配置";
      long seen = TraeMcpLastSeen(); string time = seen > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(seen).ToLocalTime().ToString("HH:mm") : "";
      if (IsTraeMcpConnected()) return "■ 最近通信" + (time.Length > 0 ? " " + time : "");
      return seen > 0 ? "■ 已失联 " + time : "■ 已配置，待通信";
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
}
