// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.GenAPI;

internal static class Resources
{
    public const string AddMemberThrowsException =
        "Adding member '{0}' to the named type '{1}' failed with exception '{2}'.";
    public const string ResolveTypeForwardFailed =
        "Could not resolve type '{0}' in containing assembly '{1}' via type forward. Make sure that the assembly is provided as a reference and contains the type.";
    public const string SyntaxNodeNotFound = "Syntax node not found.";
}
