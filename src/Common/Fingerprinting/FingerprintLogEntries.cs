// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Hashing;

namespace Microsoft.MSBuildCache.Fingerprinting;

/// <summary>
/// Shortens the fingerprint entries written to the logs, without changing the fingerprint.
/// </summary>
internal static class FingerprintLogEntries
{
    /// <summary>
    /// Replaces the entries of each dependency's outputs, one per output file, with a single entry per dependency
    /// whose hash combines them. A fingerprint diff still shows which dependency changed, and that dependency's
    /// build result lists the hash of each of its files.
    /// </summary>
    public static IReadOnlyList<FingerprintEntry> CollapseDependencyOutputs(IReadOnlyList<FingerprintEntry> entries, IContentHasher contentHasher)
    {
        List<FingerprintEntry> collapsed = new(entries.Count);
        int i = 0;
        while (i < entries.Count)
        {
            string? dependencyId = entries[i].DependencyId;
            if (dependencyId is null)
            {
                collapsed.Add(entries[i]);
                i++;
                continue;
            }

            int start = i;
            while (i < entries.Count && entries[i].DependencyId == dependencyId)
            {
                i++;
            }

            int count = i - start;
            byte[]? hash = contentHasher.CombineHashes(entries.Skip(start).Take(count).Select(entry => entry.Hash));
            collapsed.Add(new FingerprintEntry(hash, $"Dependency Outputs: {dependencyId} ({count} files)", dependencyId));
        }

        return collapsed;
    }
}
