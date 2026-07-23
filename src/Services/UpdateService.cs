using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AgentTrafficLightNative {
  sealed class UpdateInfo {
    public string Version;
    public string Tag;
    public string DownloadUrl;
    public string Sha256;
    public string ReleaseUrl;
  }

  static class UpdateService {
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

    public static UpdateInfo CheckLatest() {
      ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
      string payload;
      using (var client = Client()) payload = client.DownloadString(AppInfo.ReleaseApi);
      return ParseRelease(payload);
    }

    public static UpdateInfo ParseRelease(string payload) {
      var release = Json.DeserializeObject(payload) as IDictionary<string, object>; if (release == null) throw new InvalidDataException("GitHub 返回了无效的版本信息。");
      string tag = S(release, "tag_name", ""); Version remote, current;
      if (!Version.TryParse(tag.TrimStart('v', 'V'), out remote) || !Version.TryParse(AppInfo.Version, out current)) throw new InvalidDataException("无法解析版本号：" + tag);
      if (remote <= current) return null;
      string wanted = "Agent-Beacon-" + remote + ".exe", download = "", digest = "", hashUrl = "";
      object assetsObject; var assets = release.TryGetValue("assets", out assetsObject) ? assetsObject as IEnumerable : null;
      if (assets != null) foreach (object value in assets) {
        var asset = value as IDictionary<string, object>; if (asset == null) continue; string name = S(asset, "name", "");
        if (String.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) { download = S(asset, "browser_download_url", ""); digest = S(asset, "digest", ""); }
        else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) hashUrl = S(asset, "browser_download_url", "");
      }
      if (String.IsNullOrWhiteSpace(download)) throw new InvalidDataException("新版本没有找到 Windows EXE：" + wanted);
      string hash = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest.Substring(7) : "";
      if (String.IsNullOrWhiteSpace(hash) && !String.IsNullOrWhiteSpace(hashUrl)) using (var client = Client()) hash = ParseHash(client.DownloadString(hashUrl));
      if (!IsHash(hash)) throw new InvalidDataException("新版本缺少可验证的 SHA-256，已停止更新。");
      return new UpdateInfo { Version = remote.ToString(), Tag = tag, DownloadUrl = download, Sha256 = hash.ToUpperInvariant(), ReleaseUrl = S(release, "html_url", "") };
    }

    public static string Download(UpdateInfo update) { return Download(update, null); }

    public static string Download(UpdateInfo update, Action<int, string> report) {
      if (update == null) throw new ArgumentNullException("update");
      string directory = Path.Combine(Path.GetTempPath(), "AgentBeaconUpdates", update.Tag); Directory.CreateDirectory(directory);
      string path = Path.Combine(directory, "Agent-Beacon-" + update.Version + ".exe"), partial = path + ".download";
      if (File.Exists(partial)) File.Delete(partial); Report(report, 0, "准备下载…");
      var request = (HttpWebRequest)WebRequest.Create(update.DownloadUrl); request.UserAgent = "Agent-Beacon/" + AppInfo.Version; request.Accept = "application/octet-stream"; request.AllowAutoRedirect = true; request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
      using (var response = (HttpWebResponse)request.GetResponse()) using (var source = response.GetResponseStream()) using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None)) {
        long total = response.ContentLength, received = 0; byte[] buffer = new byte[65536]; int read, last = -1;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) { target.Write(buffer, 0, read); received += read; int percent = total > 0 ? Math.Min(85, (int)(received * 85L / total)) : Math.Min(85, last + 1); if (percent != last) { last = percent; Report(report, percent, "正在下载…"); } }
      }
      Report(report, 90, "正在校验文件…"); string actual = Hash(partial); if (!String.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(partial); throw new InvalidDataException("更新包 SHA-256 校验失败。"); }
      Report(report, 96, "正在校验版本…"); string productVersion = FileVersionInfo.GetVersionInfo(partial).ProductVersion ?? ""; if (!productVersion.StartsWith(update.Version, StringComparison.OrdinalIgnoreCase)) { File.Delete(partial); throw new InvalidDataException("更新包内部版本与 GitHub 标签不一致。"); }
      if (File.Exists(path)) File.Delete(path); File.Move(partial, path); Report(report, 100, "准备安装…"); return path;
    }

    public static void LaunchApply(string downloaded, string target) {
      string arguments = "--apply-update " + Process.GetCurrentProcess().Id + " \"" + target.Replace("\"", "\\\"") + "\"";
      Process.Start(new ProcessStartInfo { FileName = downloaded, Arguments = arguments, UseShellExecute = true });
    }

    public static bool TryApplyFromArguments(string[] args) {
      if (args == null || args.Length < 3 || args[0] != "--apply-update") return false;
      int oldPid; if (!Int32.TryParse(args[1], out oldPid)) return true; string target = Path.GetFullPath(args[2]); if (!IsSafeUpdateTarget(target)) { PixelDialog.Show("自动更新目标无效，已停止替换。", "自动更新"); return true; }
      try { try { Process.GetProcessById(oldPid).WaitForExit(20000); } catch { }
        string source = Application.ExecutablePath, staged = target + ".new", backup = target + ".old";
        if (File.Exists(staged)) File.Delete(staged); File.Copy(source, staged, true);
        if (File.Exists(target)) { if (File.Exists(backup)) File.Delete(backup); File.Replace(staged, target, backup, true); } else File.Move(staged, target);
        Process.Start(new ProcessStartInfo { FileName = target, Arguments = "--updated", UseShellExecute = true });
      } catch (Exception ex) { PixelDialog.Show("自动更新失败：\n" + ex.Message + "\n\n可以继续从 GitHub Release 手动替换。", "自动更新"); }
      return true;
    }

    public static void CleanupOldUpdate() { try { string old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); string root = Path.Combine(Path.GetTempPath(), "AgentBeaconUpdates"); if (Directory.Exists(root)) foreach (string directory in Directory.GetDirectories(root)) try { if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) Directory.Delete(directory, true); } catch { } } catch { } }
    public static bool IsSafeUpdateTarget(string target) { try { return Regex.IsMatch(Path.GetFileName(Path.GetFullPath(target)), "^Agent-Beacon(?:-[0-9]+(?:\\.[0-9]+){1,3})?\\.exe$", RegexOptions.IgnoreCase); } catch { return false; } }
    static WebClient Client() { var client = new WebClient(); client.Encoding = Encoding.UTF8; client.Headers[HttpRequestHeader.UserAgent] = "Agent-Beacon/" + AppInfo.Version; client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json"; return client; }
    static string S(IDictionary<string, object> row, string key, string fallback) { object value; return row != null && row.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback; }
    static string ParseHash(string text) { if (String.IsNullOrWhiteSpace(text)) return ""; string first = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0]; return first; }
    static bool IsHash(string value) { if (String.IsNullOrWhiteSpace(value) || value.Length != 64) return false; foreach (char c in value) if (!Uri.IsHexDigit(c)) return false; return true; }
    static string Hash(string file) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(file)) { byte[] bytes = sha.ComputeHash(stream); var text = new StringBuilder(); foreach (byte value in bytes) text.Append(value.ToString("X2")); return text.ToString(); } }
    static void Report(Action<int, string> report, int value, string message) { if (report != null) report(value, message); }
  }
}
