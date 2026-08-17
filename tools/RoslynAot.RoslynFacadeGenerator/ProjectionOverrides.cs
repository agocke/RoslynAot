namespace RoslynAot.RoslynFacadeGenerator;

/// <summary>
/// A deliberate deviation from what the projection rules would otherwise decide
/// about one member, keyed by its canonical id.
/// </summary>
/// <param name="Reason">
/// Why the deviation exists. Mandatory: an override without a reason is how a
/// workaround becomes permanent.
/// </param>
/// <param name="Strategy">
/// Replaces the classified strategy. <see cref="ProjectionStrategy.Unsupported"/>
/// withdraws the member from the ABI.
/// </param>
/// <param name="ReturnIsNullable">
/// Replaces the nullability the return type's annotations imply.
/// </param>
/// <param name="LocalStatements">
/// An analyzer-side body for a member the analyzer must answer itself rather
/// than remote to the compiler.
/// </param>
/// <param name="RemoteFallback">
/// Whether <paramref name="LocalStatements"/> is the whole body, or a local
/// fast path that falls through to the ordinary remote body.
/// </param>
internal sealed record ProjectionOverride(
    string Reason,
    ProjectionStrategy? Strategy = null,
    bool? ReturnIsNullable = null,
    IReadOnlyList<string>? LocalStatements = null,
    bool RemoteFallback = false);

/// <summary>
/// The overrides, keyed by canonical id. Every entry here was once a match on a
/// member's name inside the emitter, where it was invisible to the model, could
/// not be reported, and silently applied to every overload that shared the name.
/// </summary>
internal static class ProjectionOverrides
{
    private static readonly IReadOnlyDictionary<string, ProjectionOverride>
        s_overrides = new Dictionary<string, ProjectionOverride>(
            StringComparer.Ordinal)
    {
        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.Create(Microsoft.CodeAnalysis.DiagnosticDescriptor,Microsoft.CodeAnalysis.Location,System.Collections.Generic.IEnumerable{Microsoft.CodeAnalysis.Location},System.Collections.Immutable.ImmutableDictionary{System.String,System.String},System.Object[])~Microsoft.CodeAnalysis.Diagnostic"] = new(
            "Diagnostic is dual. An analyzer builds diagnostics from its " +
            "own descriptors and message arguments, none of which the " +
            "compiler owns, so Create constructs an analyzer-local " +
            "Diagnostic that ReportDiagnostic later transports field by " +
            "field.",
            LocalStatements:
            [
                "return __RoslynAotCreateLocal(descriptor, location, additionalLocations, properties, messageArgs);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.Create(Microsoft.CodeAnalysis.DiagnosticDescriptor,Microsoft.CodeAnalysis.Location,System.Object[])~Microsoft.CodeAnalysis.Diagnostic"] = new(
            "Diagnostic is dual. An analyzer builds diagnostics from its " +
            "own descriptors and message arguments, none of which the " +
            "compiler owns, so Create constructs an analyzer-local " +
            "Diagnostic that ReportDiagnostic later transports field by " +
            "field.",
            LocalStatements:
            [
                "return __RoslynAotCreateLocal(descriptor, location, messageArgs);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.#ctor(System.String,Microsoft.CodeAnalysis.LocalizableString,Microsoft.CodeAnalysis.LocalizableString,System.String,Microsoft.CodeAnalysis.DiagnosticSeverity,System.Boolean,Microsoft.CodeAnalysis.LocalizableString,System.String,System.String[])"] = new(
            "DiagnosticDescriptor is analyzer-local. Analyzers construct " +
            "descriptors in static initializers, before any compiler " +
            "object exists, so there is nothing to remote to and the state " +
            "lives on the analyzer-side instance.",
            LocalStatements:
            [
                "__RoslynAotInitializeLocal(id, title, messageFormat, category, defaultSeverity, isEnabledByDefault, description, helpLinkUri, customTags);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.#ctor(System.String,System.String,System.String,System.String,Microsoft.CodeAnalysis.DiagnosticSeverity,System.Boolean,System.String,System.String,System.String[])"] = new(
            "DiagnosticDescriptor is analyzer-local. Analyzers construct " +
            "descriptors in static initializers, before any compiler " +
            "object exists, so there is nothing to remote to and the state " +
            "lives on the analyzer-side instance.",
            LocalStatements:
            [
                "__RoslynAotInitializeLocal(id, title, messageFormat, category, defaultSeverity, isEnabledByDefault, description, helpLinkUri, customTags);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.Equals(Microsoft.CodeAnalysis.DiagnosticDescriptor)~System.Boolean"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return global::System.Object.ReferenceEquals(this, other);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.GetHashCode~System.Int32"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_Category~System.String"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalCategory;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_CustomTags~System.Collections.Generic.IEnumerable{System.String}"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalCustomTags;",
                "throw new global::System.PlatformNotSupportedException(\"This Roslyn API is not implemented by RoslynAot.\");",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_DefaultSeverity~Microsoft.CodeAnalysis.DiagnosticSeverity"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalDefaultSeverity;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_Description~Microsoft.CodeAnalysis.LocalizableString"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalDescriptionValue;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_HelpLinkUri~System.String"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalHelpLinkUri;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_Id~System.String"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalId;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_IsEnabledByDefault~System.Boolean"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalIsEnabledByDefault;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_MessageFormat~Microsoft.CodeAnalysis.LocalizableString"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalMessageFormatValue;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.DiagnosticDescriptor.get_Title~Microsoft.CodeAnalysis.LocalizableString"] = new(
            "Reads the analyzer-local descriptor state when the instance " +
            "was constructed analyzer-side, and remotes when it came from " +
            "the compiler.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalTitleValue;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.AnalysisContext.RegisterOperationAction(System.Action{Microsoft.CodeAnalysis.Diagnostics.OperationAnalysisContext},Microsoft.CodeAnalysis.OperationKind[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(operationKinds);",
                "RegisterOperationAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(operationKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.AnalysisContext.RegisterSymbolAction(System.Action{Microsoft.CodeAnalysis.Diagnostics.SymbolAnalysisContext},Microsoft.CodeAnalysis.SymbolKind[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(symbolKinds);",
                "RegisterSymbolAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(symbolKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.AnalysisContext.RegisterSyntaxNodeAction``1(System.Action{Microsoft.CodeAnalysis.Diagnostics.SyntaxNodeAnalysisContext},``0[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(syntaxKinds);",
                "RegisterSyntaxNodeAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(syntaxKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.CompilationAnalysisContext.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic)"] = new(
            "An analyzer-local Diagnostic has no handle, so it is " +
            "transported field by field through the diagnostic sink; a " +
            "compiler-owned one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotTryReportLocal(diagnostic)) return;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.CompilationStartAnalysisContext.RegisterOperationAction(System.Action{Microsoft.CodeAnalysis.Diagnostics.OperationAnalysisContext},Microsoft.CodeAnalysis.OperationKind[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(operationKinds);",
                "RegisterOperationAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(operationKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.CompilationStartAnalysisContext.RegisterSymbolAction(System.Action{Microsoft.CodeAnalysis.Diagnostics.SymbolAnalysisContext},Microsoft.CodeAnalysis.SymbolKind[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(symbolKinds);",
                "RegisterSymbolAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(symbolKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.CompilationStartAnalysisContext.RegisterSyntaxNodeAction``1(System.Action{Microsoft.CodeAnalysis.Diagnostics.SyntaxNodeAnalysisContext},``0[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(syntaxKinds);",
                "RegisterSyntaxNodeAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(syntaxKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.OperationAnalysisContext.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic)"] = new(
            "An analyzer-local Diagnostic has no handle, so it is " +
            "transported field by field through the diagnostic sink; a " +
            "compiler-owned one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotTryReportLocal(diagnostic)) return;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.OperationBlockAnalysisContext.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic)"] = new(
            "An analyzer-local Diagnostic has no handle, so it is " +
            "transported field by field through the diagnostic sink; a " +
            "compiler-owned one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotTryReportLocal(diagnostic)) return;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.OperationBlockStartAnalysisContext.RegisterOperationAction(System.Action{Microsoft.CodeAnalysis.Diagnostics.OperationAnalysisContext},Microsoft.CodeAnalysis.OperationKind[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(operationKinds);",
                "RegisterOperationAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(operationKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.SymbolAnalysisContext.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic)"] = new(
            "An analyzer-local Diagnostic has no handle, so it is " +
            "transported field by field through the diagnostic sink; a " +
            "compiler-owned one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotTryReportLocal(diagnostic)) return;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.SymbolStartAnalysisContext.RegisterOperationAction(System.Action{Microsoft.CodeAnalysis.Diagnostics.OperationAnalysisContext},Microsoft.CodeAnalysis.OperationKind[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(operationKinds);",
                "RegisterOperationAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(operationKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.SymbolStartAnalysisContext.RegisterSyntaxNodeAction``1(System.Action{Microsoft.CodeAnalysis.Diagnostics.SyntaxNodeAnalysisContext},``0[])"] = new(
            "The params-array overload forwards to the ImmutableArray " +
            "overload analyzer-side. Marshalling the array would cost a " +
            "crossing to rebuild what the other overload already accepts.",
            LocalStatements:
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(syntaxKinds);",
                "RegisterSyntaxNodeAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(syntaxKinds));",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.SyntaxNodeAnalysisContext.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic)"] = new(
            "An analyzer-local Diagnostic has no handle, so it is " +
            "transported field by field through the diagnostic sink; a " +
            "compiler-owned one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotTryReportLocal(diagnostic)) return;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.SyntaxNodeAnalysisContext.get_Node~Microsoft.CodeAnalysis.SyntaxNode"] = new(
            "The node arrives with the callback and is cached " +
            "analyzer-side. Remoting the getter would mint a second handle " +
            "for an object the analyzer is already holding.",
            LocalStatements:
            [
                "return __RoslynAotGetLocalNode();",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.SyntaxTreeAnalysisContext.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic)"] = new(
            "An analyzer-local Diagnostic has no handle, so it is " +
            "transported field by field through the diagnostic sink; a " +
            "compiler-owned one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotTryReportLocal(diagnostic)) return;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.ISymbol.Equals(Microsoft.CodeAnalysis.ISymbol,Microsoft.CodeAnalysis.SymbolEqualityComparer)~System.Boolean"] = new(
            "Delegates to the comparer, which owns the remote call. " +
            "Projecting this overload as well would give one comparison " +
            "two paths across the ABI.",
            LocalStatements:
            [
                "return equalityComparer.Equals(this, other);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableResourceString.#ctor(System.String,System.Resources.ResourceManager,System.Type)"] = new(
            "LocalizableResourceString is analyzer-local. Its " +
            "ResourceManager and resource source type live in the analyzer " +
            "module and cannot be reached from the compiler.",
            LocalStatements:
            [
                "__RoslynAotInitializeLocal(nameOfLocalizableResource, resourceManager, resourceSource, global::System.Array.Empty<string>());",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableResourceString.#ctor(System.String,System.Resources.ResourceManager,System.Type,System.String[])"] = new(
            "LocalizableResourceString is analyzer-local. Its " +
            "ResourceManager and resource source type live in the analyzer " +
            "module and cannot be reached from the compiler.",
            LocalStatements:
            [
                "__RoslynAotInitializeLocal(nameOfLocalizableResource, resourceManager, resourceSource, formatArguments);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableResourceString.AreEqual(System.Object)~System.Boolean"] = new(
            "LocalizableResourceString is analyzer-local. Its " +
            "ResourceManager and resource source type live in the analyzer " +
            "module and cannot be reached from the compiler.",
            LocalStatements:
            [
                "return __RoslynAotAreEqualLocal(other);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableResourceString.GetHash~System.Int32"] = new(
            "LocalizableResourceString is analyzer-local. Its " +
            "ResourceManager and resource source type live in the analyzer " +
            "module and cannot be reached from the compiler.",
            LocalStatements:
            [
                "return __RoslynAotGetHashLocal();",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableResourceString.GetText(System.IFormatProvider)~System.String"] = new(
            "LocalizableResourceString is analyzer-local. Its " +
            "ResourceManager and resource source type live in the analyzer " +
            "module and cannot be reached from the compiler.",
            LocalStatements:
            [
                "return __RoslynAotGetTextLocal(formatProvider);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.Equals(Microsoft.CodeAnalysis.LocalizableString)~System.Boolean"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return other is not null && __RoslynAotAreEqual(other);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.Equals(System.Object)~System.Boolean"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return other is not null && __RoslynAotAreEqual(other);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.GetHashCode~System.Int32"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotGetHash();",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.ToString(System.IFormatProvider)~System.String"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotGetText(formatProvider);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.ToString~System.String"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotGetText(null);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.add_OnException(System.EventHandler{System.Exception})"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "__RoslynAotAddExceptionHandler(value);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.op_Explicit(Microsoft.CodeAnalysis.LocalizableString)~System.String"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "return localizableResource?.__RoslynAotGetText(null);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.op_Implicit(System.String)~Microsoft.CodeAnalysis.LocalizableString"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "return __RoslynAotCreateFixed(fixedResource);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.LocalizableString.remove_OnException(System.EventHandler{System.Exception})"] = new(
            "LocalizableString is dual. A fixed or resource string " +
            "constructed analyzer-side answers locally; a compiler-owned " +
            "one remotes.",
            LocalStatements:
            [
                "__RoslynAotRemoveExceptionHandler(value);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.Create(Microsoft.CodeAnalysis.SyntaxTree,Microsoft.CodeAnalysis.Text.TextSpan)~Microsoft.CodeAnalysis.Location"] = new(
            "Builds an analyzer-local Location over a compiler-owned tree " +
            "and span, so that constructing a location for a diagnostic " +
            "costs no crossing.",
            LocalStatements:
            [
                "return __RoslynAotCreateLocal(syntaxTree, textSpan);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.get_IsInMetadata~System.Boolean"] = new(
            "Location is dual. A locally created location answers from its " +
            "own kind rather than remoting to a handle it does not have.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return Kind == global::Microsoft.CodeAnalysis.LocationKind.MetadataFile;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.get_IsInSource~System.Boolean"] = new(
            "Location is dual. A locally created location answers from its " +
            "own kind rather than remoting to a handle it does not have.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return Kind == global::Microsoft.CodeAnalysis.LocationKind.SourceFile;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.get_MetadataModule~Microsoft.CodeAnalysis.IModuleSymbol"] = new(
            "Location is dual. A locally created location answers from its " +
            "own kind rather than remoting to a handle it does not have.",
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return null;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.OperationAnalysisContext.GetControlFlowGraph~Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowGraph"] = new(
            ControlFlowGraphReason,
            Strategy: ProjectionStrategy.Unsupported),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.OperationBlockAnalysisContext.GetControlFlowGraph(Microsoft.CodeAnalysis.IOperation)~Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowGraph"] = new(
            ControlFlowGraphReason,
            Strategy: ProjectionStrategy.Unsupported),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostics.OperationBlockStartAnalysisContext.GetControlFlowGraph(Microsoft.CodeAnalysis.IOperation)~Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowGraph"] = new(
            ControlFlowGraphReason,
            Strategy: ProjectionStrategy.Unsupported),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.get_Kind~Microsoft.CodeAnalysis.LocationKind"] = new(
            LocationDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalKind;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.get_SourceSpan~Microsoft.CodeAnalysis.Text.TextSpan"] = new(
            LocationDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalSourceSpan;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.get_SourceTree~Microsoft.CodeAnalysis.SyntaxTree"] = new(
            LocationDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalSourceTree;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.get_None~Microsoft.CodeAnalysis.Location"] = new(
            "Location.None must be the same object the analyzer's own " +
            "diagnostics default to. Fetching the compiler's instead gave " +
            "two 'no location' values that no comparison could reconcile.",
            LocalStatements:
            [
                "return __RoslynAotCreateNone();",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.Equals(System.Object)~System.Boolean"] = new(
            LocationIdentityReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return global::System.Object.ReferenceEquals(this, obj);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Location.GetHashCode~System.Int32"] = new(
            LocationIdentityReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_Id~System.String"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalDescriptor.__RoslynAotLocalId;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_Descriptor~Microsoft.CodeAnalysis.DiagnosticDescriptor"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalDescriptor;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_Severity~Microsoft.CodeAnalysis.DiagnosticSeverity"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalDescriptor.__RoslynAotLocalDefaultSeverity;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_DefaultSeverity~Microsoft.CodeAnalysis.DiagnosticSeverity"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalDescriptor.__RoslynAotLocalDefaultSeverity;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_Location~Microsoft.CodeAnalysis.Location"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalLocation;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_AdditionalLocations~System.Collections.Generic.IReadOnlyList{Microsoft.CodeAnalysis.Location}"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalAdditionalLocations;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_Properties~System.Collections.Immutable.ImmutableDictionary{System.String,System.String}"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalProperties;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_IsSuppressed~System.Boolean"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return false;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.get_WarningLevel~System.Int32"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotLocalDescriptor.__RoslynAotLocalDefaultSeverity == global::Microsoft.CodeAnalysis.DiagnosticSeverity.Error ? 0 : 1;",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.GetMessage(System.IFormatProvider)~System.String"] = new(
            DiagnosticDualReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return __RoslynAotGetLocalMessage(formatProvider);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.Equals(Microsoft.CodeAnalysis.Diagnostic)~System.Boolean"] = new(
            DiagnosticIdentityReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal || obj is not null && obj.__RoslynAotIsLocal) return global::System.Object.ReferenceEquals(this, obj);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.Diagnostic.GetHashCode~System.Int32"] = new(
            DiagnosticIdentityReason,
            LocalStatements:
            [
                "if (__RoslynAotIsLocal) return global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);",
            ],
            RemoteFallback: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.SymbolEqualityComparer.Equals(Microsoft.CodeAnalysis.ISymbol,Microsoft.CodeAnalysis.ISymbol)~System.Boolean"] = new(
            "The comparer itself is analyzer-local; only its kind crosses, " +
            "and the comparison runs compiler-side against the two symbol " +
            "handles.",
            LocalStatements:
            [
                "if (x is null) return y is null;",
                "if (y is null) return false;",
                "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl = x.__RoslynAotGetControlVtbl();",
                "int status = controlVtbl.SymbolEqualityComparerEquals(__RoslynAotKind, x.__RoslynAotGetHandle(controlVtbl), y.__RoslynAotGetHandle(controlVtbl), out int result);",
                "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime.ThrowIfFailed(controlVtbl, status);",
                "return result != 0;",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.SymbolEqualityComparer.GetHashCode(Microsoft.CodeAnalysis.ISymbol)~System.Int32"] = new(
            "The comparer itself is analyzer-local; only its kind crosses, " +
            "and the comparison runs compiler-side against the two symbol " +
            "handles.",
            LocalStatements:
            [
                "if (obj is null) return 0;",
                "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl = obj.__RoslynAotGetControlVtbl();",
                "int status = controlVtbl.SymbolEqualityComparerGetHashCode(__RoslynAotKind, obj.__RoslynAotGetHandle(controlVtbl), out int result);",
                "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime.ThrowIfFailed(controlVtbl, status);",
                "return result;",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.SyntaxNode.GetLocation~Microsoft.CodeAnalysis.Location"] = new(
            "Returns an analyzer-local Location built from the node's " +
            "span. Every reported diagnostic calls this, so a crossing " +
            "here is a crossing per diagnostic.",
            LocalStatements:
            [
                "return global::Microsoft.CodeAnalysis.Location.__RoslynAotCreateLocal(Span);",
            ]),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.ISymbol.get_ContainingSymbol~Microsoft.CodeAnalysis.ISymbol"] = new(
            "Declared non-nullable, but a compilation's global namespace has " +
            "no containing symbol, so the annotation would make a real null " +
            "cross the ABI as a non-null handle.",
            ReturnIsNullable: true),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.INamespaceOrTypeSymbol.GetTypeMembers~System.Collections.Immutable.ImmutableArray{Microsoft.CodeAnalysis.INamedTypeSymbol}"] = new(
            GetTypeMembersReason,
            Strategy: ProjectionStrategy.Unsupported),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.INamespaceOrTypeSymbol.GetTypeMembers(System.String)~System.Collections.Immutable.ImmutableArray{Microsoft.CodeAnalysis.INamedTypeSymbol}"] = new(
            GetTypeMembersReason,
            Strategy: ProjectionStrategy.Unsupported),

        ["[Microsoft.CodeAnalysis]M:Microsoft.CodeAnalysis.INamespaceOrTypeSymbol.GetTypeMembers(System.String,System.Int32)~System.Collections.Immutable.ImmutableArray{Microsoft.CodeAnalysis.INamedTypeSymbol}"] = new(
            GetTypeMembersReason,
            Strategy: ProjectionStrategy.Unsupported),
    };

    private const string ControlFlowGraphReason =
        "Handing an analyzer a control flow graph leads into the analyzer " +
        "utilities' dataflow analysis, which visits operations through " +
        "IOperation.Accept<TArgument, TResult>. NativeAOT cannot resolve a " +
        "generic virtual method through an IDynamicInterfaceCastable proxy: " +
        "the type loader fails fast and terminates the compiler process " +
        "rather than raising AD0001, taking every other analyzer's " +
        "diagnostics with it. Withdrawn so the failure is a reported " +
        "unsupported member instead of a dead compiler. Restore it when " +
        "Step 8 gives generic virtual methods a dispatch path.";

    private const string LocationDualReason =
        "Location is dual: one type, two states. A location the analyzer " +
        "built reads the state it was built from; one that came from the " +
        "compiler dispatches on its handle. The member decides by asking the " +
        "discriminator, never by asking what type the instance is.";

    private const string LocationIdentityReason =
        "A local location has no handle for the compiler to compare, so " +
        "identity is the only equality it can offer. Value equality across " +
        "the boundary waits for the identity step.";

    private const string DiagnosticDualReason =
        "Diagnostic is dual: an analyzer constructs them and the compiler " +
        "hands them back, and the two are indistinguishable to the analyzer. " +
        "The locally built state answers without a crossing.";

    private const string DiagnosticIdentityReason =
        "A local diagnostic has no handle for the compiler to compare, and a " +
        "mixed comparison has nothing in common to compare on, so both fall " +
        "back to identity rather than dispatching into a vtbl one side lacks.";

    private const string GetTypeMembersReason =
        "The three overloads collide in the interface's projected vtbl, and " +
        "the arity overload would silently answer for the other two. Withdrawn " +
        "until overload identity reaches the vtbl slot rather than the name.";

    /// <summary>
    /// Analyzer-side initializers for static fields the compiler would
    /// otherwise have to hand over as objects. The value is the expression, so
    /// the singleton the analyzer sees is the one it constructed.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string>
        s_fieldInitializers = new Dictionary<string, string>(
            StringComparer.Ordinal)
    {
        ["[Microsoft.CodeAnalysis]F:Microsoft.CodeAnalysis.SymbolEqualityComparer.Default"] =
            "SymbolEqualityComparer.__RoslynAotCreateLocal(" +
            "global::RoslynAot.Abi.RoslynWellKnownObject." +
            "SymbolEqualityComparerDefault)",

        ["[Microsoft.CodeAnalysis]F:Microsoft.CodeAnalysis.SymbolEqualityComparer.IncludeNullability"] =
            "SymbolEqualityComparer.__RoslynAotCreateLocal(" +
            "global::RoslynAot.Abi.RoslynWellKnownObject." +
            "SymbolEqualityComparerIncludeNullability)",
    };

    public static IEnumerable<string> Ids => s_overrides.Keys;

    public static IEnumerable<string> FieldInitializerIds =>
        s_fieldInitializers.Keys;

    public static bool TryGet(
        string canonicalId,
        out ProjectionOverride projectionOverride) =>
        s_overrides.TryGetValue(canonicalId, out projectionOverride!);

    public static bool TryGetFieldInitializer(
        string canonicalId,
        out string initializer) =>
        s_fieldInitializers.TryGetValue(canonicalId, out initializer!);
}
