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
