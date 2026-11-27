// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Fingerprinting;

[TestClass]
public class FingerprintLogEntriesTests
{
    [TestMethod]
    public void DependencyOutputsAreCollapsedToOneEntryPerDependency()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        FingerprintEntry setting = new(new byte[] { 1 }, "CacheUniverse: A");
        FingerprintEntry a1 = new(new byte[] { 2 }, "Dependency Output: A - a1.dll", "A");
        FingerprintEntry a2 = new(new byte[] { 3 }, "Dependency Output: A - a2.dll", "A");
        FingerprintEntry b1 = new(new byte[] { 4 }, "Dependency Output: B - b1.dll", "B");

        IReadOnlyList<FingerprintEntry> collapsed = FingerprintLogEntries.CollapseDependencyOutputs(new[] { setting, a1, a2, b1 }, hasher);

        CollectionAssert.AreEqual(
            new[] { "CacheUniverse: A", "Dependency Outputs: A (2 files)", "Dependency Outputs: B (1 files)" },
            collapsed.Select(entry => entry.Description).ToArray());
        CollectionAssert.AreEqual(setting.Hash, collapsed[0].Hash);
        CollectionAssert.AreEqual(hasher.CombineHashes(new[] { a1.Hash, a2.Hash }), collapsed[1].Hash);
        CollectionAssert.AreEqual(hasher.CombineHashes(new[] { b1.Hash }), collapsed[2].Hash);
    }

    [TestMethod]
    public void CollapsedHashChangesWhenAnyOutputOfTheDependencyChanges()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        FingerprintEntry[] before = { new(new byte[] { 2 }, "Dependency Output: A - a1.dll", "A"), new(new byte[] { 3 }, "Dependency Output: A - a2.dll", "A") };
        FingerprintEntry[] after = { before[0], new(new byte[] { 9 }, "Dependency Output: A - a2.dll", "A") };

        byte[]? hashBefore = FingerprintLogEntries.CollapseDependencyOutputs(before, hasher)[0].Hash;
        byte[]? hashAfter = FingerprintLogEntries.CollapseDependencyOutputs(after, hasher)[0].Hash;

        CollectionAssert.AreNotEqual(hashBefore, hashAfter);
    }
}
