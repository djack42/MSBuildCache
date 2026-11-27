// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.MSBuildCache.Caching;

namespace Microsoft.MSBuildCache;

/// <summary>
/// MSBuildCache plugin using a lock-free file system cache in <see cref="PluginSettings.LocalCacheRootPath"/>,
/// which several builds can use concurrently.
/// </summary>
public sealed class MSBuildCacheLocalSharedPlugin : MSBuildCachePluginBase
{
    protected override HashType HashType => HashType.Murmur;

    protected override bool RequiresExclusiveLocalCacheAccess => false;

    // The cache is a local optimization: build without it rather than fail when it cannot be opened.
    protected override bool BuildUncachedWhenCacheIsUnavailable => true;

    protected override Task<ICacheClient> CreateCacheClientAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (Settings == null
            || FingerprintFactory == null
            || ContentHasher == null
            || NugetPackageRoot == null)
        {
            throw new InvalidOperationException();
        }

        if (Settings.AsyncCachePublishing)
        {
            logger.LogWarning($"{nameof(PluginSettings.AsyncCachePublishing)} is not supported by this plugin and is ignored: outputs are published synchronously.");
        }

        FileLog fileLog = new(Path.Combine(Settings.LogDirectory, "CacheClient.log"));
#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed using Context.Logger.Dispose in the cache client implementation.
        Logger cacheLogger = new(fileLog);
#pragma warning restore CA2000 // Dispose objects before losing scope
        Context context = new(cacheLogger);

        return Task.FromResult<ICacheClient>(new FileSystemCacheClient(
            context,
            FingerprintFactory,
            ContentHasher,
            Settings.LocalCacheRootPath,
            Settings.LocalCacheSizeInMegabytes * 1024L * 1024L,
            Settings.RepoRoot,
            NugetPackageRoot,
            GetFileRealizationMode,
            Settings.MaxConcurrentCacheContentOperations,
            Settings.AsyncCacheMaterialization,
            Settings.SkipUnchangedOutputFiles,
            Settings.TouchOutputFiles));
    }
}
