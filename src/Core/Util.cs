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
  static class Util {
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    public static string Home { get {
      string configured = Environment.GetEnvironmentVariable("AGENT_TRAFFIC_LIGHT_HOME"); if (!String.IsNullOrWhiteSpace(configured)) return configured;
      string profile = Environment.GetEnvironmentVariable("USERPROFILE"); if (!String.IsNullOrWhiteSpace(profile) && Directory.Exists(profile)) return profile;
      string drive = Environment.GetEnvironmentVariable("HOMEDRIVE"), path = Environment.GetEnvironmentVariable("HOMEPATH");
      if (!String.IsNullOrWhiteSpace(drive) && !String.IsNullOrWhiteSpace(path) && Directory.Exists(drive + path)) return drive + path;
      return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    } }
    public static string BridgeDir { get { return Path.Combine(Home, ".agent-traffic-light", "events"); } }
    public static string IntegrationDir { get { return Path.Combine(Home, ".agent-traffic-light", "integrations"); } }
    public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    public static string S(IDictionary<string, object> d, string key, string fallback) {
      object value; return d != null && d.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : fallback;
    }
    public static IDictionary<string, object> D(IDictionary<string, object> d, string key) {
      object value; return d != null && d.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
    }
    public static long N(IDictionary<string, object> d, string key, long fallback) {
      object value; long result; return d != null && d.TryGetValue(key, out value) && value != null && Int64.TryParse(Convert.ToString(value), out result) ? result : fallback;
    }
    public static string Clean(string text, string fallback) {
      if (String.IsNullOrWhiteSpace(text)) return fallback;
      text = Regex.Replace(text, "<recommended_plugins>[\\s\\S]*?</recommended_plugins>", "", RegexOptions.IgnoreCase);
      text = Regex.Replace(text, "<[^>]+>", " ");
      text = Regex.Replace(text, "\\s+", " ").Trim();
      if (text.Length == 0) return fallback;
      return text.Length > 64 ? text.Substring(0, 61) + "…" : text;
    }
    public static long At(string text, long fallback) {
      DateTimeOffset dto; return DateTimeOffset.TryParse(text, out dto) ? dto.ToUnixTimeMilliseconds() : fallback;
    }
    public static IEnumerable<string> Files(string root, Regex name, DateTime cutoff, int max) {
      var found = new List<Tuple<string, DateTime>>();
      var stack = new Stack<string>(); stack.Push(root); int visited = 0;
      while (stack.Count > 0 && visited++ < 600) {
        var dir = stack.Pop();
        try {
          foreach (var child in Directory.GetDirectories(dir)) { string folder = Path.GetFileName(child); if (!Regex.IsMatch(folder, "^(?:Cache|Code Cache|GPUCache|Crashpad|blob_storage|Service Worker|node_modules|CachedData)$", RegexOptions.IgnoreCase)) stack.Push(child); }
          foreach (var file in Directory.GetFiles(dir)) {
            if (!name.IsMatch(Path.GetFileName(file))) continue;
            var modified = File.GetLastWriteTimeUtc(file);
            if (modified >= cutoff && new FileInfo(file).Length > 0) found.Add(Tuple.Create(file, modified));
          }
        } catch { }
      }
      found.Sort(delegate(Tuple<string, DateTime> a, Tuple<string, DateTime> b) { return b.Item2.CompareTo(a.Item2); });
      for (int i = 0; i < Math.Min(max, found.Count); i++) yield return found[i].Item1;
    }
    public static string Tail(string file, int maxBytes) {
      using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
        long start = Math.Max(0, stream.Length - maxBytes); stream.Position = start;
        var buffer = new byte[stream.Length - start]; int read = stream.Read(buffer, 0, buffer.Length);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        if (start > 0) { int nl = text.IndexOf('\n'); if (nl >= 0) text = text.Substring(nl + 1); }
        return text;
      }
    }
  }
}
