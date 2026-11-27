// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fingerprint = Microsoft.MSBuildCache.Fingerprinting.Fingerprint;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class FileSystemCacheClientTests
{
    private const string OutputRelativePath = @"bin\output.dll";
    private const string CopyRelativePath = @"bin\copy.dll";

    private string _testRoot = null!;
    private string _repoRoot = null!;
    private string _cacheRoot = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MSBuildCacheTests", nameof(FileSystemCacheClientTests), Guid.NewGuid().ToString("N"));
        _repoRoot = Path.Combine(_testRoot, "repo");
        _cacheRoot = Path.Combine(_testRoot, "cache");
        Directory.CreateDirectory(Path.Combine(_repoRoot, "bin"));
    }

    [TestCleanup]
    public void TestCleanup()
    {
        foreach (string file in Directory.EnumerateFiles(_testRoot, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_testRoot, recursive: true);
    }

    [TestMethod]
    public async Task AddedNodeIsFoundAndItsOutputsArePlaced()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath, CopyRelativePath);

        await using (FileSystemCacheClient client = CreateClient())
        {
            await client.AddNodeInternalAsync(CreateNodeContext(), pathSet: null, nodeBuildResult, CancellationToken.None);
        }

        DeleteOutputs();
        NodeBuildResult? result = await GetNodeAsync();

        Assert.IsNotNull(result);
        CollectionAssert.AreEquivalent(nodeBuildResult.Outputs.Keys.ToList(), result.Outputs.Keys.ToList());
        Assert.AreEqual("content", ReadText(Path.Combine(_repoRoot, OutputRelativePath)));
        Assert.AreEqual("content", ReadText(Path.Combine(_repoRoot, CopyRelativePath)));

        // Both outputs have the same content, stored once.
        Assert.AreEqual(1, Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories).Count(file => new FileInfo(file).Length == "content".Length));
    }

    [TestMethod]
    public async Task UnknownNodeIsAMiss()
        => Assert.IsNull(await GetNodeAsync());

    [TestMethod]
    public async Task EntryWithMissingContentIsAMissAndIsRepairedByTheNextAdd()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        await AddNodeAsync(nodeBuildResult);
        File.Delete(GetContentPath(nodeBuildResult.Outputs[OutputRelativePath]));

        Assert.IsNull(await GetNodeAsync());

        await AddNodeAsync(nodeBuildResult);
        DeleteOutputs();

        Assert.IsNotNull(await GetNodeAsync());
        Assert.AreEqual("content", ReadText(Path.Combine(_repoRoot, OutputRelativePath)));
    }

    [TestMethod]
    public async Task CorruptEntryIsAMissAndIsRepairedByTheNextAdd()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        await AddNodeAsync(nodeBuildResult);
        string entryPath = Directory.GetFiles(FingerprintDirectory, "*", SearchOption.AllDirectories).Single();
        WriteText(entryPath, "not a hash");

        Assert.IsNull(await GetNodeAsync());

        await AddNodeAsync(nodeBuildResult);

        Assert.IsNotNull(await GetNodeAsync());
    }

    [TestMethod]
    public async Task CorruptNodeMetadataIsAMiss()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        await AddNodeAsync(nodeBuildResult);
        string outputContentPath = GetContentPath(nodeBuildResult.Outputs[OutputRelativePath]);
        string metadataPath = Directory.GetFiles(ContentDirectory, "*", SearchOption.AllDirectories).Single(path => path != outputContentPath);
        WriteText(metadataPath, "{ not json");

        Assert.IsNull(await GetNodeAsync());
    }

    [TestMethod]
    public async Task UnexpectedFilesInTheStoreAreIgnored()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        await AddNodeAsync(nodeBuildResult);
        string weakFingerprintDirectory = Directory.GetDirectories(FingerprintDirectory).Single();
        WriteText(Path.Combine(weakFingerprintDirectory, "zz_zz"), string.Empty);
        WriteText(Path.Combine(weakFingerprintDirectory, "stray.txt"), string.Empty);

        Assert.IsNotNull(await GetNodeAsync());
    }

    [TestMethod]
    public async Task OutputsArePlacedOverExistingReadOnlyFiles()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        await AddNodeAsync(nodeBuildResult);
        string outputPath = Path.Combine(_repoRoot, OutputRelativePath);
        WriteText(outputPath, "stale");
        File.SetAttributes(outputPath, FileAttributes.ReadOnly);

        Assert.IsNotNull(await GetNodeAsync());
        Assert.AreEqual("content", ReadText(outputPath));
    }

    [TestMethod]
    public async Task OutputRewrittenAfterHashingIsNotAdded()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        WriteText(Path.Combine(_repoRoot, OutputRelativePath), "rewritten");

        await AddNodeAsync(nodeBuildResult);

        Assert.IsNull(await GetNodeAsync(materializeOutputs: false));
        Assert.IsFalse(File.Exists(GetContentPath(nodeBuildResult.Outputs[OutputRelativePath])), "The rewritten output must not be stored under the hash of the original.");
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(LayoutDirectory, "tmp")).Length);
    }

    [TestMethod]
    public async Task OutputThatCannotBePlacedIsAMiss()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        await AddNodeAsync(nodeBuildResult);
        DeleteOutputs();

        // A directory where the output goes: the output cannot be placed.
        Directory.CreateDirectory(Path.Combine(_repoRoot, OutputRelativePath));

        Assert.IsNull(await GetNodeAsync());
    }

    [TestMethod]
    public async Task ConcurrentAddsOfTheSameNodeSucceedAndLeaveNoTempFiles()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => AddNodeAsync(nodeBuildResult))));

        Assert.AreEqual(1, Directory.GetFiles(FingerprintDirectory, "*", SearchOption.AllDirectories).Length);
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(_cacheRoot, FileSystemCacheClient.LayoutDirectoryName, "tmp")).Length);
        Assert.IsNotNull(await GetNodeAsync());
    }

    [TestMethod]
    public async Task ConcurrentLookupsOfTheSameNodeAllHit()
    {
        NodeBuildResult nodeBuildResult = WriteOutputs(OutputRelativePath);
        await AddNodeAsync(nodeBuildResult);

        // Separate clients, like separate builds: each looks up the same entry, which is read and touched concurrently.
        NodeBuildResult?[] results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => GetNodeAsync(materializeOutputs: false))));

        Assert.IsTrue(results.All(result => result != null));
    }

    // Synchronous file helpers: the async File APIs do not exist on net472.
    private static string ReadText(string path) => File.ReadAllText(path);

    private static void WriteText(string path, string contents) => File.WriteAllText(path, contents);

    private string LayoutDirectory => Path.Combine(_cacheRoot, FileSystemCacheClient.LayoutDirectoryName);

    private string ContentDirectory => Path.Combine(LayoutDirectory, "cas");

    private string FingerprintDirectory => Path.Combine(LayoutDirectory, "fp");

    private string GetContentPath(ContentHash contentHash)
    {
        string hex = contentHash.ToHex();
        return Path.Combine(ContentDirectory, hex.Substring(0, 2), hex);
    }

    private async Task AddNodeAsync(NodeBuildResult nodeBuildResult)
    {
        await using FileSystemCacheClient client = CreateClient();
        await client.AddNodeInternalAsync(CreateNodeContext(), pathSet: null, nodeBuildResult, CancellationToken.None);
    }

    private async Task<NodeBuildResult?> GetNodeAsync(bool materializeOutputs = true)
    {
        await using FileSystemCacheClient client = CreateClient();
        (_, NodeBuildResult? nodeBuildResult) = await client.GetNodeAsync(CreateNodeContext(), materializeOutputs, CancellationToken.None);
        return nodeBuildResult;
    }

    private NodeBuildResult WriteOutputs(params string[] relativePaths)
    {
        byte[] content = Encoding.UTF8.GetBytes("content");
        ContentHash contentHash = HashInfoLookup.GetContentHasher(HashType.Murmur).GetContentHash(content);
        SortedDictionary<string, ContentHash> outputs = new(StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in relativePaths)
        {
            File.WriteAllBytes(Path.Combine(_repoRoot, relativePath), content);
            outputs.Add(relativePath, contentHash);
        }

        return new NodeBuildResult(
            outputs,
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<NodeTargetResult>(),
            DateTime.UtcNow,
            DateTime.UtcNow,
            buildId: null);
    }

    private void DeleteOutputs()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(_repoRoot, "bin")))
        {
            File.Delete(file);
        }
    }

    private FileSystemCacheClient CreateClient()
    {
#pragma warning disable CA2000 // Ownership of the hasher and logger is transferred to the cache client, which disposes them.
        // Create a hasher rather than using HashInfoLookup.GetContentHasher, which returns a process-wide
        // instance that must not be disposed. CacheClient disposes the hasher it is given.
        IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        return new FileSystemCacheClient(
            new Context(new Logger()),
            new FixedFingerprintFactory(hasher),
            hasher,
            _cacheRoot,
            _repoRoot,
            nugetPackageRoot: Path.Combine(_repoRoot, "packages"),
            getFileRealizationMode: _ => FileRealizationMode.Copy,
            maxConcurrentCacheContentOperations: 4,
            enableAsyncMaterialization: false,
            skipUnchangedOutputFiles: false,
            touchOutputFiles: false);
#pragma warning restore CA2000
    }

    // ProjectInstance is only stored by NodeContext and is not used by the code under test, so this avoids
    // depending on MSBuild toolset resolution.
    private NodeContext CreateNodeContext()
        => new(
            _repoRoot,
            projectInstance: null!,
            Array.Empty<NodeContext>(),
            "project.csproj",
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private sealed class FixedFingerprintFactory : IFingerprintFactory
    {
        private readonly Fingerprint _fingerprint;

        public FixedFingerprintFactory(IContentHasher hasher)
            => _fingerprint = new Fingerprint(hasher.Info.EmptyHash.ToHashByteArray(), Array.Empty<FingerprintEntry>());

        public Task<Fingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext) => Task.FromResult<Fingerprint?>(_fingerprint);

        public PathSet? GetPathSet(NodeContext nodeContext, IReadOnlyCollection<ObservedAccess> observations) => null;

        public Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet) => Task.FromResult<Fingerprint?>(_fingerprint);

        public bool MatchesCurrentState(PathSet? cachedPathSet) => true;
    }
}
