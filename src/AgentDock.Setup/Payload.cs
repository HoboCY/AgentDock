using System.IO.Compression;
using System.Security.Cryptography;

namespace AgentDock.Setup;

internal static class Payload
{
    public const string ManifestEntryName = "payload-manifest.json";
    private const int BufferSize = 1024 * 1024;

    public static PayloadManifest ReadManifest(string payloadPath)
    {
        if (Directory.Exists(payloadPath))
        {
            var manifestPath = Path.Combine(payloadPath, ManifestEntryName);
            if (!File.Exists(manifestPath)) throw new InvalidDataException($"payload 目录缺少 {ManifestEntryName}：{payloadPath}");
            var manifest = SetupJson.Deserialize<PayloadManifest>(File.ReadAllText(manifestPath));
            Validate(manifest);
            return manifest;
        }

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(payloadPath);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"payload 压缩包无法读取（可能下载不完整或不是 zip）：{payloadPath}", ex);
        }
        using var _ = zip;
        var entry = zip.Entries.FirstOrDefault(e => Normalize(e.FullName) == ManifestEntryName);
        if (entry is null) throw new InvalidDataException($"payload 压缩包缺少 {ManifestEntryName}：{payloadPath}");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var result = SetupJson.Deserialize<PayloadManifest>(reader.ReadToEnd());
        Validate(result);
        return result;
    }

    /// <summary>从 payload ZIP 解压到 staging，逐文件校验 SHA-256 与体积；出现清单外文件即失败。</summary>
    public static PayloadManifest ExtractZip(string zipPath, string stagingDir)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifestEntry = zip.Entries.FirstOrDefault(e => Normalize(e.FullName) == ManifestEntryName)
            ?? throw new InvalidDataException($"payload 压缩包缺少 {ManifestEntryName}：{zipPath}");
        PayloadManifest manifest;
        using (var stream = manifestEntry.Open())
        using (var reader = new StreamReader(stream))
        {
            manifest = SetupJson.Deserialize<PayloadManifest>(reader.ReadToEnd());
        }
        Validate(manifest);

        var listed = manifest.Files.ToDictionary(f => Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = Normalize(entry.FullName);
            if (name.Length == 0 || name.EndsWith('/')) continue;
            if (name == ManifestEntryName) continue;
            if (!listed.TryGetValue(name, out var spec))
            {
                throw new InvalidDataException($"payload 含清单外文件：{entry.FullName}");
            }

            var destination = StagingPath(stagingDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = File.Create(destination);
            using var sha = SHA256.Create();
            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                sha.TransformBlock(buffer, 0, read, null, 0);
                total += read;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (total != spec.Size) throw new InvalidDataException($"payload 文件体积不符：{name}（{total} != {spec.Size}）");
            if (!hash.Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"payload 文件 SHA-256 不符：{name}");
            }
            seen.Add(name);
        }

        EnsureAllListed(manifest, seen);
        return manifest;
    }

    /// <summary>从解压后的 payload 目录复制到 staging，校验清单内文件且拒绝多余文件。</summary>
    public static PayloadManifest CopyDirectory(string sourceDir, string stagingDir)
    {
        var manifestPath = Path.Combine(sourceDir, ManifestEntryName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"payload 目录缺少 {ManifestEntryName}：{sourceDir}");
        var manifest = SetupJson.Deserialize<PayloadManifest>(File.ReadAllText(manifestPath));
        Validate(manifest);

        var listed = manifest.Files.ToDictionary(f => Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var name = Normalize(Path.GetRelativePath(sourceDir, file));
            if (name == ManifestEntryName) continue;
            if (!listed.TryGetValue(name, out var spec)) throw new InvalidDataException($"payload 含清单外文件：{file}");
            var info = new FileInfo(file);
            if (info.Length != spec.Size) throw new InvalidDataException($"payload 文件体积不符：{name}");
            if (!Sha256File(file).Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"payload 文件 SHA-256 不符：{name}");
            var destination = StagingPath(stagingDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
            seen.Add(name);
        }
        EnsureAllListed(manifest, seen);
        return manifest;
    }

    /// <summary>校验已有版本目录与清单完全一致（用于同版本重装修复判断）。</summary>
    public static bool DirectoryMatches(string directory, PayloadManifest manifest)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var file in manifest.Files)
        {
            var path = StagingPath(directory, Normalize(file.Path));
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length != file.Size) return false;
            if (!Sha256File(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string Normalize(string name)
    {
        var value = name.Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value;
    }

    private static void EnsureAllListed(PayloadManifest manifest, HashSet<string> seen)
    {
        foreach (var file in manifest.Files)
        {
            if (!seen.Contains(Normalize(file.Path))) throw new InvalidDataException($"payload 缺少清单文件：{file.Path}");
        }
    }

    private static string StagingPath(string root, string relative)
    {
        if (!IsSafeRelative(relative)) throw new InvalidDataException($"payload 路径非法：{relative}");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"payload 路径越界：{relative}");
        }
        return full;
    }

    private static bool IsSafeRelative(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.Contains(':')) return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..") return false;
        }
        return true;
    }

    private static void Validate(PayloadManifest manifest)
    {
        if (manifest.Schema != 1) throw new InvalidDataException($"不支持的 payload schema：{manifest.Schema}");
        if (!string.Equals(manifest.Product, "AgentDock", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"payload 产品标识不符：{manifest.Product}");
        }
        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("payload 缺少版本号。");
        if (manifest.Files is null || manifest.Files.Length == 0) throw new InvalidDataException("payload 清单为空。");
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || !IsSafeRelative(Normalize(file.Path)))
            {
                throw new InvalidDataException($"payload 清单路径非法：{file.Path}");
            }
            if (file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"payload 清单哈希非法：{file.Path}");
            }
            if (file.Size < 0) throw new InvalidDataException($"payload 清单体积非法：{file.Path}");
        }
    }
}
