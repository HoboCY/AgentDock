using System.Text.Json;

namespace AgentDock.Core;

public static class AtomicJson
{
    public static T Read<T>(string path) where T : new() => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), JsonDefaults.Options)
          ?? throw new InvalidDataException("持久化文件为空，拒绝创建替代状态。")
        : new T();

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, JsonDefaults.Options);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
