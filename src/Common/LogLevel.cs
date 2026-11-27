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
    /// Only the files needed to explain a cache miss: fingerprints, path sets, observed inputs and outputs,
    /// node information, build results and the cache client log.
    /// </summary>
    Minimal,

    /// <summary>
    /// No log files. Warnings and errors are still reported to MSBuild.
    /// </summary>
    None,
}
