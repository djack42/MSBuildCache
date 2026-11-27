// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Logging;

namespace Microsoft.MSBuildCache.Caching;

public static class CacheLoggerFactory
{
    /// <summary>
    /// Creates the logger of the cache client: CacheClient.log in the log directory, or a logger
    /// that discards everything when the log level is <see cref="LogLevel.None"/>.
    /// </summary>
    /// <remarks>
    /// The returned logger is expected to be disposed by the cache client, with its logs.
    /// </remarks>
    public static ILogger Create(PluginSettings settings)
    {
        if (settings.LogLevel == LogLevel.None)
        {
            return NullLogger.Instance;
        }

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed using Context.Logger.Dispose in the cache client implementation.
        FileLog fileLog = new(Path.Combine(settings.LogDirectory, "CacheClient.log"));
        return new Logger(fileLog);
#pragma warning restore CA2000 // Dispose objects before losing scope
    }
}
