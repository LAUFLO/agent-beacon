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
}
