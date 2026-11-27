// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using Microsoft.MSBuildCache.Caching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class FileSystemCacheTrimmerTests
{
    private const int ContentSize = 100;
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeletionDelay = TimeSpan.FromHours(1);

    private string _testRoot = null!;
    private string _contentDirectory = null!;
    private string _fingerprintDirectory = null!;
    private string _trimDirectory = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "MSBuildCacheTests", nameof(FileSystemCacheTrimmerTests), Guid.NewGuid().ToString("N"));
        _contentDirectory = Path.Combine(_testRoot, "cas");
        _fingerprintDirectory = Path.Combine(_testRoot, "fp");
        _trimDirectory = Path.Combine(_testRoot, "trim");
        Directory.CreateDirectory(_contentDirectory);
        Directory.CreateDirectory(_fingerprintDirectory);
    }

    [TestCleanup]
    public void TestCleanup() => Directory.Delete(_testRoot, recursive: true);

    [TestMethod]
    public void NothingIsDeletedUnderTheMaximumSize()
    {
        AddEntry("entry1", Now.AddDays(-2), "A1", "B1");

        TrimResult result = CreateTrimmer(maxSizeInBytes: 2 * ContentSize).Trim(Now);

        Assert.AreEqual(0, result.EntriesDeleted);
        Assert.IsTrue(EntryExists("entry1"));
        Assert.IsTrue(ContentExists("A1"));
    }

    [TestMethod]
    public void LeastRecentlyUsedEntriesAreDeletedAndTheirContentDeletedByALaterTrim()
    {
        AddEntry("old", Now.AddDays(-3), "A1", "S1");
        AddEntry("middle", Now.AddDays(-2), "B1", "S1");
        AddEntry("recent", Now.AddDays(-1), "C1", "S1");

        // 4 contents of 100 bytes, max 300: keep the content referenced by the most recent entries under 240 bytes.
        TrimResult first = CreateTrimmer(maxSizeInBytes: 3 * ContentSize).Trim(Now);

        Assert.AreEqual(2, first.EntriesDeleted);
        Assert.IsFalse(EntryExists("old"));
        Assert.IsFalse(EntryExists("middle"));
        Assert.IsTrue(EntryExists("recent"));

        // Unreferenced content is only marked, so builds still placing it from the deleted entries find it.
        Assert.AreEqual(2, first.ContentMarked);
        Assert.IsTrue(ContentExists("A1"));
        Assert.IsTrue(ContentExists("B1"));

        TrimResult tooEarly = CreateTrimmer(maxSizeInBytes: 3 * ContentSize).Trim(Now + CheckInterval);
        Assert.AreEqual(0, tooEarly.ContentDeleted);

        TrimResult later = CreateTrimmer(maxSizeInBytes: 3 * ContentSize).Trim(Now + DeletionDelay);

        Assert.AreEqual(2, later.ContentDeleted);
        Assert.IsFalse(ContentExists("A1"));
        Assert.IsFalse(ContentExists("B1"));
        Assert.IsTrue(ContentExists("C1"));
        Assert.IsTrue(ContentExists("S1"));
    }

    [TestMethod]
    public void MarkedContentReferencedAgainIsNotDeleted()
    {
        AddEntry("old", Now.AddDays(-3), "A1");
        AddEntry("recent", Now.AddDays(-1), "B1");
        CreateTrimmer(maxSizeInBytes: ContentSize).Trim(Now);
        Assert.IsTrue(ContentExists("A1"), "A1 is only marked by the first trim.");

        // Another build adds an entry referencing A1 again.
        AddEntry("again", Now.AddMinutes(30), "A1");
        TrimResult later = CreateTrimmer(maxSizeInBytes: 10 * ContentSize).Trim(Now + DeletionDelay);
        TrimResult overCap = CreateTrimmer(maxSizeInBytes: ContentSize * 3 / 2).Trim(Now + DeletionDelay + CheckInterval);

        Assert.AreEqual(0, later.ContentDeleted);
        Assert.AreEqual(1, overCap.ContentDeleted, "Only B1, still unreferenced, is deleted.");
        Assert.IsTrue(ContentExists("A1"));
        Assert.IsFalse(ContentExists("B1"));
    }

    [TestMethod]
    public void TrimIsSkippedWhileAnotherBuildTrims()
    {
        Directory.CreateDirectory(_trimDirectory);
        using FileStream otherTrimmerLock = new(Path.Combine(_trimDirectory, "trim.lock"), FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None);

        TrimResult result = CreateTrimmer(maxSizeInBytes: 0).Trim(Now);

        Assert.IsNotNull(result.SkippedReason);
    }

    [TestMethod]
    public void SizeIsCheckedAtMostOncePerInterval()
    {
        AddEntry("entry1", Now.AddDays(-1), "A1");
        CreateTrimmer(maxSizeInBytes: 10 * ContentSize).Trim(Now);

        TrimResult result = CreateTrimmer(maxSizeInBytes: 0).Trim(Now + CheckInterval - TimeSpan.FromSeconds(1));

        Assert.IsNotNull(result.SkippedReason);
        Assert.IsTrue(EntryExists("entry1"));
    }

    [TestMethod]
    public void PathSetsOfEntriesAreReferencedContent()
    {
        AddContent("P1");
        AddEntryWithPathSet("old", Now.AddDays(-2), "P1", "A1");
        AddEntryWithPathSet("recent", Now.AddDays(-1), "P1", "B1");

        // 3 contents of 100 bytes, max 250: deleting "old" brings the referenced content (P1, B1) to 200 bytes.
        TrimResult first = CreateTrimmer(maxSizeInBytes: 5 * ContentSize / 2).Trim(Now);

        Assert.AreEqual(1, first.EntriesDeleted);
        Assert.AreEqual(1, first.ContentMarked, "Only A1 is unreferenced.");

        TrimResult later = CreateTrimmer(maxSizeInBytes: 5 * ContentSize / 2).Trim(Now + DeletionDelay);

        Assert.AreEqual(1, later.ContentDeleted);
        Assert.IsFalse(ContentExists("A1"));
        Assert.IsTrue(ContentExists("P1"), "The path set of the remaining entry must be kept.");
        Assert.IsTrue(ContentExists("B1"));
    }

    [TestMethod]
    public void MarkedContentPutAgainIsNotDeleted()
    {
        AddEntry("old", Now.AddDays(-3), "A1");
        AddEntry("recent", Now.AddDays(-1), "B1");
        CreateTrimmer(maxSizeInBytes: ContentSize).Trim(Now);
        Assert.IsTrue(ContentExists("A1"), "A1 is only marked by the first trim.");

        // A build puts A1 again for an entry it writes after the next trim lists the entries.
        File.SetLastWriteTimeUtc(ContentPath("A1"), Now.AddMinutes(30));
        TrimResult later = CreateTrimmer(maxSizeInBytes: ContentSize).Trim(Now + DeletionDelay);

        Assert.AreEqual(1, later.ContentDeleted, "Only B1, not put again, is deleted.");
        Assert.IsTrue(ContentExists("A1"));
        Assert.IsFalse(ContentExists("B1"));
    }

    private FileSystemCacheTrimmer CreateTrimmer(long maxSizeInBytes)
        => new(_contentDirectory, _fingerprintDirectory, _trimDirectory, maxSizeInBytes, CheckInterval, DeletionDelay);

    private void AddEntry(string name, DateTime lastUsedUtc, params string[] contentNames)
        => AddEntryWithSelector(name, lastUsedUtc, "SELECTOR_00", contentNames);

    private void AddEntryWithPathSet(string name, DateTime lastUsedUtc, string pathSetName, params string[] contentNames)
        => AddEntryWithSelector(name, lastUsedUtc, $"{pathSetName}_00", contentNames);

    private void AddEntryWithSelector(string name, DateTime lastUsedUtc, string selector, string[] contentNames)
    {
        foreach (string contentName in contentNames)
        {
            if (!ContentExists(contentName))
            {
                AddContent(contentName);
            }
        }

        string directory = Path.Combine(_fingerprintDirectory, "WEAK");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllLines(path, [selector, .. contentNames]);
        File.SetLastWriteTimeUtc(path, lastUsedUtc);
    }

    private void AddContent(string name)
    {
        string path = ContentPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[ContentSize]);

        // Put long before the trims, which run at a fixed time.
        File.SetLastWriteTimeUtc(path, Now.AddDays(-10));
    }

    private string ContentPath(string name) => Path.Combine(_contentDirectory, name.Substring(0, 1), name);

    private bool EntryExists(string name) => File.Exists(Path.Combine(_fingerprintDirectory, "WEAK", name));

    private bool ContentExists(string name) => File.Exists(Path.Combine(_contentDirectory, name.Substring(0, 1), name));
}
