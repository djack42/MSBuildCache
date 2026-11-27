// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.MSBuildCache;

/// <summary>
/// Which files the plugin writes to <see cref="PluginSettings.LogDirectory"/>.
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// Every log file, including the raw file accesses and the parser information of each project.
    /// </summary>
    Full,

    /// <summary>
    /// Only the files needed to explain a cache miss: for each project built, its fingerprints (one entry per dependency
    /// rather than per file of its outputs), path sets, observed inputs and outputs and build result; for each cache hit,
    /// the fingerprints of the entry it used, in hits.json; node information and the cache client log.
    /// </summary>
    Minimal,

    /// <summary>
    /// No log files. Warnings and errors are still reported to MSBuild.
    /// </summary>
    None,
}
