// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.MSBuildCache.Caching;

/// <summary>
/// Keeps a <see cref="FileSystemCacheClient"/> store under its maximum size, while other builds keep using it.
/// </summary>
/// <remarks>
/// When the content exceeds the maximum size, the least recently used entries are deleted until the content they reference fits in
/// <see cref="LowWaterMark"/> of the maximum size. Content no longer referenced by any entry is deleted in two phases: it is marked first,
/// and deleted by a later trim at least <c>deletionDelay</c> after, if it is still unreferenced. This leaves time to builds that read an entry
/// just before it was deleted to place its content, and to builds that put content before writing the entry referencing it.
/// Content put again by a build after it was marked (its last write time is after the mark) is not deleted.
/// Only one build trims at a time (a file lock in <c>trim\</c>); the others skip trimming. Builds check the size at most once per <c>checkInterval</c>.
/// The store can exceed its maximum size between checks, and until marked content is deleted: size the maximum with that margin.
/// </remarks>
internal sealed class FileSystemCacheTrimmer
{
    internal const string TrimDirectoryName = "trim";
    internal const double LowWaterMark = 0.8;
    internal static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultDeletionDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan MarkValidity = TimeSpan.FromDays(1);

    private const string LockFileName = "trim.lock";
    private const string LastCheckFileName = "last-check";
    private const string MarkedContentFileName = "marked.txt";

    private readonly string _contentDirectory;
    private readonly string _fingerprintDirectory;
    private readonly string _trimDirectory;
    private readonly long _maxSizeInBytes;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _deletionDelay;

    public FileSystemCacheTrimmer(string contentDirectory, string fingerprintDirectory, string trimDirectory, long maxSizeInBytes, TimeSpan checkInterval, TimeSpan deletionDelay)
    {
        _contentDirectory = contentDirectory;
        _fingerprintDirectory = fingerprintDirectory;
        _trimDirectory = trimDirectory;
        _maxSizeInBytes = maxSizeInBytes;
        _checkInterval = checkInterval;
        _deletionDelay = deletionDelay;
    }

    public TrimResult Trim(DateTime utcNow)
    {
        Directory.CreateDirectory(_trimDirectory);
        using FileStream? trimLock = TryLock();
        if (trimLock == null)
        {
            return TrimResult.Skipped("another build is trimming the cache");
        }

        string lastCheckPath = Path.Combine(_trimDirectory, LastCheckFileName);
        if (File.Exists(lastCheckPath) && utcNow - File.GetLastWriteTimeUtc(lastCheckPath) < _checkInterval)
        {
            return TrimResult.Skipped("the cache size was checked recently");
        }

        File.WriteAllText(lastCheckPath, utcNow.ToString("O", CultureInfo.InvariantCulture));
        File.SetLastWriteTimeUtc(lastCheckPath, utcNow);

        Dictionary<string, ContentFile> contentFiles = ListContent();
        long contentSize = contentFiles.Values.Sum(content => content.Length);
        if (contentSize <= _maxSizeInBytes)
        {
            return TrimResult.Done(contentSize, contentSize, entriesDeleted: 0, contentMarked: 0, contentDeleted: 0);
        }

        // Count the references to each content, and the size of the referenced content.
        List<Entry> entries = ListEntries();
        Dictionary<string, int> referenceCounts = new(StringComparer.OrdinalIgnoreCase);
        long referencedSize = 0;
        foreach (Entry entry in entries)
        {
            foreach (string contentName in entry.ContentNames)
            {
                referenceCounts.TryGetValue(contentName, out int count);
                referenceCounts[contentName] = count + 1;
                if (count == 0 && contentFiles.TryGetValue(contentName, out ContentFile? content))
                {
                    referencedSize += content.Length;
                }
            }
        }

        // Delete the least recently used entries until the content they reference fits under the low water mark.
        long targetSize = (long)(_maxSizeInBytes * LowWaterMark);
        List<string> referencedContent = referenceCounts.Keys.ToList();
        int entriesDeleted = 0;
        foreach (Entry entry in entries.OrderBy(entry => entry.LastUsedUtc))
        {
            if (referencedSize <= targetSize)
            {
                break;
            }

            if (!TryDeleteFile(entry.Path))
            {
                continue;
            }

            entriesDeleted++;
            foreach (string contentName in entry.ContentNames)
            {
                int count = referenceCounts[contentName] - 1;
                referenceCounts[contentName] = count;
                if (count == 0 && contentFiles.TryGetValue(contentName, out ContentFile? content))
                {
                    referencedSize -= content.Length;
                }
            }
        }

        // Delete the unreferenced content marked by a previous trim long enough ago, and mark the rest.
        // A mark is dropped when the content was referenced when this trim started (it may have just been unreferenced by the
        // deletions above), and renewed rather than trusted when too old (the content may have been referenced, then unreferenced, since).
        Dictionary<string, DateTime> markedContent = ReadMarkedContent();
        foreach (string contentName in referencedContent)
        {
            markedContent.Remove(contentName);
        }

        foreach (KeyValuePair<string, DateTime> mark in markedContent.ToList())
        {
            if (utcNow - mark.Value > _deletionDelay + MarkValidity)
            {
                markedContent.Remove(mark.Key);
            }
        }

        Dictionary<string, DateTime> stillMarkedContent = new(StringComparer.OrdinalIgnoreCase);
        int contentMarked = 0;
        int contentDeleted = 0;
        long deletedSize = 0;
        foreach (ContentFile content in contentFiles.Values)
        {
            if (referenceCounts.TryGetValue(content.Name, out int count) && count > 0)
            {
                continue;
            }

            if (markedContent.TryGetValue(content.Name, out DateTime markedUtc) && utcNow - markedUtc >= _deletionDelay)
            {
                if (WasPutAfter(content.Path, markedUtc))
                {
                    // A build put it again since it was marked, for an entry written after this trim listed the entries: keep it.
                    continue;
                }

                if (TryDeleteFile(content.Path))
                {
                    contentDeleted++;
                    deletedSize += content.Length;
                    continue;
                }
            }

            if (markedContent.TryGetValue(content.Name, out DateTime previousMarkUtc))
            {
                stillMarkedContent[content.Name] = previousMarkUtc;
            }
            else
            {
                stillMarkedContent[content.Name] = utcNow;
                contentMarked++;
            }
        }

        WriteMarkedContent(stillMarkedContent);
        DeleteEmptyDirectories(_fingerprintDirectory);
        DeleteEmptyDirectories(_contentDirectory);

        return TrimResult.Done(contentSize, contentSize - deletedSize, entriesDeleted, contentMarked, contentDeleted);
    }

    private FileStream? TryLock()
    {
        try
        {
            return new FileStream(Path.Combine(_trimDirectory, LockFileName), FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private Dictionary<string, ContentFile> ListContent()
    {
        Dictionary<string, ContentFile> contentFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in new DirectoryInfo(_contentDirectory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                contentFiles[file.Name] = new ContentFile(file.Name, file.FullName, file.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted meanwhile.
            }
        }

        return contentFiles;
    }

    private List<Entry> ListEntries()
    {
        List<Entry> entries = new();
        foreach (FileInfo file in new DirectoryInfo(_fingerprintDirectory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                DateTime lastUsedUtc = file.LastWriteTimeUtc;
                string text;
                using (FileStream stream = new(file.FullName, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new(stream, Encoding.UTF8))
                {
                    text = reader.ReadToEnd();
                }

                // The first line is the selector (path set hash, then output), the others are content hashes. The path set is content too.
                string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                IEnumerable<string> contentNames = lines.Skip(1);
                if (lines.Length > 0)
                {
                    contentNames = contentNames.Prepend(lines[0].Split('_')[0]);
                }

                entries.Add(new Entry(file.FullName, lastUsedUtc, contentNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Deleted or replaced meanwhile.
            }
        }

        return entries;
    }

    private Dictionary<string, DateTime> ReadMarkedContent()
    {
        Dictionary<string, DateTime> markedContent = new(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(_trimDirectory, MarkedContentFileName);
        if (!File.Exists(path))
        {
            return markedContent;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            string[] parts = line.Split(' ');
            if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                markedContent[parts[0]] = new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        return markedContent;
    }

    private void WriteMarkedContent(Dictionary<string, DateTime> markedContent)
    {
        string path = Path.Combine(_trimDirectory, MarkedContentFileName);
        string tempPath = path + ".tmp";
        File.WriteAllLines(tempPath, markedContent.Select(pair => $"{pair.Key} {pair.Value.Ticks.ToString(CultureInfo.InvariantCulture)}"));
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.Move(tempPath, path);
    }

    private static void DeleteEmptyDirectories(string root)
    {
        foreach (DirectoryInfo directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            try
            {
                if (!directory.EnumerateFileSystemInfos().Any())
                {
                    // Not recursive: fails, and is ignored, if a build created a file in it meanwhile.
                    directory.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use, left for the next trim.
            }
        }
    }

    private static bool WasPutAfter(string path, DateTime utc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) > utc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ContentFile(string Name, string Path, long Length);

    private sealed record Entry(string Path, DateTime LastUsedUtc, string[] ContentNames);
}

internal sealed record TrimResult(string? SkippedReason, long SizeBefore, long SizeAfter, int EntriesDeleted, int ContentMarked, int ContentDeleted)
{
    public static TrimResult Skipped(string reason) => new(reason, 0, 0, 0, 0, 0);

    public static TrimResult Done(long sizeBefore, long sizeAfter, int entriesDeleted, int contentMarked, int contentDeleted)
        => new(null, sizeBefore, sizeAfter, entriesDeleted, contentMarked, contentDeleted);

    public override string ToString()
        => SkippedReason != null
            ? $"Cache trim skipped: {SkippedReason}."
            : $"Cache trim: size {SizeBefore / (1024 * 1024)} MB -> {SizeAfter / (1024 * 1024)} MB, {EntriesDeleted} entries deleted, {ContentMarked} unreferenced content files marked, {ContentDeleted} deleted.";
}
