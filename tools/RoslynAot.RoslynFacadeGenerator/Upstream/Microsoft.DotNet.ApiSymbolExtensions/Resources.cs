// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.ApiSymbolExtensions;

internal static class Resources
{
    public const string AssemblyLoaded = "Assembly '{0}' loaded.";
    public const string AssemblyReferenceLoaded =
        "Assembly '{0}' referenced by '{1}' loaded.";
    public const string CouldNotResolveReference =
        "Could not resolve reference '{0}' directly or transitively referenced by {1} in any of the provided search directories.";
    public const string LoadingAssemblies = "Loading assemblies '{0}'.";
    public const string LoadingAssembliesFromArchive =
        "Loading assemblies '{0}' ({1}).";
    public const string LoadingAssembly = "Loading assembly '{0}'.";
    public const string LoadingAssemblyFromStream =
        "Loading assembly '{0}' from stream.";
    public const string MatchingAssemblyNotFound =
        "Could not find matching assembly: '{0}' in any of the search directories.";
    public const string ProvidedPathToLoadBinariesFromNotFound =
        "Could not find the provided path '{0}' to load binaries from.";
    public const string ProvidedStreamDoesNotHaveMetadata =
        "Provided stream for assembly '{0}' doesn't have any metadata to read.";
    public const string RootAssemblyDisplayString = "'{0}'";
    public const string RootAssemblyFromPackageDisplayString = "'{0}' ({1})";
    public const string ShouldNotBeNullAndContainAtLeastOneElement =
        "Should not be null and contain at least one element.";
    public const string ShouldProvideValidAssemblyName =
        "Should provide a valid assembly name.";
    public const string StreamPositionGreaterThanLength =
        "Stream position is greater than its length, so there are no contents available to read.";
}
