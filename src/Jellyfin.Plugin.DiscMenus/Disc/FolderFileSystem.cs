using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.DiscMenus.Disc;

/// <summary>A disc stored as a plain folder (the folder that contains BDMV).</summary>
public sealed class FolderFileSystem : IDiscFileSystem
{
    public FolderFileSystem(string root)
    {
        SourcePath = root;
    }

    public string SourcePath { get; }

    public bool IsIso => false;

    public bool FileExists(string relativePath) => Resolve(relativePath, false) != null;

    public bool DirectoryExists(string relativePath) => Resolve(relativePath, true) != null;

    public IReadOnlyList<string> ListFiles(string relativeDirectory)
    {
        var dir = Resolve(relativeDirectory, true);
        if (dir == null)
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList();
    }

    public long GetLength(string relativePath) => new FileInfo(Require(relativePath)).Length;

    public IReadOnlyList<FileExtent> GetExtents(string relativePath)
    {
        var full = Require(relativePath);
        return new[] { new FileExtent(full, 0, new FileInfo(full).Length) };
    }

    public byte[] ReadAllBytes(string relativePath) => File.ReadAllBytes(Require(relativePath));

    public byte[] ReadPrefix(string relativePath, int count)
    {
        using var fs = new FileStream(Require(relativePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buf = new byte[Math.Min(count, fs.Length)];
        fs.ReadExactly(buf);
        return buf;
    }

    public void Dispose()
    {
    }

    private string Require(string relativePath)
        => Resolve(relativePath, false) ?? throw new FileNotFoundException("Not found on disc: " + relativePath);

    /// <summary>Resolves a disc-relative path, ignoring case (Blu-ray names are upper case, but copies vary).</summary>
    private string? Resolve(string relativePath, bool wantDirectory)
    {
        var current = SourcePath;
        var parts = relativePath.Split(new[] { '/', '\\' },StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var candidate = Path.Combine(current, parts[i]);
            var isDir = !last || wantDirectory;
            if (isDir ? Directory.Exists(candidate) : File.Exists(candidate))
            {
                current = candidate;
                continue;
            }

            if (!Directory.Exists(current))
            {
                return null;
            }

            var entries = isDir ? Directory.EnumerateDirectories(current) : Directory.EnumerateFiles(current);
            var match = entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), parts[i], StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }
}
