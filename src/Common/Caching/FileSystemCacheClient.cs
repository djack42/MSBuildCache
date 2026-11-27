// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.CopyOnWrite;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using WeakFingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.Caching;

/// <summary>
/// A cache client storing everything as plain files in a directory, without any lock or database, so several builds
/// (MSBuild processes) can use the same cache concurrently.
/// </summary>
/// <remarks>
/// Layout of <c>&lt;cache root&gt;\FileSystemCache.v1</c> (the version changes with the layout):
/// <list type="bullet">
///   <item><description><c>cas\&lt;2 hex&gt;\&lt;hash hex&gt;</c>: content, named by its hash. Written to <c>tmp\</c>, flushed, then moved into place, so a file in <c>cas\</c> is always complete.</description></item>
///   <item><description><c>fp\&lt;weak fingerprint hex&gt;\&lt;selector id&gt;</c>: one entry per selector, named by a hash of the selector to keep paths short.
///   It lists the selector, then the content hashes of the node (node build result first, then the outputs).
///   Written last, once all its content is in <c>cas\</c>. Its last write time is its last use.</description></item>
///   <item><description><c>tmp\</c>: files being written. Leftovers of crashed builds are deleted after a day.</description></item>
/// </list>
/// Concurrent writers of the same content or entry are idempotent: the first one wins, except that an entry referencing missing content is replaced.
/// Any store error while looking up a node is a cache miss, and any store error while adding a node skips the add: the store never fails the build.
/// Node metadata (node build results and path sets) is verified against its hash when read. Outputs are verified when stored, not when placed, for speed.
/// Placing a file clones it when the file system supports copy-on-write (ReFS, Dev Drive) and copies it otherwise,
/// so <see cref="FileRealizationMode"/> (hardlinks) does not apply.
/// </remarks>
public sealed class FileSystemCacheClient : CacheClient
{
    internal const string LayoutDirectoryName = "FileSystemCache.v1";
    private const string ContentDirectoryName = "cas";
    private const string FingerprintDirectoryName = "fp";
    private const string TempDirectoryName = "tmp";
    private static readonly TimeSpan TempFileMaxAge = TimeSpan.FromDays(1);
    private const int SelectorIdLength = 32;
    private static readonly char[] LineSeparators = ['\r', '\n'];

    private readonly ICopyOnWriteFilesystem _copyOnWriteFilesystem = CopyOnWriteFilesystemFactory.GetInstance();
    private readonly IContentHasher _contentHasher;
    private readonly HashType _hashType;
    private readonly string _contentDirectory;
    private readonly string _fingerprintDirectory;
    private readonly string _tempDirectory;

    // Content placed per destination path during this build. Several nodes can produce the same output (identical duplicate outputs)
    // and place it concurrently: place it once, and serialize placements to the same path.
    private readonly ConcurrentDictionary<string, PlacedFile> _placedFiles = new(StringComparer.OrdinalIgnoreCase);

    public FileSystemCacheClient(
        Context rootContext,
        IFingerprintFactory fingerprintFactory,
        IContentHasher hasher,
        string cacheRootPath,
        string repoRoot,
        string nugetPackageRoot,
        Func<string, FileRealizationMode> getFileRealizationMode,
        int maxConcurrentCacheContentOperations,
        bool enableAsyncMaterialization,
        bool skipUnchangedOutputFiles,
        bool touchOutputFiles)
        : base(
            rootContext,
            fingerprintFactory,
            hasher,
            repoRoot,
            nugetPackageRoot,
            getFileRealizationMode,
            localCache: null,
            localCas: null,
            maxConcurrentCacheContentOperations,
            // Async publishing is not supported: in CacheClient it captures outputs through the BuildXL local cache
            // (LocalCacheSession.PutFileAsync), which this client does not have. It would gain little here anyway: capturing
            // an output is a clone (or copy) into cas\ plus a hash, and an entry is one small file, unlike a RocksDB or remote write.
            // Supporting it would need a CacheClient extension point to capture content into this store.
            enableAsyncPublishing: false,
            enableAsyncMaterialization,
            skipUnchangedOutputFiles,
            touchOutputFiles)
    {
        _contentHasher = hasher;
        _hashType = hasher.Info.HashType;
        string layoutDirectory = Path.Combine(cacheRootPath, LayoutDirectoryName);
        _contentDirectory = Path.Combine(layoutDirectory, ContentDirectoryName);
        _fingerprintDirectory = Path.Combine(layoutDirectory, FingerprintDirectoryName);
        _tempDirectory = Path.Combine(layoutDirectory, TempDirectoryName);
        Directory.CreateDirectory(_contentDirectory);
        Directory.CreateDirectory(_fingerprintDirectory);
        Directory.CreateDirectory(_tempDirectory);
        DeleteStaleTempFiles(rootContext);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        PutOrPlaceFileGate.Dispose();
    }

    protected override Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cancellationToken)
        => Task.Run(() => OpenStream(contentHash), cancellationToken);

    // Only node metadata is read through streams (outputs are placed as files), so it is small enough to read at once and verify.
    private OpenStreamResult OpenStream(ContentHash contentHash)
    {
        string contentPath = GetContentPath(contentHash);
        byte[] bytes;
        try
        {
            using FileStream stream = new(contentPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read | FileShare.Delete);
            bytes = new byte[stream.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            return new OpenStreamResult(OpenStreamResult.ResultCode.ContentNotFound, $"Content {contentHash.ToShortHash()} could not be read from {contentPath}: {ex.Message}");
        }

        ContentHash actualHash = _contentHasher.GetContentHash(bytes);
        if (actualHash != contentHash)
        {
            return new OpenStreamResult(OpenStreamResult.ResultCode.ContentNotFound, $"Content {contentHash.ToShortHash()} at {contentPath} is corrupt (hash {actualHash.ToShortHash()}).");
        }

        return new OpenStreamResult(new MemoryStream(bytes, writable: false));
    }

    protected override async Task<AddNodeResult> AddNodeAsync(
        Context context,
        StrongFingerprint fingerprint,
        IReadOnlyDictionary<string, ContentHash> outputs,
        (ContentHash hash, byte[] bytes) nodeBuildResultBytes,
        (ContentHash hash, byte[] bytes)? pathSetBytes,
        CancellationToken cancellationToken)
    {
        string entryPath = GetEntryPath(fingerprint.WeakFingerprint, fingerprint.Selector);
        try
        {
            // Content first, so an entry never references missing content (except after eviction).
            // Content is put even when the entry exists, so an entry whose content was lost gets repaired below.
            List<Task> putTasks = new(outputs.Count + 2)
            {
                Task.Run(() => PutBytes(nodeBuildResultBytes.hash, nodeBuildResultBytes.bytes), cancellationToken),
            };

            if (pathSetBytes != null)
            {
                (ContentHash hash, byte[] bytes) pathSet = pathSetBytes.Value;
                putTasks.Add(Task.Run(() => PutBytes(pathSet.hash, pathSet.bytes), cancellationToken));
            }

            // Several outputs can have the same content: store each content once.
            Dictionary<ContentHash, string> contentAbsolutePaths = new(outputs.Count);
            foreach (KeyValuePair<string, ContentHash> output in outputs)
            {
                if (!contentAbsolutePaths.ContainsKey(output.Value))
                {
                    contentAbsolutePaths.Add(output.Value, output.Key);
                }
            }

            foreach (KeyValuePair<ContentHash, string> content in contentAbsolutePaths)
            {
                putTasks.Add(PutOrPlaceFileGate.GatedOperationAsync(async (_, _) =>
                {
                    await PutFileAsync(content.Key, content.Value);
                    return true;
                }, cancellationToken));
            }

            await Task.WhenAll(putTasks);

            EntryData? existingEntry = TryReadEntry(context, entryPath, fingerprint.Selector);
            if (existingEntry != null && GetMissingContent(existingEntry) == null)
            {
                Tracer.Debug(context, $"Found existing entry {entryPath}");
                return AddNodeResult.AlreadyExists;
            }

            // The entry lists its selector, then the node build result, then the outputs.
            IEnumerable<string> entryLines = new[] { FormatSelector(fingerprint.Selector), nodeBuildResultBytes.hash.ToHex() }
                .Concat(contentAbsolutePaths.Keys.Select(hash => hash.ToHex()));
            string tempPath = WriteTempFile(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, entryLines)));

            // An existing entry here is incomplete or unreadable: replace it.
            bool added = File.Exists(entryPath)
                ? ReplaceEntry(context, tempPath, entryPath)
                : MoveIntoPlace(tempPath, entryPath);
            Tracer.Debug(context, $"{(added ? "Added" : "Found existing")} entry {entryPath}");
            return added ? AddNodeResult.Added : AddNodeResult.AlreadyExists;
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            Tracer.Warning(context, $"Skipped adding entry {entryPath}: {ex}");
            return AddNodeResult.Skipped;
        }
    }

    protected override async IAsyncEnumerable<Selector> GetSelectors(Context context, WeakFingerprint fingerprint, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        string weakFingerprintDirectory = Path.Combine(_fingerprintDirectory, fingerprint.ToHex());
        List<FileInfo> entries;
        try
        {
            // Most recent entries first, the most likely to match.
            entries = new DirectoryInfo(weakFingerprintDirectory).EnumerateFiles().OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            if (ex is not DirectoryNotFoundException)
            {
                Tracer.Debug(context, $"Could not list entries in {weakFingerprintDirectory}: {ex.Message}");
            }

            yield break;
        }

        foreach (FileInfo entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EntryData? entryData = TryReadEntry(context, entry.FullName, expectedSelector: null);
            if (entryData == null)
            {
                Tracer.Debug(context, $"Skipping unexpected entry {entry.FullName}");
                continue;
            }

            yield return entryData.Selector;
        }
    }

    protected override Task<ICacheEntry?> GetCacheEntryAsync(Context context, StrongFingerprint cacheStrongFingerprint, CancellationToken cancellationToken)
        => Task.Run<ICacheEntry?>(() => GetCacheEntry(context, cacheStrongFingerprint), cancellationToken);

    private CacheEntry? GetCacheEntry(Context context, StrongFingerprint cacheStrongFingerprint)
    {
        string entryPath = GetEntryPath(cacheStrongFingerprint.WeakFingerprint, cacheStrongFingerprint.Selector);
        EntryData? entry = TryReadEntry(context, entryPath, cacheStrongFingerprint.Selector);
        if (entry == null)
        {
            return null;
        }

        // Ensure all the content is available before declaring victory: it may have been evicted.
        ContentHash? missingContent = GetMissingContent(entry);
        if (missingContent != null)
        {
            Tracer.Debug(context, $"Entry {entryPath} is incomplete (missing content {missingContent.Value.ToShortHash()}), ignoring it.");
            return null;
        }

        // Mark the entry as recently used. Best effort: another build may be touching or replacing it at the same time.
        try
        {
            File.SetLastWriteTimeUtc(entryPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            Tracer.Debug(context, $"Could not mark entry {entryPath} as used: {ex.Message}");
        }

        return new CacheEntry(this, entry.ContentHashes[0]);
    }

    /// <returns>The entry, or null if it does not exist, cannot be read or parsed, or belongs to another selector than <paramref name="expectedSelector"/>.</returns>
    private EntryData? TryReadEntry(Context context, string entryPath, Selector? expectedSelector)
    {
        try
        {
            string text;

            // Share everything: other builds may read, touch or replace the entry at the same time.
            using (FileStream stream = new(entryPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new(stream, Encoding.UTF8))
            {
                text = reader.ReadToEnd();
            }

            string[] lines = text.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                Tracer.Debug(context, $"Entry {entryPath} is incomplete, ignoring it.");
                return null;
            }

            Selector selector = ParseSelector(lines[0]);
            if (expectedSelector != null && !string.Equals(lines[0], FormatSelector(expectedSelector.Value), StringComparison.OrdinalIgnoreCase))
            {
                Tracer.Debug(context, $"Entry {entryPath} belongs to another selector, ignoring it.");
                return null;
            }

            ContentHash[] contentHashes = lines
                .Skip(1)
                .Select(line => new ContentHash(_hashType, HexUtilities.HexToBytes(line)))
                .ToArray();
            return new EntryData(selector, contentHashes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (IsStoreException(ex) || ex is ArgumentException or FormatException)
        {
            Tracer.Debug(context, $"Entry {entryPath} could not be read, ignoring it: {ex.Message}");
            return null;
        }
    }

    private ContentHash? GetMissingContent(EntryData entry)
    {
        // The selector's path set is content too, except for the empty selector, which has none.
        IEnumerable<ContentHash> contentHashes = entry.Selector.ContentHash == _contentHasher.Info.EmptyHash
            ? entry.ContentHashes
            : entry.ContentHashes.Prepend(entry.Selector.ContentHash);
        foreach (ContentHash contentHash in contentHashes)
        {
            if (!File.Exists(GetContentPath(contentHash)))
            {
                return contentHash;
            }
        }

        return null;
    }

    private static string FormatSelector(Selector selector) => $"{selector.ContentHash.ToHex()}_{ToHex(selector.Output)}";

    private Selector ParseSelector(string text)
    {
        string[] parts = text.Split('_');
        if (parts.Length != 2)
        {
            throw new FormatException($"Invalid selector `{text}`.");
        }

        return new Selector(new ContentHash(_hashType, HexUtilities.HexToBytes(parts[0])), HexUtilities.HexToBytes(parts[1]));
    }

    internal string GetContentPath(ContentHash contentHash)
    {
        string hex = contentHash.ToHex();
        return Path.Combine(_contentDirectory, hex.Substring(0, 2), hex);
    }

    // The entry is named by a hash of its selector rather than the selector itself (two hashes), so paths stay short:
    // MSBuild on .NET Framework may not support long paths.
    internal string GetEntryPath(WeakFingerprint weakFingerprint, Selector selector)
    {
        string selectorId = _contentHasher.GetContentHash(Encoding.UTF8.GetBytes(FormatSelector(selector))).ToHex().Substring(0, SelectorIdLength);
        return Path.Combine(_fingerprintDirectory, weakFingerprint.ToHex(), selectorId);
    }

    private static string ToHex(byte[]? bytes)
    {
        StringBuilder hex = new((bytes?.Length ?? 0) * 2);
        foreach (byte b in bytes ?? Array.Empty<byte>())
        {
            hex.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    private void PutBytes(ContentHash contentHash, byte[] bytes)
    {
        string contentPath = GetContentPath(contentHash);
        if (TouchExistingContent(contentPath))
        {
            return;
        }

        MoveIntoPlace(WriteTempFile(bytes), contentPath);
    }

    private async Task PutFileAsync(ContentHash contentHash, string sourcePath)
    {
        string contentPath = GetContentPath(contentHash);
        if (TouchExistingContent(contentPath))
        {
            return;
        }

        string tempPath = GetTempPath();
        try
        {
            CloneOrCopyFile(sourcePath, tempPath);

            // The output may have been rewritten since it was hashed: store it only if the snapshot has the expected content.
            ContentHash actualHash = await _contentHasher.GetFileHashAsync(tempPath);
            if (actualHash != contentHash)
            {
                throw new IOException($"{sourcePath} changed after it was hashed (hash {actualHash.ToShortHash()} instead of {contentHash.ToShortHash()}).");
            }

            // Flush before the file is moved into place, so a power loss never leaves a truncated file under its final name.
            FlushToDisk(tempPath);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        MoveIntoPlace(tempPath, contentPath);
    }

    /// <summary>
    /// Marks existing content as just written, so a trim that marked it unreferenced before this build references it again does not delete it.
    /// </summary>
    /// <returns>True if the content exists.</returns>
    private static bool TouchExistingContent(string contentPath)
    {
        if (!File.Exists(contentPath))
        {
            return false;
        }

        try
        {
            File.SetLastWriteTimeUtc(contentPath, DateTime.UtcNow);
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            // Deleted or in use meanwhile: the entry check after the puts finds missing content.
        }

        return true;
    }

    private string GetTempPath() => Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));

    private string WriteTempFile(byte[] contents)
    {
        string tempPath = GetTempPath();
        try
        {
            using FileStream stream = new(tempPath, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None);
            stream.Write(contents, 0, contents.Length);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        return tempPath;
    }

    private static void FlushToDisk(string path)
    {
        // Share everything: a scanner (e.g. an antivirus) may have the file open already.
        using FileStream stream = new(path, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Flush(flushToDisk: true);
    }

    /// <returns>True if moved, false if the destination already existed (another build wrote it first).</returns>
    private static bool MoveIntoPlace(string tempPath, string destinationPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(tempPath, destinationPath);
            return true;
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            TryDeleteFile(tempPath);
            return false;
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    /// <summary>Replaces an existing incomplete or unreadable entry. Readers share deletion, so they do not block it.</summary>
    private bool ReplaceEntry(Context context, string tempPath, string entryPath)
    {
        try
        {
            File.Delete(entryPath);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }

        Tracer.Debug(context, $"Replacing incomplete entry {entryPath}");
        return MoveIntoPlace(tempPath, entryPath);
    }

    private void PlaceFile(ContentHash contentHash, string contentPath, string destinationPath)
    {
        PlacedFile placedFile = _placedFiles.GetOrAdd(destinationPath, _ => new PlacedFile());
        lock (placedFile)
        {
            if (placedFile.ContentHash == contentHash)
            {
                return;
            }

            if (File.Exists(destinationPath))
            {
                // A read-only destination can be neither deleted nor overwritten.
                FileAttributes attributes = File.GetAttributes(destinationPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(destinationPath);
            }

            CloneOrCopyFile(contentPath, destinationPath);
            placedFile.ContentHash = contentHash;
        }
    }

    private void CloneOrCopyFile(string sourcePath, string destinationPath)
    {
        if (_copyOnWriteFilesystem.CopyOnWriteLinkSupportedBetweenPaths(sourcePath, destinationPath))
        {
            try
            {
                _copyOnWriteFilesystem.CloneFile(sourcePath, destinationPath);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or (IOException and not FileNotFoundException and not DirectoryNotFoundException))
            {
                // Fall back to a copy, e.g. when the clone is refused for this file.
            }
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private void DeleteStaleTempFiles(Context context)
    {
        try
        {
            DateTime threshold = DateTime.UtcNow - TempFileMaxAge;
            foreach (FileInfo file in new DirectoryInfo(_tempDirectory).EnumerateFiles())
            {
                // Creation time: a clone keeps the last write time of its source, which can be old.
                if (file.CreationTimeUtc < threshold)
                {
                    TryDeleteFile(file.FullName);
                }
            }
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            Tracer.Debug(context, $"Could not clean up {_tempDirectory}: {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsStoreException(ex))
        {
            // Left for the next cleanup.
        }
    }

    private static bool IsStoreException(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private sealed record EntryData(Selector Selector, ContentHash[] ContentHashes);

    private sealed class PlacedFile
    {
        public ContentHash? ContentHash { get; set; }
    }

    private sealed class CacheEntry : ICacheEntry
    {
        private readonly FileSystemCacheClient _client;
        private readonly ContentHash _nodeBuildResultHash;

        public CacheEntry(FileSystemCacheClient client, ContentHash nodeBuildResultHash)
        {
            _client = client;
            _nodeBuildResultHash = nodeBuildResultHash;
        }

        public async Task<Stream?> GetNodeBuildResultAsync(Context context, CancellationToken cancellationToken)
        {
            OpenStreamResult streamResult = await _client.OpenStreamAsync(context, _nodeBuildResultHash, cancellationToken);
            return streamResult.Succeeded ? streamResult.Stream : null;
        }

        public async Task PlaceFilesAsync(Context context, IReadOnlyDictionary<string, ContentHash> files, CancellationToken cancellationToken)
        {
            List<Task> placeTasks = new(files.Count);
            foreach (KeyValuePair<string, ContentHash> file in files)
            {
                string destinationPath = file.Key;
                ContentHash contentHash = file.Value;
                placeTasks.Add(_client.PutOrPlaceFileGate.GatedOperationAsync(
                    (_, _) => Task.Run(
                        () =>
                        {
                            _client.CreateParentDirectory(destinationPath);
                            _client.PlaceFile(contentHash, _client.GetContentPath(contentHash), destinationPath);
                            return true;
                        },
                        cancellationToken),
                    cancellationToken));
            }

            await Task.WhenAll(placeTasks);
        }

        public void Dispose()
        {
        }
    }
}
