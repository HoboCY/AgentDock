using System.IO.Compression;
using System.Security.Cryptography;

namespace AgentDock.Setup;

internal static class Payload
{
    public const string ManifestEntryName = "payload-manifest.json";
    public const string BundledSource = "embedded:AgentDock.Payload.zip";
    private const string BundledResource = "AgentDock.Payload.zip";
    public static bool HasBundledPayload => typeof(Payload).Assembly.GetManifestResourceNames().Contains(BundledResource);
    private const int BufferSize = 1024 * 1024;

    private static ZipArchive OpenZip(string path)
    {
        var stream = path == BundledSource
            ? typeof(Payload).Assembly.GetManifestResourceStream(BundledResource)
                ?? throw new InvalidDataException("安装程序缺少内置程序包，请重新下载安装程序。")
            : File.OpenRead(path);
        try { return new ZipArchive(stream, ZipArchiveMode.Read); }
        catch { stream.Dispose(); throw; }
    }

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
            zip = OpenZip(payloadPath);
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

    /// <summary>从 payload ZIP 解压到 staging，逐文件校验 SHA-256 与体积；出现清单外文件或符号链接即失败。</summary>
    public static PayloadManifest ExtractZip(string zipPath, string stagingDir)
    {
        using var zip = OpenZip(zipPath);
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
            if (IsSymlinkEntry(entry)) throw new InvalidDataException($"payload 含符号链接：{entry.FullName}");
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
            output.Dispose();
            ApplyMode(destination, spec.Mode);
            seen.Add(name);
        }

        EnsureAllListed(manifest, seen);
        return manifest;
    }

    /// <summary>从解压后的 payload 目录复制到 staging，校验清单内文件且拒绝多余文件或符号链接。</summary>
    public static PayloadManifest CopyDirectory(string sourceDir, string stagingDir)
    {
        var manifestPath = Path.Combine(sourceDir, ManifestEntryName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException($"payload 目录缺少 {ManifestEntryName}：{sourceDir}");
        var manifest = SetupJson.Deserialize<PayloadManifest>(File.ReadAllText(manifestPath));
        Validate(manifest);

        var listed = manifest.Files.ToDictionary(f => Normalize(f.Path), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumeratePayloadFiles(sourceDir))
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
            ApplyMode(destination, spec.Mode);
            seen.Add(name);
        }
        EnsureAllListed(manifest, seen);
        return manifest;
    }

    /// <summary>
    /// 校验已有版本目录与清单完全一致（用于同版本重装修复判断）。
    /// 清单文件若被符号链接/重解析点替换即视为不一致，绝不跟随链接读取或复用。
    /// </summary>
    public static bool DirectoryMatches(string directory, PayloadManifest manifest)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var file in manifest.Files)
        {
            var path = StagingPath(directory, Normalize(file.Path));
            if (!File.Exists(path)) return false;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            var info = new FileInfo(path);
            if (info.Length != file.Size) return false;
            if (!Sha256File(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            if (!OperatingSystem.IsWindows() && file.Mode is not null &&
                File.GetUnixFileMode(path) != ParseMode(file.Mode)) return false;
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

    /// <summary>
    /// 应用清单声明的 Unix 权限位（macOS 安装需要）。mode 为空表示不改变默认权限
    /// （旧 win-x64 清单没有该属性）；Windows 上没有 Unix 权限位，调用为 no-op。
    /// </summary>
    public static void ApplyMode(string path, string? mode)
    {
        if (mode is null || OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, ParseMode(mode));
    }

    /// <summary>解析清单权限位：三位或四位八进制，禁止 setuid/setgid/sticky。</summary>
    public static UnixFileMode ParseMode(string mode)
    {
        if (mode.Length is < 3 or > 4 || mode.Any(c => c is < '0' or > '7'))
        {
            throw new InvalidDataException($"payload 权限位非法：{mode}（应为三位或四位八进制数字）");
        }
        var value = Convert.ToInt32(mode, 8);
        if (value > 0x1FF)
        {
            throw new InvalidDataException($"payload 权限位非法：{mode}（不得包含 setuid/setgid/sticky 位）");
        }
        return (UnixFileMode)value;
    }

    /// <summary>
    /// 枚举 payload 目录文件；遇到符号链接/联接等重解析条目即拒绝，且绝不进入它们
    /// （自实现遍历，避免递归枚举跟随重解析点造成越界或循环）。
    /// </summary>
    private static IEnumerable<string> EnumeratePayloadFiles(string sourceDir)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(sourceDir));
        while (pending.Count > 0)
        {
            foreach (var info in pending.Pop().EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"payload 含符号链接或其他重解析条目：{info.FullName}");
                }
                if (info is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }
                yield return info.FullName;
            }
        }
    }

    /// <summary>ZIP 条目的 Unix 模式高位为 0xA000 表示符号链接；拒绝其进入 payload。</summary>
    private static bool IsSymlinkEntry(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static void Validate(PayloadManifest manifest)
    {
        if (manifest.Schema != 1) throw new InvalidDataException($"不支持的 payload schema：{manifest.Schema}");
        if (!string.Equals(manifest.Product, "AgentDock", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"payload 产品标识不符：{manifest.Product}");
        }
        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException("payload 缺少版本号。");
        if (manifest.Files is null || manifest.Files.Length == 0) throw new InvalidDataException("payload 清单为空。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path) || !IsSafeRelative(Normalize(file.Path)))
            {
                throw new InvalidDataException($"payload 清单路径非法：{file.Path}");
            }
            if (!paths.Add(Normalize(file.Path)))
            {
                throw new InvalidDataException($"payload 清单包含重复路径：{file.Path}");
            }
            if (file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException($"payload 清单哈希非法：{file.Path}");
            }
            if (file.Size < 0) throw new InvalidDataException($"payload 清单体积非法：{file.Path}");
            if (file.Mode is { } mode)
            {
                // 只校验格式与安全范围；是否要求可执行位由各平台安装程序决定。
                ParseMode(mode);
            }
        }
    }
}
