using System.Text.Json;
using System.Xml.Linq;
using AgentDock.Core;
using Xunit;

namespace AgentDock.Tests;

public sealed class MacInstallIntegrationTests : IDisposable
{
    private readonly string root = ProjectPaths.Canonicalize(Path.Combine(Path.GetTempPath(), "agentdock-mac-install-" + Guid.NewGuid().ToString("N")));
    private string Home => Path.Combine(root, "data");

    public MacInstallIntegrationTests() => Directory.CreateDirectory(Home);

    [MacPathFact]
    public async Task OldDesktopLaunchesHostFromCurrentInstallPointer()
    {
        var directory = Path.Combine(root, "install", "v2", "host");
        Directory.CreateDirectory(directory);
        var wrapper = Path.Combine(directory, "host-wrapper");
        var actual = Path.ChangeExtension(typeof(AgentDock.Host.Program).Assembly.Location, null);
        File.WriteAllText(wrapper, "#!/bin/sh\nexec '" + actual.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "' \"$@\"\n");
        if (OperatingSystem.IsMacOS()) File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(root, "install", "current.json"), JsonSerializer.Serialize(new { schema = 1, host = wrapper, dataHome = Home }));
        var client = new DockClient(Home, "/bin/false");
        try
        {
            await client.EnsureHostAsync();
            Assert.True((await client.InvokeAsync<HostStatus>("status")).CanUpdate);
            var plist = XDocument.Load(Path.Combine(Home, "host-launch.plist"));
            Assert.Contains(plist.Descendants("string"), element => element.Value == wrapper);
        }
        finally
        {
            try { await client.InvokeAsync<JsonElement>("shutdown", new { confirm = false }); }
            catch (Exception error) when (error is IOException or TimeoutException) { }
            await Task.Delay(300);
            await MacHostLauncher.RemoveStoppedAsync(Home);
        }
    }

    [MacPathFact]
    public void LoginStartupIsOptInAndUsesOnlyTheStableLauncher()
    {
        var registration = MacLoginStartup.RegistrationPath(Home);
        Assert.False(File.Exists(registration));
        var launcher = Path.Combine(root, "launcher");
        File.WriteAllText(launcher, "fixture-only");
        var install = Path.Combine(root, "plugin", "plugins", "agentdock", "runtime", "install.json");
        Directory.CreateDirectory(Path.GetDirectoryName(install)!);
        File.WriteAllText(install, JsonSerializer.Serialize(new { launcher }));
        try
        {
            MacLoginStartup.Configure(Home, true);
            var plist = XDocument.Load(registration);
            var values = plist.Descendants("string").Select(element => element.Value).ToArray();
            Assert.Contains(launcher, values);
            Assert.Contains("--tray", values);
            Assert.Contains(root, values);
            MacLoginStartup.Configure(Home, false);
            Assert.False(File.Exists(registration));
        }
        finally { File.Delete(registration); }
    }

    public void Dispose() => Directory.Delete(root, true);
}
