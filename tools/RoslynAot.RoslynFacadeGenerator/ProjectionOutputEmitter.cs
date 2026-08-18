using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.DotNet.GenAPI;

namespace RoslynAot.RoslynFacadeGenerator;

internal static class ProjectionOutputEmitter
{
    public static void WriteNonFacadeOutputs(
        ProjectionModel model,
        string outputRoot)
    {
        string abiDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "Abi"));
        string compilerDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "Compiler"));
        string analyzerRuntimeDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "AnalyzerRuntime"));
        string manifestDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "Manifest"));

        WriteGeneratedFile(
            Path.Combine(abiDirectory, "RoslynControlVtbl.g.cs"),
            EmitAbiMetadata(model));
        IReadOnlyDictionary<string, int> callOrdinals =
            GetCallCounterOrdinals(model);
        foreach (VtblProjection vtbl in model.Vtbls)
        {
            WriteGeneratedFile(
                Path.Combine(
                    abiDirectory,
                    $"{vtbl.Name}.g.cs"),
                EmitAbiVtbl(vtbl));
            WriteGeneratedFile(
                Path.Combine(
                    compilerDirectory,
                    $"{GetDispatcherClassName(vtbl)}.g.cs"),
                EmitCompilerDispatcher(vtbl, callOrdinals));
        }
        WriteGeneratedFile(
            Path.Combine(
                compilerDirectory,
                "RoslynCallCounters.g.cs"),
            EmitCallCounters(model, callOrdinals));
        WriteGeneratedFile(
            Path.Combine(
                compilerDirectory,
                "RoslynDispatcherRegistry.g.cs"),
            EmitCompilerDispatcherRegistry(model));
        WriteGeneratedFile(
            Path.Combine(
                analyzerRuntimeDirectory,
                "RoslynProxyFactory.g.cs"),
            EmitAnalyzerRuntimeProxyFactory(model));
        WriteManifest(
            Path.Combine(manifestDirectory, "RoslynProjection.json"),
            model);
        File.WriteAllText(
            Path.Combine(
                manifestDirectory,
                "ProjectionInventory.txt"),
            EmitInventory(model).ReplaceLineEndings("\n"));
    }

    public static void WriteFacadeRuntime(
        ProjectionModel model,
        string facadesRoot)
    {
        string runtimeDirectory = Path.Combine(facadesRoot, "Runtime");
        Directory.CreateDirectory(runtimeDirectory);
        WriteGeneratedFile(
            Path.Combine(runtimeDirectory, "RoslynFacadeRuntime.g.cs"),
            EmitFacadeRuntime());
        WriteGeneratedFile(
            Path.Combine(runtimeDirectory, "RoslynVtblFactory.g.cs"),
            EmitVtblFactory(model));

        string coreDirectory = Path.Combine(
            facadesRoot,
            "Microsoft.CodeAnalysis");
        WriteGeneratedFile(
            Path.Combine(
                coreDirectory,
                "RoslynAotProjectionFriends.g.cs"),
            """
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "roslyn-aot-roslyn-projection-client, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "RoslynAot.AnalyzerRuntime, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            """);
        WriteGeneratedFile(
            Path.Combine(
                coreDirectory,
                "RoslynAotTypeMap.g.cs"),
            EmitFacadeTypeMap(model, "Microsoft.CodeAnalysis"));
        WriteGeneratedFile(
            Path.Combine(
                facadesRoot,
                "Microsoft.CodeAnalysis.CSharp",
                "RoslynAotProjectionFriends.g.cs"),
            """
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "roslyn-aot-roslyn-projection-client, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "RoslynAot.AnalyzerRuntime, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            """);
        WriteGeneratedFile(
            Path.Combine(
                facadesRoot,
                "Microsoft.CodeAnalysis.CSharp",
                "RoslynAotTypeMap.g.cs"),
            EmitFacadeTypeMap(model, "Microsoft.CodeAnalysis.CSharp"));
    }

    private static string EmitFacadeTypeMap(
        ProjectionModel model,
        string assemblyName)
    {
        var builder = new StringBuilder();
        foreach (VtblProjection vtbl in GetDynamicInterfaceVtbls(model)
            .Where(
                vtbl =>
                    vtbl.FacadeType.ContainingAssembly.Name == assemblyName))
        {
            string typeName = vtbl.FacadeType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            builder.AppendLine(
                "[assembly: global::System.Runtime.InteropServices." +
                "TypeMapAssociation<" +
                "global::RoslynAot.RoslynFacade.RoslynProxyTypeMap>(");
            builder.AppendLine($"    typeof({typeName}),");
            builder.AppendLine(
                $"    typeof({typeName}.__RoslynAotImplementation))]");
        }

        return builder.ToString();
    }

    private static string EmitAbiMetadata(ProjectionModel model) =>
        $$"""
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;

        namespace RoslynAot.Abi;

        public static unsafe class RoslynAbi
        {
            public const int Success = 0;
            public const int InvalidArgument = unchecked((int)0x80070057);
            public const int ObjectDisposed = unchecked((int)0x80131622);
            public const int Unsupported = unchecked((int)0x80131515);
            public const int Failure = unchecked((int)0x80004005);

            public const string ManifestIdentity = "{{model.Identity}}";
            public const long ManifestIdentityLow = {{model.IdentityLow}}L;
            public const long ManifestIdentityHigh = {{model.IdentityHigh}}L;

            public static uint Release(nint instance)
            {
                if (instance == 0)
                {
                    return 0;
                }

                nint* vtable = *(nint**)instance;
                var release = (delegate* unmanaged<nint, uint>)vtable[2];
                return release(instance);
            }
        }

        public enum RoslynRemoteErrorKind
        {
            None,
            Argument,
            ObjectDisposed,
            Unsupported,
            OperationCanceled,
            Failure,
        }

        public enum RoslynWellKnownObject
        {
            SymbolEqualityComparerDefault,
            SymbolEqualityComparerIncludeNullability,
        }

        [GeneratedComInterface]
        [Guid("{{model.ControlVtblId:D}}")]
        public partial interface IRoslynControlVtbl
        {
            [PreserveSig]
            int GetManifestIdentity(
                out long identityLow,
                out long identityHigh);

            [PreserveSig]
            int GetVtbl(
                long vtblIdLow,
                long vtblIdHigh,
                out nint vtbl);

            [PreserveSig]
            int CopyLastErrorUtf16(
                nint buffer,
                int bufferLength,
                out int requiredLength,
                out RoslynRemoteErrorKind errorKind);

            [PreserveSig]
            int CreateSourceTextUtf16(
                nint utf16Text,
                int utf16Length,
                int checksumAlgorithm,
                out long result);

            [PreserveSig]
            int IsObjectType(
                long handle,
                long vtblIdLow,
                long vtblIdHigh,
                out int isType);

            [PreserveSig]
            int CreateObjectCollection(
                nint handles,
                int count,
                out long result);

            [PreserveSig]
            int GetCollectionCount(
                long handle,
                out int count);

            [PreserveSig]
            int GetObjectCollectionItem(
                long handle,
                int index,
                out long result);

            [PreserveSig]
            int CopyStringCollectionItemUtf16(
                long handle,
                int index,
                nint buffer,
                int bufferLength,
                out int requiredLength);

            // Membership must be answered by the collection that owns the
            // semantics. Copying the contents to a string[] and probing that
            // silently substitutes ordinal equality for whatever comparer the
            // source used - Roslyn's analyzer config keys, for one, are
            // case-insensitive through CaseInsensitiveComparison, so the copy
            // answers false where Roslyn answers true.
            [PreserveSig]
            int StringCollectionContains(
                long handle,
                [global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::System.Runtime.InteropServices.Marshalling.Utf16StringMarshaller))] string value,
                out int result);

            // Materializes a live collection into an indexable snapshot so
            // enumeration can use the index-based item accessor above. Only
            // enumeration pays for this; membership and count do not.
            [PreserveSig]
            int SnapshotStringCollection(
                long handle,
                out long result);

            [PreserveSig]
            int GetWellKnownObject(
                RoslynWellKnownObject kind,
                out long result);

            [PreserveSig]
            int SymbolEqualityComparerEquals(
                RoslynWellKnownObject kind,
                long x,
                long y,
                out int result);

            [PreserveSig]
            int SymbolEqualityComparerGetHashCode(
                RoslynWellKnownObject kind,
                long symbol,
                out int result);

            [PreserveSig]
            int CopyObjectToStringUtf16(
                long handle,
                nint buffer,
                int bufferLength,
                out int requiredLength);

            [PreserveSig]
            int ObjectEquals(
                long handle,
                long other,
                out int result);

            [PreserveSig]
            int ObjectGetHashCode(
                long handle,
                out int result);
        }
        """;

    private static string EmitAbiVtbl(VtblProjection vtbl)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using System.Runtime.InteropServices;");
        builder.AppendLine("using System.Runtime.InteropServices.Marshalling;");
        builder.AppendLine();
        builder.AppendLine("namespace RoslynAot.Abi;");
        builder.AppendLine();
        builder.AppendLine("[GeneratedComInterface]");
        builder.AppendLine($"[Guid(\"{vtbl.VtblId:D}\")]");
        string baseVtbl = vtbl.BaseVtbl is null
            ? string.Empty
            : $" : {vtbl.BaseVtbl.Name}";
        builder.AppendLine(
            $"public partial interface {vtbl.Name}{baseVtbl}");
        builder.AppendLine("{");
        foreach (ProjectedCall operation in vtbl.Members)
        {
            builder.AppendLine();
            builder.AppendLine("    [PreserveSig]");
            builder.AppendLine($"    int {operation.GeneratedName}(");
            IReadOnlyList<string> abiParameters = GetAbiParameters(operation);
            string[] parameters = abiParameters
                .Select((parameter, index) =>
                    $"        {parameter}" +
                    $"{(index + 1 == abiParameters.Count ? string.Empty : ",")}")
                .ToArray();
            builder.AppendLine(string.Join("\n", parameters));
            builder.AppendLine("    );");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Assigns each distinct projected Roslyn member a stable counter slot.
    /// Keyed on canonical signature, not generated name: a member inherited
    /// into several vtbls is emitted as several dispatcher methods but is one
    /// Roslyn member, and the coverage metric is about members.
    /// </summary>
    private static IReadOnlyDictionary<string, int> GetCallCounterOrdinals(
        ProjectionModel model)
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string signature in model.Vtbls
            .SelectMany(GetDispatcherMembers)
            .Select(operation => operation.CanonicalSignature)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(signature => signature, StringComparer.Ordinal))
        {
            ordinals[signature] = ordinals.Count;
        }

        return ordinals;
    }

    /// <summary>
    /// The control vtbl's own methods, which carry the traffic no per-member
    /// counter can see.
    /// </summary>
    /// <remarks>
    /// Dispatcher counters record one increment per projected member call, but
    /// a single such call can fan out into many control-vtbl crossings — a
    /// collection read costs a count plus two per element, so a member counted
    /// once was really hundreds of round trips. Leaving these uncounted made
    /// the boundary totals look like the whole picture when they were the tip
    /// of it, and a measurement that silently under-reports is worse than none.
    ///
    /// This list must match the interface emitted in <c>EmitAbi</c>; a name
    /// here that no longer exists there is a dead counter, and a method there
    /// missing here reads as zero calls rather than as uninstrumented.
    /// </remarks>
    private static readonly string[] s_controlVtblMembers =
    [
        "GetManifestIdentity",
        "GetVtbl",
        "CopyLastErrorUtf16",
        "CreateSourceTextUtf16",
        "IsObjectType",
        "CreateObjectCollection",
        "GetCollectionCount",
        "GetObjectCollectionItem",
        "CopyStringCollectionItemUtf16",
        "StringCollectionContains",
        "SnapshotStringCollection",
        "GetWellKnownObject",
        "SymbolEqualityComparerEquals",
        "SymbolEqualityComparerGetHashCode",
        "CopyObjectToStringUtf16",
        "ObjectEquals",
        "ObjectGetHashCode",
    ];

    private static string EmitCallCounters(
        ProjectionModel model,
        IReadOnlyDictionary<string, int> ordinals)
    {
        ProjectedCall[] members = model.Vtbls
            .SelectMany(GetDispatcherMembers)
            .GroupBy(
                operation => operation.CanonicalSignature,
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(
                operation => ordinals[operation.CanonicalSignature])
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System.Threading;");
        builder.AppendLine();
        builder.AppendLine("namespace RoslynAot.Csc;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// Per-member call counts at the projection boundary. Counting is");
        builder.AppendLine(
            "/// unconditional - one interlocked increment against a preallocated");
        builder.AppendLine(
            "/// slot, negligible beside the round trip it measures - so a count of");
        builder.AppendLine(
            "/// zero always means 'never called' rather than 'not instrumented'.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("internal static class RoslynCallCounters");
        builder.AppendLine("{");
        builder.AppendLine(
            "    public const int MemberCount = " +
            $"{members.Length + s_controlVtblMembers.Length};");
        builder.AppendLine();
        builder.AppendLine(
            "    private static readonly long[] s_counts = new long[MemberCount];");
        builder.AppendLine();
        builder.AppendLine("    public static readonly string[] MemberNames =");
        builder.AppendLine("    [");
        foreach (ProjectedCall operation in members)
        {
            builder.AppendLine(
                $"        {Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(GetCounterName(operation), quote: true)},");
        }

        foreach (string name in s_controlVtblMembers)
        {
            builder.AppendLine(
                "        " +
                Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                    $"RoslynAot.Abi.IRoslynControlVtbl.{name}",
                    quote: true) +
                ",");
        }

        builder.AppendLine("    ];");
        builder.AppendLine();
        builder.AppendLine("    /// <summary>");
        builder.AppendLine(
            "    /// Ordinals for the control vtbl, which is hand-written and");
        builder.AppendLine(
            "    /// so records against these rather than an emitted literal.");
        builder.AppendLine("    /// </summary>");
        for (int index = 0; index < s_controlVtblMembers.Length; index++)
        {
            builder.AppendLine(
                $"    public const int Control{s_controlVtblMembers[index]} = " +
                $"{members.Length + index};");
        }

        builder.AppendLine();
        builder.AppendLine("    public static void Record(int ordinal) =>");
        builder.AppendLine(
            "        Interlocked.Increment(ref s_counts[ordinal]);");
        builder.AppendLine();
        builder.AppendLine("    public static long[] Snapshot()");
        builder.AppendLine("    {");
        builder.AppendLine("        var snapshot = new long[MemberCount];");
        builder.AppendLine(
            "        for (int index = 0; index < snapshot.Length; index++)");
        builder.AppendLine("        {");
        builder.AppendLine(
            "            snapshot[index] = Interlocked.Read(ref s_counts[index]);");
        builder.AppendLine("        }");
        builder.AppendLine();
        builder.AppendLine("        return snapshot;");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// The member name shape the differential harness already parses out of
    /// AD0001 stack frames, so coverage rows and burn-down reasons join.
    /// </summary>
    private static string GetCounterName(ProjectedCall operation) =>
        $"{operation.Symbol.ContainingType.ToDisplayString()}." +
        $"{operation.Symbol.Name}";

    private static string EmitCompilerDispatcher(
        VtblProjection vtbl,
        IReadOnlyDictionary<string, int> ordinals)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System.Text;");
        builder.AppendLine("using System.Runtime.InteropServices.Marshalling;");
        builder.AppendLine("using RoslynAot.Abi;");
        builder.AppendLine();
        builder.AppendLine("namespace RoslynAot.Csc;");
        builder.AppendLine();
        builder.AppendLine("[GeneratedComClass]");
        builder.AppendLine(
            $"internal sealed partial class {GetDispatcherClassName(vtbl)} : {vtbl.Name}");
        builder.AppendLine("{");
        builder.AppendLine("    private readonly RoslynInterop _owner;");
        builder.AppendLine();
        builder.AppendLine(
            $"    public {GetDispatcherClassName(vtbl)}(RoslynInterop owner)");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        _owner = owner ?? throw new ArgumentNullException(nameof(owner));");
        builder.AppendLine("    }");

        foreach (ProjectedCall operation in GetDispatcherMembers(vtbl))
        {
            EmitCompilerMethod(
                builder,
                operation,
                ordinals[operation.CanonicalSignature]);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EmitCompilerDispatcherRegistry(
        ProjectionModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("namespace RoslynAot.Csc;");
        builder.AppendLine();
        builder.AppendLine("internal static class RoslynDispatcherRegistry");
        builder.AppendLine("{");
        builder.AppendLine("    public static object Create(");
        builder.AppendLine("        long vtblIdLow,");
        builder.AppendLine("        long vtblIdHigh,");
        builder.AppendLine("        RoslynInterop owner) =>");
        builder.AppendLine("        (vtblIdLow, vtblIdHigh) switch");
        builder.AppendLine("        {");
        foreach (VtblProjection vtbl in model.Vtbls)
        {
            (long low, long high) = GetVtblIdParts(vtbl);
            builder.AppendLine(
                $"            ({low}L, {high}L) => new {GetDispatcherClassName(vtbl)}(owner),");
        }

        builder.AppendLine(
            "            _ => throw new PlatformNotSupportedException(\"The requested Roslyn vtable is not available in this build.\"),");
        builder.AppendLine("        };");
        builder.AppendLine();
        builder.AppendLine("    public static bool IsRuntimeType(");
        builder.AppendLine("        object value,");
        builder.AppendLine("        long vtblIdLow,");
        builder.AppendLine("        long vtblIdHigh) =>");
        builder.AppendLine("        (vtblIdLow, vtblIdHigh) switch");
        builder.AppendLine("    {");
        foreach (VtblProjection vtbl in GetRuntimeClassVtbls(model))
        {
            (long low, long high) = GetVtblIdParts(vtbl);
            string typeName = vtbl.FacadeType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            builder.AppendLine(
                $"            ({low}L, {high}L) => value is {typeName},");
        }

        builder.AppendLine("            _ => false,");
        builder.AppendLine("        };");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EmitAnalyzerRuntimeProxyFactory(
        ProjectionModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using System.Runtime.CompilerServices;");
        builder.AppendLine("using System.Runtime.InteropServices;");
        builder.AppendLine("using RoslynAot.Abi;");
        builder.AppendLine("using RoslynAot.RoslynFacade;");
        builder.AppendLine("using Microsoft.CodeAnalysis;");
        builder.AppendLine();
        builder.AppendLine("namespace RoslynAot.AnalyzerRuntime;");
        builder.AppendLine();
        builder.AppendLine("internal static class RoslynProxyFactory");
        builder.AppendLine("{");
        builder.AppendLine("    public static SyntaxNode CreateSyntaxNode(");
        builder.AppendLine("        IRoslynControlVtbl controlVtbl,");
        builder.AppendLine("        long handle) =>");
        builder.AppendLine(
            "        SyntaxNode.__RoslynAotCreateProxy(controlVtbl, handle);");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static IEnumerable<VtblProjection> GetDynamicInterfaceVtbls(
        ProjectionModel model) =>
        model.Vtbls
            .Where(
                vtbl =>
                    !vtbl.IsTypeVtbl &&
                    model.UsesDynamicInterfaceProxy(vtbl.FacadeType))
            .OrderBy(
                vtbl => CanonicalSignatureBuilder.GetMetadataTypeName(
                    vtbl.FacadeType),
                StringComparer.Ordinal);

    private static IEnumerable<VtblProjection> GetRuntimeClassVtbls(
        ProjectionModel model) =>
        model.Vtbls
            .Where(
                vtbl =>
                    !vtbl.IsTypeVtbl &&
                    vtbl.FacadeType.TypeKind is
                        TypeKind.Class or
                        TypeKind.Interface &&
                    IsExternallyReferenceable(vtbl.FacadeType))
            .OrderByDescending(
                vtbl => GetInheritanceDepth(vtbl.FacadeType))
            .ThenBy(
                vtbl => CanonicalSignatureBuilder.GetMetadataTypeName(
                    vtbl.FacadeType),
                StringComparer.Ordinal);

    private static int GetInheritanceDepth(INamedTypeSymbol type)
    {
        int depth = 0;
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static bool IsExternallyReferenceable(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<ProjectedCall> GetDispatcherMembers(
        VtblProjection vtbl)
    {
        var hierarchy = new Stack<VtblProjection>();
        for (VtblProjection? current = vtbl;
             current is not null;
             current = current.BaseVtbl)
        {
            hierarchy.Push(current);
        }

        return hierarchy
            .SelectMany(current => current.Members)
            .GroupBy(
                operation => operation.GeneratedName,
                StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
    }

    private static string GetDispatcherClassName(VtblProjection vtbl) =>
        $"{(vtbl.Name.StartsWith('I') ? vtbl.Name[1..] : vtbl.Name)}Dispatcher";

    private static (long Low, long High) GetVtblIdParts(
        VtblProjection vtbl)
    {
        byte[] bytes = vtbl.VtblId.ToByteArray();
        return (
            BitConverter.ToInt64(bytes, 0),
            BitConverter.ToInt64(bytes, 8));
    }

    private static void EmitCompilerMethod(
        StringBuilder builder,
        ProjectedCall operation,
        int counterOrdinal)
    {
        builder.AppendLine();
        builder.AppendLine(
            $"    public{(operation.ReturnValue.Kind == AbiTypeKind.Utf16String ? " unsafe" : string.Empty)} int {operation.GeneratedName}(");
        IReadOnlyList<string> abiParameters = GetAbiParameters(operation);
        for (int index = 0; index < abiParameters.Count; index++)
        {
            builder.Append("        ");
            builder.Append(abiParameters[index]);
            builder.AppendLine(index + 1 == abiParameters.Count ? ")" : ",");
        }

        builder.AppendLine("    {");

        // Counted before the try, so a member that always throws still reports
        // as called. Coverage answers "was it reached", not "did it succeed".
        builder.AppendLine($"        RoslynCallCounters.Record({counterOrdinal});");
        if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
        {
            builder.AppendLine("        requiredLength = default;");
        }
        else if (operation.ReturnValue.Kind != AbiTypeKind.Void)
        {
            builder.AppendLine("        result = default;");
        }

        builder.AppendLine();
        builder.AppendLine("        try");
        builder.AppendLine("        {");
        foreach (string statement in GetCompilerStatements(operation))
        {
            builder.Append("            ");
            builder.AppendLine(statement);
        }

        builder.AppendLine("            return RoslynAbi.Success;");
        builder.AppendLine("        }");
        builder.AppendLine("        catch (global::System.Exception exception)");
        builder.AppendLine("        {");
        builder.AppendLine("            return _owner.SetError(exception);");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
    }

    private static IEnumerable<string> GetCompilerStatements(
        ProjectedCall operation)
    {
        if (operation.Strategy == ProjectionStrategy.Dispose)
        {
            yield return
                $"_owner.Objects.DisposeObject<{GetSourceType(operation.Symbol.ContainingType)}>" +
                "(receiver);";
            yield break;
        }

        if (operation.Strategy == ProjectionStrategy.PropertySet)
        {
            var property =
                (IPropertySymbol)operation.Symbol.AssociatedSymbol!;
            string receiver = GetCompilerReceiver(operation);
            string value = GetCompilerArgument(
                operation.Parameters[^1]);
            if (property.IsIndexer)
            {
                string indexes = string.Join(
                    ", ",
                    operation.Parameters
                        .Take(operation.Parameters.Count - 1)
                        .Select(GetCompilerArgument));
                yield return $"{receiver}[{indexes}] = {value};";
            }
            else
            {
                yield return $"{receiver}." +
                    $"{CSharpName.EscapeIdentifier(property.Name)} = " +
                    $"{value};";
            }

            yield break;
        }

        string invocation = GetCompilerInvocation(operation);
        if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
        {
            yield return
                "if (bufferLength < 0) throw new global::System.ArgumentOutOfRangeException(nameof(bufferLength));";
            yield return $"string? __roslynAotValue = {invocation};";
            yield return
                "if (__roslynAotValue is null) { requiredLength = -1; return RoslynAbi.Success; }";
            yield return
                "requiredLength = __roslynAotValue.Length;";
            yield return "if (buffer == 0) return RoslynAbi.Success;";
            yield return
                "if (bufferLength < requiredLength) throw new global::System.ArgumentException(\"The UTF-16 result buffer is too small.\", nameof(bufferLength));";
            yield return
                "__roslynAotValue.AsSpan().CopyTo(new global::System.Span<char>((void*)buffer, bufferLength));";
            yield break;
        }

        switch (operation.ReturnValue.Kind)
        {
            case AbiTypeKind.Void:
                yield return $"{invocation};";
                break;
            case AbiTypeKind.Boolean:
                yield return $"result = {invocation} ? 1 : 0;";
                break;
            case AbiTypeKind.Enum:
                yield return
                    $"result = ({operation.ReturnValue.AbiType}){invocation};";
                break;
            case AbiTypeKind.Integral
                when operation.ReturnValue.SourceType.SpecialType ==
                    SpecialType.System_Char:
                yield return $"result = (ushort){invocation};";
                break;
            case AbiTypeKind.Integral:
                yield return $"result = {invocation};";
                break;
            case AbiTypeKind.StringCollection:
                // Deliberately not ToArray'd: the analyzer reaches this
                // collection through a handle so that membership is answered
                // by the collection itself, with its own comparer and its own
                // complexity, rather than by a copy that has neither.
                yield return
                    $"result = _owner.Objects.AddObject({invocation});";
                break;
            case AbiTypeKind.ObjectCollection:
                yield return
                    $"result = _owner.Objects.AddObject(" +
                    $"global::System.Linq.Enumerable.Cast<object>({invocation}).ToArray());";
                break;
            case AbiTypeKind.ObjectHandle:
                yield return operation.ReturnValue.IsNullable
                    ? $"result = _owner.Objects.AddNullableObject({invocation});"
                    : $"result = _owner.Objects.AddObject({invocation});";
                break;
            case AbiTypeKind.ValueHandle:
                yield return $"result = _owner.Objects.AddValue({invocation});";
                break;
            case AbiTypeKind.NullableHandle:
                yield return
                    $"result = {invocation} is {{ }} value " +
                    "? _owner.Objects.AddValue(value) : 0;";
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported compiler result for " +
                    $"'{operation.CanonicalSignature}'.");
        }
    }

    private static string GetCompilerInvocation(
        ProjectedCall operation)
    {
        IMethodSymbol method = operation.Symbol;
        string receiver = GetCompilerReceiver(operation);
        string[] arguments = operation.Parameters
            .Select(GetCompilerArgument)
            .ToArray();

        if (operation.Strategy == ProjectionStrategy.Constructor)
        {
            return $"new {GetSourceType(method.ContainingType)}" +
                $"({string.Join(", ", arguments)})";
        }

        if (operation.Strategy == ProjectionStrategy.PropertyGet)
        {
            var property =
                (IPropertySymbol)method.AssociatedSymbol!;
            return property.IsIndexer
                ? $"{receiver}[{string.Join(", ", arguments)}]"
                : $"{receiver}." +
                    CSharpName.EscapeIdentifier(property.Name);
        }

        return $"{receiver}." +
            $"{CSharpName.EscapeIdentifier(method.Name)}" +
            $"({string.Join(", ", arguments)})";
    }

    private static string GetCompilerReceiver(
        ProjectedCall operation)
    {
        if (!operation.HasReceiver)
        {
            return GetSourceType(operation.Symbol.ContainingType);
        }

        AbiTypePlan receiver = operation.Receiver
            ?? throw new InvalidOperationException(
                "Instance operation has no receiver ABI plan.");
        return receiver.Kind switch
        {
            AbiTypeKind.ObjectHandle =>
                $"_owner.Objects.GetObject<{GetSourceType(operation.Symbol.ContainingType)}>(receiver)",
            AbiTypeKind.ValueHandle =>
                $"_owner.Objects.GetValue<{GetSourceType(operation.Symbol.ContainingType)}>(receiver)",
            _ => throw new InvalidOperationException(
                $"Unsupported receiver plan '{receiver.Kind}'."),
        };
    }

    private static string GetCompilerArgument(ParameterProjection parameter)
    {
        string name = CSharpName.EscapeIdentifier(
            parameter.Symbol.Name);
        return parameter.AbiType.Kind switch
        {
            AbiTypeKind.Boolean => $"{name} != 0",
            AbiTypeKind.Enum =>
                $"({GetSourceType(parameter.Symbol.Type)}){name}",
            AbiTypeKind.Integral
                when parameter.Symbol.Type.SpecialType ==
                    SpecialType.System_Char =>
                $"(char){name}",
            AbiTypeKind.Integral => name,
            AbiTypeKind.ObjectHandle =>
                parameter.AbiType.IsNullable
                    ? $"{name} == 0 ? null : " +
                    $"_owner.Objects.GetObject<{GetSourceType(parameter.AbiType.RemoteType!)}>({name})"
                    : $"_owner.Objects.GetObject<{GetSourceType(parameter.AbiType.RemoteType!)}>({name})",
            AbiTypeKind.ValueHandle =>
                $"_owner.Objects.GetValue<{GetSourceType(parameter.Symbol.Type)}>({name})",
            AbiTypeKind.NullableHandle =>
                $"{name} == 0 ? null : " +
                $"_owner.Objects.GetValue<{GetSourceType(parameter.AbiType.RemoteType!)}>({name})",
            AbiTypeKind.ObjectArray =>
                $"global::System.Array.ConvertAll(" +
                $"_owner.Objects.GetObject<object[]>({name}), " +
                $"static value => ({GetSourceType(((IArrayTypeSymbol)parameter.Symbol.Type).ElementType)})value)",
            AbiTypeKind.Utf16String => name,
            _ => throw new InvalidOperationException(
                $"Unsupported compiler argument '{parameter.Symbol}'."),
        };
    }

    private static IReadOnlyList<string> GetAbiParameters(
        ProjectedCall operation)
    {
        var parameters = new List<string>();
        if (operation.HasReceiver)
        {
            parameters.Add(
                $"{operation.Receiver!.AbiType} receiver");
        }

        parameters.AddRange(operation.Parameters.Select(parameter =>
            parameter.AbiType.Kind == AbiTypeKind.Utf16String
                ? "[global::System.Runtime.InteropServices.Marshalling.MarshalUsing(" +
                    "typeof(global::System.Runtime.InteropServices.Marshalling.Utf16StringMarshaller))] " +
                    "string " +
                    CSharpName.EscapeIdentifier(parameter.Symbol.Name)
                : $"{parameter.AbiType.AbiType} " +
                    CSharpName.EscapeIdentifier(parameter.Symbol.Name)));

        if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
        {
            parameters.Add("nint buffer");
            parameters.Add("int bufferLength");
            parameters.Add("out int requiredLength");
        }
        else if (operation.ReturnValue.Kind != AbiTypeKind.Void)
        {
            parameters.Add($"out {operation.ReturnValue.AbiType} result");
        }

        return parameters;
    }

    private static string EmitFacadeRuntime()
    {
        return """
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;
        using System.Threading;
        using RoslynAot.Abi;

        namespace RoslynAot.RoslynFacade;

        public delegate int CopyUtf16String(
            nint buffer,
            int bufferLength,
            out int requiredLength);

        public sealed class RoslynProxyTypeMap;

        /// <summary>
        /// A compiler-owned string collection, reached through its handle
        /// rather than copied.
        /// </summary>
        /// <remarks>
        /// Copying the contents into a <c>string[]</c> and handing that back
        /// preserves the elements but discards the collection's behaviour.
        /// Two things go missing, and only one of them is benign:
        /// <list type="bullet">
        /// <item>Membership complexity. Roslyn backs these with sets -
        /// <c>IdentifierCollection</c>, <c>ImmutableSegmentedHashSet</c> -
        /// so an O(1) lookup becomes an O(n) array scan.</item>
        /// <item>Equality semantics. The copy answers with
        /// <c>EqualityComparer&lt;string&gt;.Default</c> no matter what the
        /// source used. Analyzer config keys compare case-insensitively
        /// through <c>CaseInsensitiveComparison</c>, so a copy answers
        /// <c>false</c> where Roslyn answers <c>true</c> - a wrong answer
        /// with no exception attached.</item>
        /// </list>
        /// Implementing <see cref="ICollection{T}"/> rather than only
        /// <see cref="IEnumerable{T}"/> is load-bearing:
        /// <c>Enumerable.Contains</c> defers to <c>ICollection&lt;T&gt;</c>
        /// when the runtime type provides it, so members declared as
        /// <c>IEnumerable&lt;string&gt;</c> get the faithful answer too.
        /// </remarks>
        public sealed class RoslynStringCollection : ICollection<string>
        {
            private readonly IRoslynControlVtbl _controlVtbl;
            private readonly long _handle;
            private string[]? _snapshot;

            public RoslynStringCollection(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                _controlVtbl = controlVtbl ??
                    throw new ArgumentNullException(nameof(controlVtbl));
                _handle = handle != 0
                    ? handle
                    : throw new ArgumentOutOfRangeException(nameof(handle));
            }

            public int Count
            {
                get
                {
                    int status = _controlVtbl.GetCollectionCount(
                        _handle,
                        out int count);
                    RoslynFacadeRuntime.ThrowIfFailed(_controlVtbl, status);
                    return count;
                }
            }

            public bool IsReadOnly => true;

            public bool Contains(string item)
            {
                if (item is null)
                {
                    return false;
                }

                int status = _controlVtbl.StringCollectionContains(
                    _handle,
                    item,
                    out int result);
                RoslynFacadeRuntime.ThrowIfFailed(_controlVtbl, status);
                return result != 0;
            }

            // Enumeration is the one operation that cannot be answered a
            // question at a time, so it snapshots. Held afterwards because
            // the compiler-side collection a handle refers to is immutable
            // for the handle's lifetime.
            private string[] Snapshot()
            {
                string[]? snapshot = _snapshot;
                if (snapshot is null)
                {
                    int status = _controlVtbl.SnapshotStringCollection(
                        _handle,
                        out long snapshotHandle);
                    RoslynFacadeRuntime.ThrowIfFailed(_controlVtbl, status);
                    snapshot = RoslynFacadeRuntime.ReadStringCollection(
                        _controlVtbl,
                        snapshotHandle);
                    _snapshot = snapshot;
                }

                return snapshot;
            }

            public IEnumerator<string> GetEnumerator() =>
                ((IEnumerable<string>)Snapshot()).GetEnumerator();

            System.Collections.IEnumerator
                System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();

            public void CopyTo(string[] array, int arrayIndex) =>
                Snapshot().CopyTo(array, arrayIndex);

            public void Add(string item) =>
                throw new NotSupportedException();

            public bool Remove(string item) =>
                throw new NotSupportedException();

            public void Clear() =>
                throw new NotSupportedException();
        }

        public sealed class RoslynObjectProxy : IDynamicInterfaceCastable
        {
            public RoslynObjectProxy(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                ControlVtbl = controlVtbl ??
                    throw new ArgumentNullException(nameof(controlVtbl));
                Handle = handle != 0
                    ? handle
                    : throw new ArgumentOutOfRangeException(nameof(handle));
            }

            private int? _hashCode;

            public IRoslynControlVtbl ControlVtbl { get; }

            private long Handle { get; }

            public long GetHandle(IRoslynControlVtbl controlVtbl) => Handle;

            private static readonly Lock s_cacheGate = new();
            private static readonly Dictionary<long, WeakReference<RoslynObjectProxy>>
                s_cache = [];
            private static int s_cacheInsertsSinceSweep;
            private const int CacheSweepInterval = 4096;

            /// <summary>
            /// Resolves a handle to the same proxy every time, so a Roslyn
            /// object read through two different interface accessors (say,
            /// as <c>ISymbol</c> and later as <c>ITypeSymbol</c>) comes back
            /// as the literal same instance — correct here because
            /// <see cref="IDynamicInterfaceCastable"/> lets one proxy answer
            /// any interface cast dynamically, so unlike the per-declaring-
            /// type proxies used for class hierarchies, there is nothing
            /// static-type-specific baked into this object to make distinct
            /// instances necessary. Weak-valued: the compiler process keeps
            /// every crossed object alive for good (see the reverse map in
            /// RoslynHandleTable), but an analyzer module that has moved on
            /// from a compilation should still be able to reclaim its own
            /// proxies for it.
            /// </summary>
            public static RoslynObjectProxy GetOrCreate(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                lock (s_cacheGate)
                {
                    if (s_cache.TryGetValue(
                            handle,
                            out WeakReference<RoslynObjectProxy>? entry) &&
                        entry.TryGetTarget(out RoslynObjectProxy? existing) &&
                        ReferenceEquals(existing.ControlVtbl, controlVtbl))
                    {
                        return existing;
                    }

                    var proxy = new RoslynObjectProxy(controlVtbl, handle);
                    s_cache[handle] = new WeakReference<RoslynObjectProxy>(proxy);
                    if (++s_cacheInsertsSinceSweep >= CacheSweepInterval)
                    {
                        SweepDeadCacheEntries();
                    }

                    return proxy;
                }
            }

            // Must run under s_cacheGate: bounds the dictionary's own growth
            // against a long-lived analyzer module, since a dead
            // WeakReference's entry is otherwise never removed on its own.
            private static void SweepDeadCacheEntries()
            {
                s_cacheInsertsSinceSweep = 0;
                List<long>? dead = null;
                foreach (KeyValuePair<long, WeakReference<RoslynObjectProxy>> pair
                    in s_cache)
                {
                    if (!pair.Value.TryGetTarget(out _))
                    {
                        (dead ??= []).Add(pair.Key);
                    }
                }

                if (dead is not null)
                {
                    foreach (long key in dead)
                    {
                        s_cache.Remove(key);
                    }
                }
            }

            public override string ToString() =>
                RoslynFacadeRuntime.ReadUtf16String(
                    ControlVtbl,
                    (nint buffer, int bufferLength, out int requiredLength) =>
                        ControlVtbl.CopyObjectToStringUtf16(
                            Handle,
                            buffer,
                            bufferLength,
                            out requiredLength)) ?? string.Empty;

            public override bool Equals(object? other)
            {
                // GetOrCreate dedups by handle, so equal handles are usually
                // already the same instance and return above. This
                // ControlVtbl check is not the control-scoping migration
                // Step 4 retired — it survives because handles are no longer
                // tagged with which table they came from, so it is what
                // stops two numerically equal handles from two different
                // live control identities (were that ever to happen) from
                // comparing equal.
                if (ReferenceEquals(this, other))
                {
                    return true;
                }

                if (other is not RoslynObjectProxy proxy ||
                    !ReferenceEquals(ControlVtbl, proxy.ControlVtbl))
                {
                    return false;
                }

                if (Handle == proxy.Handle)
                {
                    return true;
                }

                int status = ControlVtbl.ObjectEquals(
                    Handle,
                    proxy.Handle,
                    out int equal);
                RoslynFacadeRuntime.ThrowIfFailed(ControlVtbl, status);
                return equal != 0;
            }

            public override int GetHashCode()
            {
                int? cached = _hashCode;
                if (cached is null)
                {
                    int status = ControlVtbl.ObjectGetHashCode(
                        Handle,
                        out int hashCode);
                    RoslynFacadeRuntime.ThrowIfFailed(ControlVtbl, status);
                    cached = hashCode;
                    _hashCode = cached;
                }

                return cached.GetValueOrDefault();
            }

            public bool IsInterfaceImplemented(
                RuntimeTypeHandle interfaceType,
                bool throwIfNotImplemented)
            {
                if (!TryGetImplementationType(
                        interfaceType,
                        out Type? implementationType))
                {
                    return false;
                }

                byte[] bytes = implementationType.GUID.ToByteArray();
                int status = ControlVtbl.IsObjectType(
                    Handle,
                    BitConverter.ToInt64(bytes, 0),
                    BitConverter.ToInt64(bytes, 8),
                    out int isType);
                RoslynFacadeRuntime.ThrowIfFailed(ControlVtbl, status);
                return isType != 0;
            }

            public RuntimeTypeHandle GetInterfaceImplementation(
                RuntimeTypeHandle interfaceType)
            {
                if (!TryGetImplementationType(
                        interfaceType,
                        out Type? implementationType))
                {
                    throw new InvalidCastException(
                        Type.GetTypeFromHandle(interfaceType).FullName);
                }

                return implementationType.TypeHandle;
            }

            private static bool TryGetImplementationType(
                RuntimeTypeHandle interfaceType,
                out Type? implementationType)
            {
                Type requestedType = Type.GetTypeFromHandle(interfaceType);
                return TypeMapping
                    .GetOrCreateProxyTypeMapping<RoslynProxyTypeMap>()
                    .TryGetValue(
                        requestedType,
                        out implementationType);
            }
        }

        public sealed class RoslynRemoteException : Exception
        {
            public RoslynRemoteException(string message, int status)
                : base(message)
            {
                Status = status;
            }

            public int Status { get; }
        }

        public static unsafe class RoslynFacadeRuntime
        {
            private static readonly StrategyBasedComWrappers s_comWrappers = new();
            private static readonly AsyncLocal<IRoslynControlVtbl?> s_current = new();

            public static IRoslynControlVtbl GetOrCreateControlVtbl(
                nint instance)
            {
                if (instance == 0)
                {
                    throw new ArgumentNullException(nameof(instance));
                }

                var controlVtbl = (IRoslynControlVtbl)s_comWrappers
                    .GetOrCreateObjectForComInstance(
                        instance,
                        CreateObjectFlags.None);
                int status = controlVtbl.GetManifestIdentity(
                    out long identityLow,
                    out long identityHigh);
                if (status != RoslynAbi.Success ||
                    identityLow != RoslynAbi.ManifestIdentityLow ||
                    identityHigh != RoslynAbi.ManifestIdentityHigh)
                {
                    throw new InvalidOperationException(
                        "The compiler Roslyn projection manifest is incompatible.");
                }

                return controlVtbl;
            }

            public static IRoslynControlVtbl GetCurrentControlVtbl() =>
                s_current.Value ?? throw new InvalidOperationException(
                    "No compiler Roslyn control vtbl is active.");

            public static IDisposable Enter(IRoslynControlVtbl controlVtbl)
            {
                ArgumentNullException.ThrowIfNull(controlVtbl);
                IRoslynControlVtbl? previous = s_current.Value;
                s_current.Value = controlVtbl;
                return new Scope(previous);
            }

            public static unsafe void ThrowIfFailed(
                IRoslynControlVtbl controlVtbl,
                int status)
            {
                if (status == RoslynAbi.Success)
                {
                    return;
                }

                int queryStatus = controlVtbl.CopyLastErrorUtf16(
                    0,
                    0,
                    out int charCount,
                    out RoslynRemoteErrorKind errorKind);
                if (queryStatus != RoslynAbi.Success || charCount < 0)
                {
                    throw new RoslynRemoteException(
                        $"Remote Roslyn operation failed with 0x{status:x8}; " +
                        "its error details could not be queried.",
                        status);
                }

                string message = charCount == 0
                    ? $"Remote Roslyn operation failed with 0x{status:x8}."
                    : string.Create(
                        charCount,
                        (controlVtbl, errorKind, status),
                        static (destination, state) =>
                        {
                            fixed (char* buffer = destination)
                            {
                                int copyStatus =
                                    state.controlVtbl.CopyLastErrorUtf16(
                                        (nint)buffer,
                                        destination.Length,
                                        out int copiedCharCount,
                                        out RoslynRemoteErrorKind copiedErrorKind);
                                if (copyStatus != RoslynAbi.Success ||
                                    copiedCharCount != destination.Length ||
                                    copiedErrorKind != state.errorKind)
                                {
                                    throw new RoslynRemoteException(
                                        $"Remote Roslyn operation failed with 0x{state.status:x8}; " +
                                        "its error details changed while being copied.",
                                        state.status);
                                }
                            }
                        });
                throw errorKind switch
                {
                    RoslynRemoteErrorKind.Argument =>
                        new ArgumentException(message),
                    RoslynRemoteErrorKind.ObjectDisposed =>
                        new ObjectDisposedException("Roslyn facade", message),
                    RoslynRemoteErrorKind.Unsupported =>
                        new PlatformNotSupportedException(message),
                    RoslynRemoteErrorKind.OperationCanceled =>
                        new OperationCanceledException(message),
                    _ => new RoslynRemoteException(message, status),
                };
            }

            public static T UnsupportedStaticField<T>(string message) =>
                throw new PlatformNotSupportedException(message);

            public static unsafe string? ReadUtf16String(
                IRoslynControlVtbl controlVtbl,
                CopyUtf16String copy)
            {
                ArgumentNullException.ThrowIfNull(controlVtbl);
                ArgumentNullException.ThrowIfNull(copy);

                int status = copy(0, 0, out int charCount);
                ThrowIfFailed(controlVtbl, status);
                if (charCount == -1)
                {
                    return null;
                }

                if (charCount < -1)
                {
                    throw new InvalidOperationException(
                        "The remote UTF-16 string length is invalid.");
                }

                if (charCount == 0)
                {
                    return string.Empty;
                }

                return string.Create(
                    charCount,
                    (controlVtbl, copy),
                    static (destination, state) =>
                    {
                        fixed (char* buffer = destination)
                        {
                            int copyStatus = state.copy(
                                (nint)buffer,
                                destination.Length,
                                out int copiedCharCount);
                            ThrowIfFailed(state.controlVtbl, copyStatus);
                            if (copiedCharCount != destination.Length)
                            {
                                throw new InvalidOperationException(
                                    "The remote UTF-16 string changed while being copied.");
                            }
                        }
                    });
            }

            public static long CreateSourceTextHandle(
                IRoslynControlVtbl controlVtbl,
                string text,
                int checksumAlgorithm)
            {
                ArgumentNullException.ThrowIfNull(text);
                long result;
                fixed (char* buffer = text)
                {
                    int status = controlVtbl.CreateSourceTextUtf16(
                        (nint)buffer,
                        text.Length,
                        checksumAlgorithm,
                        out result);
                    ThrowIfFailed(controlVtbl, status);
                }

                return result;
            }

            public static string[] ReadStringCollection(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                int status = controlVtbl.GetCollectionCount(
                    handle,
                    out int count);
                ThrowIfFailed(controlVtbl, status);
                if (count < 0)
                {
                    throw new InvalidOperationException(
                        "The remote collection length is invalid.");
                }

                var result = new string[count];
                for (int index = 0; index < result.Length; index++)
                {
                    int itemIndex = index;
                    result[index] = ReadUtf16String(
                        controlVtbl,
                        (nint buffer, int bufferLength, out int requiredLength) =>
                            controlVtbl.CopyStringCollectionItemUtf16(
                                handle,
                                itemIndex,
                                buffer,
                                bufferLength,
                                out requiredLength)) ??
                        throw new InvalidOperationException(
                            "The remote string collection contains null.");
                }

                return result;
            }

            public static RoslynStringCollection ReadStringCollectionProxy(
                IRoslynControlVtbl controlVtbl,
                long handle) =>
                new(controlVtbl, handle);

            public static long CreateObjectCollectionHandle(
                IRoslynControlVtbl controlVtbl,
                long[] handles)
            {
                ArgumentNullException.ThrowIfNull(handles);
                long result;
                fixed (long* buffer = handles)
                {
                    int status = controlVtbl.CreateObjectCollection(
                        (nint)buffer,
                        handles.Length,
                        out result);
                    ThrowIfFailed(controlVtbl, status);
                }

                return result;
            }

            public static T[] ReadObjectCollection<T>(
                IRoslynControlVtbl controlVtbl,
                long handle,
                Func<IRoslynControlVtbl, long, T> createProxy)
            {
                ArgumentNullException.ThrowIfNull(createProxy);
                int status = controlVtbl.GetCollectionCount(
                    handle,
                    out int count);
                ThrowIfFailed(controlVtbl, status);
                if (count < 0)
                {
                    throw new InvalidOperationException(
                        "The remote collection length is invalid.");
                }

                var result = new T[count];
                for (int index = 0; index < result.Length; index++)
                {
                    status = controlVtbl.GetObjectCollectionItem(
                        handle,
                        index,
                        out long itemHandle);
                    ThrowIfFailed(controlVtbl, status);
                    result[index] = createProxy(controlVtbl, itemHandle);
                }

                return result;
            }

            public static T GetWellKnownObject<T>(
                RoslynWellKnownObject kind,
                Func<IRoslynControlVtbl, long, T> createProxy)
            {
                ArgumentNullException.ThrowIfNull(createProxy);
                IRoslynControlVtbl controlVtbl = GetCurrentControlVtbl();
                int status = controlVtbl.GetWellKnownObject(
                    kind,
                    out long handle);
                ThrowIfFailed(controlVtbl, status);
                return createProxy(controlVtbl, handle);
            }

            private sealed class Scope(IRoslynControlVtbl? previous) : IDisposable
            {
                private IRoslynControlVtbl? _previous = previous;
                private bool _disposed;

                public void Dispose()
                {
                    if (_disposed)
                    {
                        return;
                    }

                    s_current.Value = _previous;
                    _previous = null;
                    _disposed = true;
                }
            }
        }
        """;
    }

    private static string EmitVtblFactory(ProjectionModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("using System.Runtime.CompilerServices;");
        builder.AppendLine("using System.Runtime.InteropServices;");
        builder.AppendLine("using System.Runtime.InteropServices.Marshalling;");
        builder.AppendLine("using RoslynAot.Abi;");
        builder.AppendLine();
        builder.AppendLine("namespace RoslynAot.RoslynFacade;");
        builder.AppendLine();
        builder.AppendLine("public static class RoslynVtblFactory");
        builder.AppendLine("{");
        builder.AppendLine(
            "    private static readonly StrategyBasedComWrappers s_comWrappers = new();");
        builder.AppendLine(
            "    private static readonly ConditionalWeakTable<IRoslynControlVtbl, VtblCache> s_caches = new();");
        builder.AppendLine();
        foreach (VtblProjection vtbl in model.Vtbls)
        {
            (long low, long high) = GetVtblIdParts(vtbl);
            builder.AppendLine(
                $"    public static {vtbl.Name} " +
                $"{vtbl.FactoryMethodName}(");
            builder.AppendLine("        IRoslynControlVtbl controlVtbl)");
            builder.AppendLine("    {");
            builder.AppendLine(
                "        ArgumentNullException.ThrowIfNull(controlVtbl);");
            builder.AppendLine(
                $"        return GetVtbl<{vtbl.Name}>(");
            builder.AppendLine("            controlVtbl,");
            builder.AppendLine($"            {low}L,");
            builder.AppendLine($"            {high}L);");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("    private static T GetVtbl<T>(");
        builder.AppendLine("        IRoslynControlVtbl controlVtbl,");
        builder.AppendLine("        long vtblIdLow,");
        builder.AppendLine("        long vtblIdHigh)");
        builder.AppendLine("        where T : class");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        object vtbl = s_caches.GetValue(controlVtbl, static _ => new VtblCache())");
        builder.AppendLine(
            "            .GetOrCreate(controlVtbl, vtblIdLow, vtblIdHigh);");
        builder.AppendLine("        return vtbl as T ?? throw new InvalidOperationException(");
        builder.AppendLine(
            "            $\"The resolved Roslyn vtable does not implement '{typeof(T).FullName}'.\");");
        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    private sealed class VtblCache");
        builder.AppendLine("    {");
        builder.AppendLine(
            "        private readonly Dictionary<(long Low, long High), object> _vtbls = [];");
        builder.AppendLine();
        builder.AppendLine("        public object GetOrCreate(");
        builder.AppendLine("            IRoslynControlVtbl controlVtbl,");
        builder.AppendLine("            long vtblIdLow,");
        builder.AppendLine("            long vtblIdHigh)");
        builder.AppendLine("        {");
        builder.AppendLine("            lock (_vtbls)");
        builder.AppendLine("            {");
        builder.AppendLine(
            "                if (_vtbls.TryGetValue((vtblIdLow, vtblIdHigh), out object? existing))");
        builder.AppendLine("                {");
        builder.AppendLine("                    return existing;");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine(
            "                int status = controlVtbl.GetVtbl(vtblIdLow, vtblIdHigh, out nint pointer);");
        builder.AppendLine(
            "                RoslynFacadeRuntime.ThrowIfFailed(controlVtbl, status);");
        builder.AppendLine("                if (pointer == 0)");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    throw new InvalidOperationException(\"The compiler returned no Roslyn vtable pointer.\");");
        builder.AppendLine("                }");
        builder.AppendLine();
        builder.AppendLine("                try");
        builder.AppendLine("                {");
        builder.AppendLine(
            "                    object created = s_comWrappers.GetOrCreateObjectForComInstance(");
        builder.AppendLine("                        pointer,");
        builder.AppendLine("                        CreateObjectFlags.None);");
        builder.AppendLine(
            "                    _vtbls.Add((vtblIdLow, vtblIdHigh), created);");
        builder.AppendLine("                    return created;");
        builder.AppendLine("                }");
        builder.AppendLine("                finally");
        builder.AppendLine("                {");
        builder.AppendLine("                    RoslynAbi.Release(pointer);");
        builder.AppendLine("                }");
        builder.AppendLine("            }");
        builder.AppendLine("        }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void WriteManifest(string path, ProjectionModel model)
    {
        using FileStream stream = File.Create(path);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("identity", model.Identity);
        writer.WriteString(
            "controlVtblId",
            model.ControlVtblId);
        writer.WriteStartArray("vtbls");
        foreach (VtblProjection vtbl in model.Vtbls)
        {
            writer.WriteStartObject();
            writer.WriteString("name", vtbl.Name);
            writer.WriteString(
                "vtblId",
                vtbl.VtblId);
            writer.WriteString(
                "facadeType",
                CanonicalSignatureBuilder.GetMetadataTypeName(
                    vtbl.FacadeType));
            writer.WriteString(
                "kind",
                vtbl.IsTypeVtbl
                    ? "type"
                    : "instance");
            writer.WriteNumber(
                "memberCount",
                vtbl.Members.Count);
            if (vtbl.BaseVtbl is not null)
            {
                writer.WriteString(
                    "baseVtbl",
                    vtbl.BaseVtbl.Name);
            }
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("assemblies");
        foreach (IAssemblySymbol assembly in model.Assemblies)
        {
            writer.WriteStartObject();
            writer.WriteString("name", assembly.Identity.Name);
            writer.WriteString("version", assembly.Identity.Version.ToString());
            writer.WriteString(
                "publicKeyToken",
                assembly.Identity.PublicKeyToken.IsDefaultOrEmpty
                    ? string.Empty
                    : Convert.ToHexString(
                        assembly.Identity.PublicKeyToken.AsSpan())
                        .ToLowerInvariant());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("types");
        foreach (TypeProjection type in model.Types)
        {
            writer.WriteStartObject();
            writer.WriteString("canonicalId", type.CanonicalId);
            writer.WriteString("shape", type.Shape);
            writer.WriteString("ownership", type.Ownership.ToString());
            writer.WriteBoolean("ownershipDeclared", type.OwnershipDeclared);
            writer.WriteString("ownershipReason", type.OwnershipReason);

            writer.WriteBoolean("reachable", type.IsReachable);
            if (type.ReachedBy is not null)
            {
                writer.WriteString("reachedBy", type.ReachedBy);
            }

            writer.WriteBoolean("requiresProxy", type.RequiresProxy);
            writer.WriteBoolean(
                "dynamicInterfaceProxy",
                type.UsesDynamicInterfaceProxy);
            if (type.InstanceVtbl is not null)
            {
                writer.WriteString("instanceVtbl", type.InstanceVtbl.Name);
            }

            if (type.TypeVtbl is not null)
            {
                writer.WriteString("typeVtbl", type.TypeVtbl.Name);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("members");
        foreach (MemberProjection member in model.Members)
        {
            writer.WriteStartObject();
            writer.WriteString("canonicalId", member.CanonicalId);
            writer.WriteString("canonicalSignature", member.CanonicalSignature);
            writer.WriteBoolean("supported", member.IsSupported);
            if (member.UnsupportedReason is not null)
            {
                writer.WriteString(
                    "unsupportedReason",
                    member.UnsupportedReason);
            }

            writer.WriteStartArray("calls");
            foreach (ProjectedCall operation in member.Calls
                .OrderBy(
                    operation => operation.CanonicalSignature,
                    StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("canonicalId", operation.CanonicalId);
                writer.WriteString(
                    "canonicalSignature",
                    operation.CanonicalSignature);
                writer.WriteString("wireSignature", operation.WireSignature);
                writer.WriteString("generatedName", operation.GeneratedName);
                writer.WriteBoolean("supported", operation.IsSupported);
                writer.WriteString(
                    "strategy",
                    operation.Strategy.ToString());
                if (operation.Vtbl is not null)
                {
                    writer.WriteString(
                        "vtbl",
                        operation.Vtbl.Name);
                    writer.WriteString(
                        "vtblId",
                        operation.Vtbl.VtblId);
                }
                WriteAbiPlan(writer, "receiver", operation.Receiver);
                writer.WriteStartArray("parameters");
                foreach (ParameterProjection parameter in operation.Parameters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", parameter.Symbol.Name);
                    WriteAbiPlan(writer, "abi", parameter.AbiType);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                WriteAbiPlan(
                    writer,
                    "return",
                    operation.ReturnValue);
                if (operation.UnsupportedReason is not null)
                {
                    writer.WriteString(
                        "unsupportedReason",
                        operation.UnsupportedReason);
                }

                if (operation.OverrideReason is not null)
                {
                    writer.WriteString(
                        "overrideReason",
                        operation.OverrideReason);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        stream.WriteByte((byte)'\n');
    }

    private static void WriteAbiPlan(
        Utf8JsonWriter writer,
        string propertyName,
        AbiTypePlan? plan)
    {
        if (plan is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteString("kind", plan.Kind.ToString());
        if (plan.IsSupported)
        {
            writer.WriteString("abiType", plan.AbiType);
            writer.WriteBoolean("nullable", plan.IsNullable);
        }
        else
        {
            writer.WriteString(
                "unsupportedReason",
                plan.UnsupportedReason);
        }

        writer.WriteEndObject();
    }

    private static string EmitInventory(ProjectionModel model)
    {
        var builder = new StringBuilder();
        int supportedCount =
            model.Calls.Count(call => call.IsSupported);
        builder.AppendLine($"identity={model.Identity}");
        builder.AppendLine($"supported={supportedCount}");
        builder.AppendLine(
            $"unsupported={model.Calls.Count - supportedCount}");
        builder.AppendLine(
            $"overrides={model.Calls.Count(call => call.OverrideReason is not null)}");
        builder.AppendLine($"vtbls={model.Vtbls.Count}");
        builder.AppendLine($"types={model.Types.Count}");
        builder.AppendLine(
            "declaredOwnership=" +
            model.Types.Count(type => type.OwnershipDeclared));
        builder.AppendLine(
            $"reachableTypes={model.Types.Count(type => type.IsReachable)}");
        builder.AppendLine(
            "unreachableCalls=" +
            model.Calls.Count(call =>
                call.IsSupported && !model.IsReachable(call)));
        builder.AppendLine();

        foreach (TypeProjection type in model.Types)
        {
            builder.Append("TYPE ownership=");
            builder.Append(type.Ownership);
            builder.Append(type.OwnershipDeclared
                ? " (declared)"
                : " (derived)");
            builder.Append(" shape=");
            builder.Append(type.Shape);
            builder.Append(" proxy=");
            builder.Append(type.UsesDynamicInterfaceProxy
                ? "dynamic"
                : type.RequiresProxy
                    ? "yes"
                    : "no");
            builder.Append(" instanceVtbl=");
            builder.Append(type.InstanceVtbl?.Name ?? "none");
            builder.Append(" typeVtbl=");
            builder.Append(type.TypeVtbl?.Name ?? "none");
            builder.Append(" reachedBy=");
            builder.Append(type.ReachedBy ?? "unreachable");
            builder.Append(" id=");
            builder.AppendLine(type.CanonicalId);
        }

        builder.AppendLine();

        foreach (ProjectedCall operation in model.Calls)
        {
            builder.Append(operation.IsSupported
                ? "SUPPORTED"
                : "UNSUPPORTED");
            builder.Append(" name=");
            builder.Append(operation.GeneratedName);
            builder.Append(" strategy=");
            builder.Append(operation.Strategy);
            builder.Append(" vtbl=");
            builder.Append(operation.Vtbl?.Name ?? "none");
            builder.Append(" receiver=");
            builder.Append(
                operation.Receiver?.InventoryName ?? "none");
            builder.Append(" parameters=[");
            builder.Append(
                string.Join(
                    ",",
                    operation.Parameters.Select(parameter =>
                        $"{parameter.Symbol.Name}:{parameter.AbiType.InventoryName}")));
            builder.Append("] return=");
            builder.Append(operation.ReturnValue.InventoryName);
            if (operation.UnsupportedReason is not null)
            {
                builder.Append(" reason=");
                builder.Append(operation.UnsupportedReason);
            }

            if (operation.OverrideReason is not null)
            {
                builder.Append(" override=");
                builder.Append(operation.OverrideReason);
            }

            builder.Append(" wire=");
            builder.Append(operation.WireSignature);
            builder.Append(" id=");
            builder.AppendLine(operation.CanonicalId);
        }

        return builder.ToString();
    }

    private static string GetSourceType(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteGeneratedFile(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            CSharpFileBuilder.DefaultFileHeader +
            content.TrimEnd().ReplaceLineEndings("\n") +
            "\n");
    }
}
