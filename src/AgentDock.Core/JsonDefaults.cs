using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace AgentDock.Core;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };
}
