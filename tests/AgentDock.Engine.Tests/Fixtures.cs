using System.Text.Json;

namespace AgentDock.Engine.Tests;

internal static class Fixtures
{
    public const string Session = "ses_fixture";

    private static int _stepIds;

    public static string Part(string partType, params (string Key, object? Value)[] fields)
    {
        var value = new Dictionary<string, object?>
        {
            ["id"] = "prt_" + partType + Guid.NewGuid().ToString("N")[..8],
            ["sessionID"] = Session,
            ["messageID"] = "msg_fixture",
            ["type"] = partType,
        };
        foreach (var (key, fieldValue) in fields)
        {
            value[key] = fieldValue;
        }
        return JsonSerializer.Serialize(value);
    }

    public static string Event(string type, string? partJson = null, object? error = null)
    {
        var value = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["timestamp"] = 1_700_000_000_000L,
            ["sessionID"] = Session,
        };
        if (partJson is not null)
        {
            value["part"] = JsonDocument.Parse(partJson).RootElement.Clone();
        }
        if (error is not null)
        {
            value["error"] = error;
        }
        return JsonSerializer.Serialize(value);
    }

    public static string StepFinish(string reason, string? id = null, string? tokensJson = null)
    {
        var fields = new List<(string, object?)>
        {
            ("reason", reason),
            ("cost", 0),
        };
        if (id is not null)
        {
            fields.Add(("id", id));
        }
        else
        {
            fields.Add(("id", "prt_step_" + Interlocked.Increment(ref _stepIds)));
        }
        if (tokensJson is not null)
        {
            fields.Add(("tokens", JsonDocument.Parse(tokensJson).RootElement.Clone()));
        }
        return Event("step_finish", Part("step-finish", fields.ToArray()));
    }

    public static string Tokens(double input, double output, double reasoning, double read, double write) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["input"] = input,
            ["output"] = output,
            ["reasoning"] = reasoning,
            ["cache"] = new Dictionary<string, object?> { ["read"] = read, ["write"] = write },
        });
}

internal static class AdapterExtensions
{
    public static List<Core.EngineEvent> FeedAll(this OpenCodeEventAdapter adapter, params string[] lines)
    {
        var events = new List<Core.EngineEvent>();
        foreach (var line in lines)
        {
            events.AddRange(adapter.Feed(line));
        }
        return events;
    }
}
