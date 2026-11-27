// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fingerprint = Microsoft.MSBuildCache.Fingerprinting.Fingerprint;
using WeakFingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class CacheClientTests
{
    private static readonly string TestRepoRoot = Path.Combine(Path.GetTempPath(), "MSBuildCacheTests", "repo");

    [TestMethod]
    public void AsyncPublishingRequiresALocalCache()
        => Assert.ThrowsExactly<ArgumentException>(() => new NoLocalCacheClient(enableAsyncPublishing: true));

    [TestMethod]
    public async Task ClientWithoutLocalCacheReportsItAndDisposes()
    {
        NoLocalCacheClient client = new(enableAsyncPublishing: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => client.GetLocalCacheSession());

        await client.DisposeAsync();
    }

    private sealed class NoLocalCacheClient : CacheClient
    {
        public NoLocalCacheClient(bool enableAsyncPublishing)
#pragma warning disable CA2000 // Ownership of the hasher and logger is transferred to the cache client, which disposes them.
            : base(
                new Context(new Logger()),
                new NullFingerprintFactory(),
                HashInfoLookup.Find(HashType.Murmur).CreateContentHasher(),
                TestRepoRoot,
                Path.Combine(TestRepoRoot, "packages"),
                _ => FileRealizationMode.Copy,
                localCache: null,
                localCas: null,
                maxConcurrentCacheContentOperations: 1,
                enableAsyncPublishing,
                enableAsyncMaterialization: false,
                skipUnchangedOutputFiles: false,
                touchOutputFiles: false)
#pragma warning restore CA2000
        {
        }

        public IContentSession GetLocalCacheSession() => LocalCacheSession;

        protected override Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override Task<AddNodeResult> AddNodeAsync(
            Context context,
            StrongFingerprint fingerprint,
            IReadOnlyDictionary<string, ContentHash> outputs,
            (ContentHash hash, byte[] bytes) nodeBuildResultBytes,
            (ContentHash hash, byte[] bytes)? pathSetBytes,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override IAsyncEnumerable<Selector> GetSelectors(Context context, WeakFingerprint fingerprint, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        protected override Task<ICacheEntry?> GetCacheEntryAsync(Context context, StrongFingerprint cacheStrongFingerprint, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class NullFingerprintFactory : IFingerprintFactory
    {
        public Task<Fingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext) => Task.FromResult<Fingerprint?>(null);

        public PathSet? GetPathSet(NodeContext nodeContext, IReadOnlyCollection<ObservedAccess> observations) => null;

        public Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet) => Task.FromResult<Fingerprint?>(null);

        public bool MatchesCurrentState(PathSet? cachedPathSet) => true;
    }
}
