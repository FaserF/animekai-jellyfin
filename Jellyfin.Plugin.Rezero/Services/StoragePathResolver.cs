using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Rezero.Configuration;

namespace Jellyfin.Plugin.Rezero.Services;

/// <summary>Represents a mounted filesystem entry parsed from /proc/mounts or injected for testing.</summary>
public sealed record MountEntry(string Device, string MountPoint, string FileSystemType, bool IsReadOnly);

/// <summary>Result of resolving physical storage and symlink paths for Re:Zero streaming files.</summary>
public sealed class StorageResolutionResult
{
    public string TargetSeasonPath { get; init; } = string.Empty;
    public string PhysicalStoragePath { get; init; } = string.Empty;
    public bool UseSymlink { get; init; }
    public string DetectionMode { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>Resolves fast local dedicated storage paths and symlink strategies for any Jellyfin environment.</summary>
public class StoragePathResolver
{
    private static readonly HashSet<string> RemoteFsTypes = new(StringComparer.OrdinalIgnoreCase)
        { "nfs", "nfs4", "cifs", "smb", "smb3", "smbfs", "davfs", "davfs2", "glusterfs", "ceph", "9p", "lustre", "afs", "ncpfs" };

    private static readonly HashSet<string> DisallowedPosixDirs = new(StringComparer.Ordinal)
        { "/", "/etc", "/usr", "/var", "/opt", "/tmp", "/proc", "/sys", "/dev", "/run", "/home", "/root", "/bin", "/sbin", "/lib", "/lib64" };

    private readonly Func<IReadOnlyList<MountEntry>> _getMounts;
    private readonly Func<string, string?> _resolveRealPath;
    private readonly Func<string, string?> _getLinkTarget;
    private readonly Func<string, bool> _isDirectoryWritable;
    private readonly Func<string, bool> _isNetworkPath;

    public StoragePathResolver(
        Func<IReadOnlyList<MountEntry>>? getMounts = null,
        Func<string, string?>? resolveRealPath = null,
        Func<string, string?>? getLinkTarget = null,
        Func<string, bool>? isDirectoryWritable = null,
        Func<string, bool>? isNetworkPath = null)
    {
        _getMounts = getMounts ?? ReadProcMounts;
        _resolveRealPath = resolveRealPath ?? ResolveRealPathDefault;
        _getLinkTarget = getLinkTarget ?? GetLinkTargetDefault;
        _isDirectoryWritable = isDirectoryWritable ?? IsDirectoryWritableDefault;
        _isNetworkPath = isNetworkPath ?? IsNetworkPathDefault;
    }

    public StorageResolutionResult Resolve(PluginConfiguration config, string seriesPath, string seasonFolder, string? appDataPath)
    {
        var normalizedSeries = NormalizePath(seriesPath);
        var season = string.IsNullOrWhiteSpace(seasonFolder) ? "Season 00" : seasonFolder.Trim();
        var targetSeasonPath = JoinPath(normalizedSeries, season);

        if (!string.IsNullOrWhiteSpace(config.DedicatedStoragePath))
        {
            var trimmed = TrimEndSep(config.DedicatedStoragePath.Trim());
            var lastSeg = trimmed.LastIndexOfAny(['/', '\\']) is var idx && idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
            var physical = NormalizePath(string.Equals(lastSeg, season, StringComparison.OrdinalIgnoreCase) ? trimmed : JoinPath(trimmed, season));
            var useSym = config.EnableSymlink && !IsSameOrNestedPath(targetSeasonPath, physical);
            return new() { TargetSeasonPath = targetSeasonPath, PhysicalStoragePath = physical, UseSymlink = useSym, DetectionMode = "Manual",
                Reason = useSym ? $"Using manual DedicatedStoragePath '{physical}'." : $"Writing directly to '{physical}'." };
        }

        if (!config.EnableSymlink)
            return new() { TargetSeasonPath = targetSeasonPath, PhysicalStoragePath = targetSeasonPath, UseSymlink = false, DetectionMode = "Direct", Reason = "Symlink storage is disabled." };

        var mounts = _getMounts();
        var existingTarget = _getLinkTarget(targetSeasonPath);
        if (!string.IsNullOrWhiteSpace(existingTarget))
        {
            var resolved = NormalizePath(IsRooted(existingTarget) ? existingTarget : ResolveRelative(normalizedSeries, existingTarget));
            if (!IsSameOrNestedPath(targetSeasonPath, resolved) && !_isNetworkPath(NormalizePath(_resolveRealPath(resolved) ?? resolved)) &&
                !IsRemoteOrUnion(FindLongestMount(NormalizePath(_resolveRealPath(resolved) ?? resolved), mounts)))
                return new() { TargetSeasonPath = targetSeasonPath, PhysicalStoragePath = resolved, UseSymlink = true, DetectionMode = "Auto (Existing Symlink)", Reason = $"Reusing symlink target '{resolved}'." };
        }

        var realSeries = NormalizePath(_resolveRealPath(normalizedSeries) ?? normalizedSeries);
        var seriesMount = FindLongestMount(realSeries, mounts);
        if (IsRemoteOrUnion(seriesMount) || _isNetworkPath(realSeries))
        {
            foreach (var candidate in EnumerateAncestors(seriesMount?.MountPoint, normalizedSeries))
            {
                var physical = JoinPath(candidate, "rezero", season);
                if (IsValidLocalRoot(candidate, mounts) && !IsSameOrNestedPath(targetSeasonPath, physical))
                    return new() { TargetSeasonPath = targetSeasonPath, PhysicalStoragePath = physical, UseSymlink = true, DetectionMode = "Auto (Local Media Root)",
                        Reason = $"Series sits on union/remote mount ({seriesMount?.FileSystemType ?? "network"}); using local root '{candidate}'." };
            }
        }
        else
        {
            var parentOfLib = GetParent(GetParent(normalizedSeries));
            if (!string.IsNullOrWhiteSpace(parentOfLib) && IsValidLocalRoot(parentOfLib, mounts))
            {
                var physical = JoinPath(parentOfLib, "rezero", season);
                if (!IsSameOrNestedPath(targetSeasonPath, physical))
                    return new() { TargetSeasonPath = targetSeasonPath, PhysicalStoragePath = physical, UseSymlink = true, DetectionMode = "Auto (Local Media Root)",
                        Reason = $"Using local sibling storage root '{parentOfLib}' outside TV library root." };
            }
        }

        if (!string.IsNullOrWhiteSpace(appDataPath))
        {
            var appData = NormalizePath(appDataPath);
            var appReal = NormalizePath(_resolveRealPath(appData) ?? appData);
            var appMount = FindLongestMount(appReal, mounts);
            var physical = JoinPath(appData, "rezero", season);
            if (!IsRemoteOrUnion(appMount) && !_isNetworkPath(appReal) && (appMount == null || (!appMount.IsReadOnly && !IsRootOverlay(appMount))) &&
                _isDirectoryWritable(appData) && !IsSameOrNestedPath(targetSeasonPath, physical))
                return new() { TargetSeasonPath = targetSeasonPath, PhysicalStoragePath = physical, UseSymlink = true, DetectionMode = "Auto (AppData)",
                    Reason = $"Using persistent Jellyfin AppData '{appData}'." };
        }

        return new() { TargetSeasonPath = targetSeasonPath, PhysicalStoragePath = targetSeasonPath, UseSymlink = false, DetectionMode = "Direct", Reason = "Fallback to direct writes inside series season directory." };
    }

    public static bool IsSameOrNestedPath(string pathA, string pathB)
    {
        var (a, b) = (TrimEndSep(pathA), TrimEndSep(pathB));
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var cmp = pathA.StartsWith('/') && pathB.StartsWith('/') ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (string.Equals(a, b, cmp)) return true;
        var sep = pathA.StartsWith('/') ? "/" : Path.DirectorySeparatorChar.ToString();
        return b.StartsWith((a.EndsWith(sep, StringComparison.Ordinal) ? a : a + sep), cmp) ||
               a.StartsWith((b.EndsWith(sep, StringComparison.Ordinal) ? b : b + sep), cmp);
    }

    internal static IReadOnlyList<MountEntry> ParseProcMounts(string content) =>
        content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 4)
            .Select(p => new MountEntry(Unescape(p[0]), Unescape(p[1]), p[2], p[3].Split(',').Contains("ro", StringComparer.Ordinal)))
            .ToList();

    private IEnumerable<string> EnumerateAncestors(string? mountPoint, string seriesPath)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seed in new[] { mountPoint != null ? GetParent(NormalizePath(mountPoint)) : null, GetParent(seriesPath) })
            for (var cur = seed; !string.IsNullOrWhiteSpace(cur); cur = GetParent(cur))
                if (seen.Add(cur)) yield return cur;
    }

    private bool IsValidLocalRoot(string candidate, IReadOnlyList<MountEntry> mounts)
    {
        var norm = NormalizePath(candidate);
        if (norm.StartsWith('/'))
        {
            if (DisallowedPosixDirs.Contains(norm)) return false;
            if (norm is "/mnt" or "/media")
            {
                var exact = FindLongestMount(norm, mounts);
                if (exact == null || exact.MountPoint != norm || exact.FileSystemType.Equals("overlay", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        else if (norm.Contains("\\Windows", StringComparison.OrdinalIgnoreCase)) return false;

        var real = NormalizePath(_resolveRealPath(norm) ?? norm);
        if (_isNetworkPath(real)) return false;
        var mount = FindLongestMount(real, mounts);
        return (mount == null || (!mount.IsReadOnly && !IsRemoteOrUnion(mount) && !IsRootOverlay(mount))) && _isDirectoryWritable(norm);
    }

    private static bool IsRemoteOrUnion(MountEntry? m) => m != null && (RemoteFsTypes.Contains(m.FileSystemType) ||
        (m.FileSystemType.StartsWith("fuse.", StringComparison.OrdinalIgnoreCase) &&
         !m.FileSystemType.Equals("fuse.fuseblk", StringComparison.OrdinalIgnoreCase) &&
         !m.FileSystemType.Equals("fuse.fusectl", StringComparison.OrdinalIgnoreCase)));

    private static bool IsRootOverlay(MountEntry m) => m.MountPoint == "/" && m.FileSystemType.Equals("overlay", StringComparison.OrdinalIgnoreCase);

    private static MountEntry? FindLongestMount(string path, IReadOnlyList<MountEntry> mounts)
    {
        if (mounts.Count == 0 || !path.StartsWith('/')) return null;
        MountEntry? best = null;
        var bestLen = -1;
        foreach (var m in mounts)
        {
            var mp = m.MountPoint == "/" ? "/" : m.MountPoint.TrimEnd('/');
            var match = mp == "/" ? path.StartsWith('/') : path == mp || path.StartsWith(mp + "/", StringComparison.Ordinal);
            if (match && mp.Length >= bestLen) { bestLen = mp.Length; best = m with { MountPoint = mp }; }
        }
        return best;
    }

    private static IReadOnlyList<MountEntry> ReadProcMounts()
    {
        try { return OperatingSystem.IsLinux() && File.Exists("/proc/mounts") ? ParseProcMounts(File.ReadAllText("/proc/mounts")) : Array.Empty<MountEntry>(); }
        catch { return Array.Empty<MountEntry>(); }
    }

    private static string Unescape(string v) => v.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    private static string? ResolveRealPathDefault(string path)
    {
        try
        {
            if (!path.StartsWith('/')) return new DirectoryInfo(Path.GetFullPath(path)).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);
            var cur = "/";
            var depth = 0;
            foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (depth++ > 32) break;
                cur = cur == "/" ? "/" + seg : cur + "/" + seg;
                if (GetLinkTargetDefault(cur) is { Length: > 0 } lt)
                    cur = IsRooted(lt) ? NormalizePath(lt) : ResolveRelative(GetParent(cur) ?? "/", lt);
            }
            return cur;
        }
        catch { return path; }
    }

    private static string? GetLinkTargetDefault(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is { Length: > 0 } dt ? dt : new FileInfo(path).LinkTarget is { Length: > 0 } ft ? ft : null; }
        catch { return null; }
    }

    private static bool IsDirectoryWritableDefault(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            using (File.Create(Path.Combine(path, ".rezero_write_probe_" + Guid.NewGuid().ToString("N")), 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

    private static bool IsNetworkPathDefault(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;
        try { return Path.GetPathRoot(path) is { Length: >= 2 } r && r[1] == ':' && new DriveInfo(r).DriveType == DriveType.Network; }
        catch { return false; }
    }

    private static bool IsRooted(string p) => p.StartsWith('/') || Path.IsPathRooted(p);
    private static string TrimEndSep(string p) => p == "/" || (p.Length == 3 && p[1] == ':' && p[2] is '\\' or '/') ? p : p.TrimEnd('/', '\\');
    private static string NormalizePath(string p) => p.StartsWith('/') ? TrimEndSep(string.Join('/', p.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)).Insert(0, "/")) : TrimEndSep(p.Replace('/', '\\'));

    private static string JoinPath(string basePath, params string[] segs)
    {
        var sep = basePath.StartsWith('/') ? "/" : "\\";
        var cur = TrimEndSep(basePath);
        foreach (var s in segs.Select(x => x.Trim('/', '\\')).Where(x => x.Length > 0))
            cur = cur == "/" || cur.EndsWith(sep, StringComparison.Ordinal) ? cur + s : cur + sep + s;
        return cur;
    }

    private static string? GetParent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var t = TrimEndSep(path);
        if (!t.StartsWith('/')) return Path.GetDirectoryName(t);
        if (t == "/") return null;
        var idx = t.LastIndexOf('/');
        return idx <= 0 ? "/" : t[..idx];
    }

    private static string ResolveRelative(string baseDir, string rel)
    {
        if (!baseDir.StartsWith('/')) return Path.GetFullPath(Path.Combine(baseDir, rel));
        var stack = new List<string>(baseDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var p in rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (p == ".") continue;
            if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(p);
        }
        return "/" + string.Join('/', stack);
    }
}
