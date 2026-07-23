using System.Reflection;

[assembly: AssemblyTitle("Agent Beacon")]
[assembly: AssemblyDescription("Windows native status lights for AI coding agents")]
[assembly: AssemblyCompany("LAUFLO")]
[assembly: AssemblyProduct("Agent Beacon")]
[assembly: AssemblyCopyright("Copyright © LAUFLO 2026")]
[assembly: AssemblyVersion("1.4.2.0")]
[assembly: AssemblyFileVersion("1.4.2.0")]
[assembly: AssemblyInformationalVersion("1.4.2")]

namespace AgentTrafficLightNative {
  static class AppInfo {
    public const string Version = "1.4.2";
    public const string Repository = "LAUFLO/agent-beacon";
    public const string ReleaseApi = "https://api.github.com/repos/LAUFLO/agent-beacon/releases/latest";
  }
}
