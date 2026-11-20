using System;
using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.Rezero.Configuration;
using Jellyfin.Plugin.Rezero.Services;
using Xunit;

namespace Jellyfin.Plugin.Rezero.Tests;

public class StoragePathResolverTests
{
    private const string OracleMounts = """
        overlay / overlay rw,relatime 0 0
        /dev/sda1 /config ext4 rw,relatime 0 0
        /dev/sda1 /data ext4 rw,relatime 0 0
        root@dell:/data/media /data/dell fuse.sshfs rw,nosuid,nodev,relatime 0 0
        cloud_pool_crypt: /data/cloud_pool fuse.rclone rw,nosuid,nodev,relatime 0 0
        local:cloud_pool /data/merged fuse.mergerfs rw,nosuid,nodev,relatime 0 0
        """;

    [Fact]
    public void Resolve_MergerfsAndRclone_WithLogicalTvSymlink_ShouldPickLocalDataRoot()
    {
        var mounts = StoragePathResolver.ParseProcMounts(OracleMounts);
        var resolver = new StoragePathResolver(
            getMounts: () => mounts,
            resolveRealPath: p => p.StartsWith("/data/tv", StringComparison.Ordinal)
                ? "/data/merged/tv" + p["/data/tv".Length..]
                : p,
            getLinkTarget: _ => null,
            isDirectoryWritable: p => p is "/data" or "/config/data",
            isNetworkPath: _ => false);

        var config = new PluginConfiguration
        {
            DedicatedStoragePath = string.Empty,
            EnableSymlink = true
        };

        var result = resolver.Resolve(
            config,
            "/data/tv/Re - ZERO, Starting Life in Another World",
            "Season 00",
            "/config/data");

        result.DetectionMode.Should().Be("Auto (Local Media Root)");
        result.UseSymlink.Should().BeTrue();
        result.TargetSeasonPath.Should().Be("/data/tv/Re - ZERO, Starting Life in Another World/Season 00");
        result.PhysicalStoragePath.Should().Be("/data/rezero/Season 00");
    }

    [Fact]
    public void Resolve_ExistingSymlink_ShouldReuseExistingLocalTarget()
    {
        var mounts = StoragePathResolver.ParseProcMounts(OracleMounts);
        var resolver = new StoragePathResolver(
            getMounts: () => mounts,
            resolveRealPath: p => p,
            getLinkTarget: p => p.EndsWith("/Season 00", StringComparison.Ordinal) ? "/data/rezero/Season 00" : null,
            isDirectoryWritable: _ => true,
            isNetworkPath: _ => false);

        var config = new PluginConfiguration
        {
            DedicatedStoragePath = string.Empty,
            EnableSymlink = true
        };

        var result = resolver.Resolve(
            config,
            "/data/tv/Re - ZERO, Starting Life in Another World",
            "Season 00",
            "/config/data");

        result.DetectionMode.Should().Be("Auto (Existing Symlink)");
        result.UseSymlink.Should().BeTrue();
        result.PhysicalStoragePath.Should().Be("/data/rezero/Season 00");
    }

    [Fact]
    public void Resolve_IsolatedRcloneUnderOverlay_ShouldFallbackToAppData()
    {
        const string overlayMounts = """
            overlay / overlay rw,relatime 0 0
            cloud_pool: /media fuse.rclone rw 0 0
            /dev/sda1 /config ext4 rw 0 0
            """;
        var mounts = StoragePathResolver.ParseProcMounts(overlayMounts);
        var resolver = new StoragePathResolver(
            getMounts: () => mounts,
            resolveRealPath: p => p,
            getLinkTarget: _ => null,
            isDirectoryWritable: p => p is "/config/data" or "/config",
            isNetworkPath: _ => false);

        var config = new PluginConfiguration
        {
            DedicatedStoragePath = string.Empty,
            EnableSymlink = true
        };

        var result = resolver.Resolve(
            config,
            "/media/tv/ReZero",
            "Season 00",
            "/config/data");

        result.DetectionMode.Should().Be("Auto (AppData)");
        result.UseSymlink.Should().BeTrue();
        result.PhysicalStoragePath.Should().Be("/config/data/rezero/Season 00");
    }

    [Fact]
    public void Resolve_StandardLocalLibrary_ShouldUseSiblingLocalRoot()
    {
        const string localMounts = """
            overlay / overlay rw,relatime 0 0
            /dev/sda1 /data ext4 rw,relatime 0 0
            /dev/sda1 /config ext4 rw,relatime 0 0
            """;
        var mounts = StoragePathResolver.ParseProcMounts(localMounts);
        var resolver = new StoragePathResolver(
            getMounts: () => mounts,
            resolveRealPath: p => p,
            getLinkTarget: _ => null,
            isDirectoryWritable: p => p is "/data" or "/data/tv" or "/config/data",
            isNetworkPath: _ => false);

        var config = new PluginConfiguration
        {
            DedicatedStoragePath = string.Empty,
            EnableSymlink = true
        };

        var result = resolver.Resolve(
            config,
            "/data/tv/ReZero",
            "Season 00",
            "/config/data");

        result.DetectionMode.Should().Be("Auto (Local Media Root)");
        result.UseSymlink.Should().BeTrue();
        result.PhysicalStoragePath.Should().Be("/data/rezero/Season 00");
    }

    [Fact]
    public void Resolve_ManualOverride_WithOrWithoutSeasonSuffix_ShouldNormalizeSeasonFolder()
    {
        var resolver = new StoragePathResolver(
            getMounts: () => Array.Empty<MountEntry>(),
            resolveRealPath: p => p,
            getLinkTarget: _ => null,
            isDirectoryWritable: _ => true,
            isNetworkPath: _ => false);

        var withoutSeason = resolver.Resolve(
            new PluginConfiguration { DedicatedStoragePath = "/custom/rezero", EnableSymlink = true },
            "/data/tv/ReZero",
            "Season 00",
            "/config/data");

        withoutSeason.DetectionMode.Should().Be("Manual");
        withoutSeason.UseSymlink.Should().BeTrue();
        withoutSeason.PhysicalStoragePath.Should().Be("/custom/rezero/Season 00");

        var withSeason = resolver.Resolve(
            new PluginConfiguration { DedicatedStoragePath = "/custom/rezero/Season 00/", EnableSymlink = true },
            "/data/tv/ReZero",
            "Season 00",
            "/config/data");

        withSeason.DetectionMode.Should().Be("Manual");
        withSeason.UseSymlink.Should().BeTrue();
        withSeason.PhysicalStoragePath.Should().Be("/custom/rezero/Season 00");
    }

    [Fact]
    public void Resolve_DirectMode_WhenSymlinkDisabled_ShouldWriteDirectlyToSeasonFolder()
    {
        var resolver = new StoragePathResolver(
            getMounts: () => Array.Empty<MountEntry>(),
            resolveRealPath: p => p,
            getLinkTarget: _ => null,
            isDirectoryWritable: _ => true,
            isNetworkPath: _ => false);

        var config = new PluginConfiguration
        {
            DedicatedStoragePath = string.Empty,
            EnableSymlink = false
        };

        var result = resolver.Resolve(
            config,
            "/data/tv/ReZero",
            "Season 00",
            "/config/data");

        result.DetectionMode.Should().Be("Direct");
        result.UseSymlink.Should().BeFalse();
        result.PhysicalStoragePath.Should().Be("/data/tv/ReZero/Season 00");
        result.TargetSeasonPath.Should().Be("/data/tv/ReZero/Season 00");
    }
}
