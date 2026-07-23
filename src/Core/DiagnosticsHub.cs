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
  static class DiagnosticsHub {
    static readonly object Sync = new object();
    static readonly Dictionary<string, string> Lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static long lastScanAt, lastDuration, lastManagedMemoryMb, lastPrivateMemoryMb, lastWorkingSetMb, lastPersistAt; static int lastHandleCount, lastFiles, lastInterval; static string lastError = "", lastStateSignature = "", lastPersisted = "";
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
        lastScanAt = Util.Now(); lastDuration = cycle == null ? 0 : cycle.DurationMs; lastManagedMemoryMb = cycle == null ? 0 : cycle.ManagedMemoryMb; lastPrivateMemoryMb = cycle == null ? 0 : cycle.PrivateMemoryMb; lastWorkingSetMb = cycle == null ? 0 : cycle.WorkingSetMb; lastHandleCount = cycle == null ? 0 : cycle.HandleCount; lastFiles = cycle == null ? 0 : cycle.FilesRead; lastInterval = cycle == null ? 0 : cycle.EffectiveIntervalMs; lastError = cycle == null ? "" : (cycle.Error ?? "");
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
        return "Agent Beacon " + AppInfo.Version + " 诊断（不包含聊天正文）" + Environment.NewLine + "扫描: " + at + " · " + lastDuration + "ms · 读取 " + lastFiles + " 个变化文件 · 间隔 " + lastInterval + "ms" + Environment.NewLine + "内存: 托管 " + lastManagedMemoryMb + "MB / 私有 " + lastPrivateMemoryMb + "MB / 工作集 " + lastWorkingSetMb + "MB · 句柄 " + lastHandleCount + (String.IsNullOrWhiteSpace(lastError) ? "" : Environment.NewLine + "错误: " + lastError) + Environment.NewLine + Summary();
      }
    }
    static void Persist(bool force) { try { long now = Util.Now(); if (!force && lastPersistAt != 0 && now - lastPersistAt < 30000) return; string report = Report(); if (report == lastPersisted && !force) return; Directory.CreateDirectory(Path.GetDirectoryName(FilePath)); File.WriteAllText(FilePath, report, new UTF8Encoding(false)); lastPersisted = report; lastPersistAt = now; } catch { } }
  }
}
