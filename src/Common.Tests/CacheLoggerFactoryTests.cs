// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Logging;
using Microsoft.MSBuildCache.Caching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public sealed class CacheLoggerFactoryTests
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Justification: Always set by MSTest.
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor.

    [TestMethod]
    [DataRow(LogLevel.Full, true)]
    [DataRow(LogLevel.Minimal, true)]
    [DataRow(LogLevel.None, false)]
    public void CacheClientLogIsWrittenUnlessLogLevelIsNone(LogLevel logLevel, bool expectLog)
    {
        string repoRoot = Path.Combine(TestContext.TestResultsDirectory!, nameof(CacheLoggerFactoryTests), Guid.NewGuid().ToString("N"));
        PluginSettings settings = new() { RepoRoot = repoRoot, LogLevel = logLevel };
        Directory.CreateDirectory(settings.LogDirectory);

        using (ILogger logger = CacheLoggerFactory.Create(settings))
        {
            Assert.AreEqual(expectLog, logger is not NullLogger);
        }

        Assert.AreEqual(expectLog, File.Exists(Path.Combine(settings.LogDirectory, "CacheClient.log")));
    }
}
