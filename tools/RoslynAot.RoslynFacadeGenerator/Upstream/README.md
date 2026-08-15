# Upstream sources

The projects under this directory are forked from `dotnet/sdk` commit
`e6bc966cc3d1348265b0831c6daca23267169d8f`, the `v10.0.100` tag.

Imported directories:

- `src/Compatibility/GenAPI/Microsoft.DotNet.GenAPI`
- `src/Compatibility/Microsoft.DotNet.ApiSymbolExtensions`

The original source is licensed by the .NET Foundation under the MIT license.
The license is reproduced in `LICENSE.TXT`, and source files retain their
upstream license headers. Local changes adapt the projects to this repository
and replace reference-assembly method bodies with explicit
unsupported-operation failures suitable for executable facades.
