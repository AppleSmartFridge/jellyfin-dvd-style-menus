using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.DiscMenus.Disc;

/// <summary>A run of bytes inside a real file on disk.</summary>
/// <param name="SourcePath">The file on disk (the ISO, or the m2ts file itself).</param>
/// <param name="Offset">Byte offset inside <paramref name="SourcePath"/>.</param>
/// <param name="Length">Length in bytes.</param>
public sealed record FileExtent(string SourcePath, long Offset, long Length);

/// <summary>Read-only view of a Blu-ray disc's files, from a folder or an ISO image.</summary>
public interface IDiscFileSystem : IDisposable
{
    /// <summary>Gets the path the disc was opened from (folder that contains BDMV, or the .iso file).</summary>
    string SourcePath { get; }

    /// <summary>Gets a value indicating whether the source is an ISO image.</summary>
    bool IsIso { get; }

    bool FileExists(string relativePath);

    bool DirectoryExists(string relativePath);

    /// <summary>Lists file names (not paths) directly inside a directory. Empty if it does not exist.</summary>
    IReadOnlyList<string> ListFiles(string relativeDirectory);

    long GetLength(string relativePath);

    /// <summary>Gets where the file's bytes live on disk, in order.</summary>
    IReadOnlyList<FileExtent> GetExtents(string relativePath);

    /// <summary>Reads a whole (small) file.</summary>
    byte[] ReadAllBytes(string relativePath);

    /// <summary>Reads <paramref name="count"/> bytes from the start of a file (fewer if the file is shorter).</summary>
    byte[] ReadPrefix(string relativePath, int count);
}

/// <summary>Opens the right <see cref="IDiscFileSystem"/> for a path.</summary>
public static class DiscFileSystem
{
    /// <summary>
    /// Opens a disc. <paramref name="path"/> may be an .iso file, a folder containing BDMV,
    /// the BDMV folder itself, or BDMV/index.bdmv.
    /// </summary>
    public static IDiscFileSystem? TryOpen(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            if (File.Exists(path))
            {
                if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                {
                    var iso = UdfIsoFileSystem.TryOpen(path);
                    if (iso != null && iso.FileExists("BDMV/index.bdmv"))
                    {
                        return iso;
                    }

                    iso?.Dispose();
                    return null;
                }

                if (Path.GetFileName(path).Equals("index.bdmv", StringComparison.OrdinalIgnoreCase))
                {
                    path = Path.GetDirectoryName(path)!;
                }
                else
                {
                    return null;
                }
            }

            if (!Directory.Exists(path))
            {
                return null;
            }

            if (Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!;
            }

            var folder = new FolderFileSystem(path);
            return folder.FileExists("BDMV/index.bdmv") ? folder : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
