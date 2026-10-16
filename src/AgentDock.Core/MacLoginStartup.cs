using System.Text.Json;
using System.Xml.Linq;

namespace AgentDock.Core;

/// <summary>Opt-in desktop login registration; separate from on-demand Host launch.</summary>
public static class MacLoginStartup
{
    public static string RegistrationPath(string home) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents",
        "org.agentdock.login." + DockClient.PipeFor(home) + ".plist");

    public static void Configure(string home, bool enabled)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var path = RegistrationPath(home);
        if (!enabled) { File.Delete(path); return; }
        var root = Path.GetDirectoryName(Path.GetFullPath(home))!;
        var install = Path.Combine(root, "plugin", "plugins", "agentdock", "runtime", "install.json");
        if (!File.Exists(install)) throw new DockException("DesktopMissing", "请先安装 AgentDock，再设置登录自启。");
        using var document = JsonDocument.Parse(File.ReadAllText(install));
        var launcher = document.RootElement.GetProperty("launcher").GetString();
        if (string.IsNullOrEmpty(launcher) || !Path.IsPathFullyQualified(launcher) || !File.Exists(launcher))
            throw new DockException("DesktopMissing", "安装入口不可用，请修复 AgentDock 安装。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("plist", new XAttribute("version", "1.0"),
            new XElement("dict",
                new XElement("key", "Label"), new XElement("string", Path.GetFileNameWithoutExtension(path)),
                new XElement("key", "ProgramArguments"), new XElement("array",
                    new XElement("string", launcher), new XElement("string", "--launch"), new XElement("string", "--tray"),
                    new XElement("string", "--root"), new XElement("string", root)),
                new XElement("key", "RunAtLoad"), new XElement("true"),
                new XElement("key", "KeepAlive"), new XElement("false"))));
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            plist.Save(temporary);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
