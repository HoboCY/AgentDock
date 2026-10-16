using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDock.Setup;

/// <summary>安装包 payload 清单（payload-manifest.json，位于 payload 根）。</summary>
internal sealed record PayloadFile(string Path, long Size, string Sha256);

internal sealed record PayloadManifest(int Schema, string Product, string Version, string Runtime, PayloadFile[] Files, bool Partial = false);

/// <summary>安装目录内的稳定指针（install/current.json）。</summary>
internal sealed record CurrentInstall(
    int Schema,
    string Version,
    string Root,
    string Host,
    string Desktop,
    string Engine,
    string Plugin,
    string DataHome);

/// <summary>本机检测结果（install/machine.json），不含任何凭据。</summary>
internal sealed record MachineInfo(
    int Schema,
    string? ShellPath,
    string? ShellVersion,
    string? ShellSource,
    string? ShellSha256,
    string? CodexCli,
    bool LegacyDeepSeekAgents,
    string[] LegacyDetails,
    string[] Warnings,
    string ProbedAt,
    string OsDescription,
    string Architecture);

/// <summary>插件 runtime/install.json：供插件内 Host 代理定位稳定安装位置。</summary>
internal sealed record PluginInstall(
    int Schema,
    string Product,
    string Version,
    string InstallRoot,
    string HostManifest,
    string Launcher,
    string DataHome);

/// <summary>更新进行中标记（install/update.lock）；由 OperationGate 写入并在操作结束时清理。</summary>
internal sealed record UpdateLock(
    int Schema,
    string TargetVersion,
    int ProcessId,
    string CreatedAt,
    string? PayloadSha256,
    string? Operation);

internal static class SetupJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("JSON 内容为空。");
}
