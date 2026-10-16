using AgentDock.Setup;

namespace AgentDock.MacSetup;

/// <summary>Receipts identify installed versions; directory names alone never authorize deletion.</summary>
internal static class MacOwnership
{
    private const string FileName = ".agentdock-version.json";
    private sealed record Receipt(int Schema, string Product, string Version);

    public static void RecordVersion(string directory, string version) =>
        File.WriteAllText(Path.Combine(directory, FileName), SetupJson.Serialize(new Receipt(1, "AgentDock", version)));

    public static bool IsInstalledVersion(string directory)
    {
        try
        {
            var receiptPath = Path.Combine(directory, FileName);
            if (!Directory.Exists(directory) || !File.Exists(receiptPath) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(receiptPath) & FileAttributes.ReparsePoint) != 0) return false;
            var receipt = SetupJson.Deserialize<Receipt>(File.ReadAllText(receiptPath));
            return receipt.Schema == 1 && receipt.Product == "AgentDock" &&
                string.Equals(receipt.Version, Path.GetFileName(directory), StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
