using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynAot.RoslynFacadeGenerator;

internal enum ProjectionStrategy
{
    Unsupported,
    InstanceMethod,
    StaticMethod,
    Constructor,
    PropertyGet,
    PropertySet,
    Dispose,
}

internal enum AbiTypeKind
{
    Unsupported,
    Void,
    Integral,
    Boolean,
    Enum,
    Utf16String,
    StringCollection,
    ObjectCollection,
    ObjectArray,
    ObjectHandle,
    ValueHandle,
    NullableHandle,
}

internal enum AbiTypePosition
{
    Receiver,
    Parameter,
    Return,
    ConstructorReturn,
}

internal sealed record AbiTypePlan(
    AbiTypeKind Kind,
    string AbiType,
    ITypeSymbol SourceType,
    bool IsNullable,
    string? UnsupportedReason)
{
    public bool IsSupported => Kind != AbiTypeKind.Unsupported;

    public bool IsHandle =>
        Kind is AbiTypeKind.ObjectHandle
            or AbiTypeKind.ValueHandle
            or AbiTypeKind.NullableHandle;

    public INamedTypeSymbol? RemoteType =>
        Kind == AbiTypeKind.NullableHandle &&
        SourceType is INamedTypeSymbol
        {
            TypeArguments.Length: 1
        } nullable
            ? nullable.TypeArguments[0] as INamedTypeSymbol
            : Kind == AbiTypeKind.ObjectArray &&
                SourceType is IArrayTypeSymbol
                {
                    ElementType: INamedTypeSymbol elementType
                }
                ? elementType
            : IsHandle
                ? SourceType as INamedTypeSymbol
                : null;

    public INamedTypeSymbol? CollectionElementType =>
        Kind == AbiTypeKind.ObjectCollection &&
        SourceType is INamedTypeSymbol
        {
            TypeArguments: [INamedTypeSymbol elementType]
        }
            ? elementType
            : null;

    public string InventoryName =>
        IsSupported
            ? $"{Kind}:{AbiType}{(IsNullable ? "?" : string.Empty)}"
            : $"Unsupported:{UnsupportedReason}";
}

internal sealed record ParameterProjection(
    IParameterSymbol Symbol,
    AbiTypePlan AbiType);

internal sealed class ProjectedCall
{
    public required IMethodSymbol Symbol { get; init; }

    /// <summary>
    /// The model key: an assembly-qualified documentation comment id. Overload
    /// misassociation is unrepresentable through it, which is the whole reason
    /// it replaced matching on member names.
    /// </summary>
    public required string CanonicalId { get; init; }

    public required string CanonicalSignature { get; init; }

    public required string BaseName { get; init; }

    public required string GeneratedName { get; set; }

    public required ProjectionStrategy Strategy { get; init; }

    public required string? UnsupportedReason { get; init; }

    public required string? OverrideReason { get; init; }

    public required AbiTypePlan? Receiver { get; init; }

    public required AbiTypePlan ReturnValue { get; init; }

    public required IReadOnlyList<ParameterProjection> Parameters { get; init; }

    public VtblProjection? Vtbl { get; set; }

    public VtblProjection? ContainingInstanceVtbl
    {
        get;
        set;
    }

    public bool IsSupported => Strategy != ProjectionStrategy.Unsupported;

    public bool HasReceiver => Receiver is not null;

    /// <summary>
    /// The shape this call takes on the wire, independent of its C# signature.
    /// Two calls with the same wire signature are interchangeable to the ABI,
    /// which is what makes symmetry between the two sides checkable.
    /// </summary>
    public string WireSignature =>
        $"{Receiver?.InventoryName ?? "static"}" +
        $"({string.Join(",", Parameters.Select(parameter => parameter.AbiType.InventoryName))})" +
        $"->{ReturnValue.InventoryName}";
}

internal sealed class VtblProjection
{
    public required INamedTypeSymbol FacadeType { get; init; }

    public required bool IsTypeVtbl { get; init; }

    public required string Name { get; init; }

    public required string FactoryMethodName { get; init; }

    public required Guid VtblId { get; init; }

    public required IReadOnlyList<ProjectedCall> Members { get; init; }

    public VtblProjection? BaseVtbl { get; set; }
}

internal sealed class MemberProjection
{
    public required ISymbol Symbol { get; init; }

    public required string CanonicalId { get; init; }

    public required string CanonicalSignature { get; init; }

    public required IReadOnlyList<ProjectedCall> Calls { get; init; }

    public bool IsSupported =>
        Calls.Count > 0 &&
        Calls.All(call => call.IsSupported);

    public string? UnsupportedReason => Calls
        .FirstOrDefault(call => !call.IsSupported)
        ?.UnsupportedReason;
}

/// <summary>
/// One projected type's model entry: what owns it, what shape it takes, and how
/// it is reached across the boundary.
/// </summary>
internal sealed class TypeProjection
{
    public required INamedTypeSymbol Symbol { get; init; }

    public required string CanonicalId { get; init; }

    public required TypeOwnership Ownership { get; init; }

    /// <summary>
    /// Null when the ownership was derived rather than declared. Step 3 makes
    /// every type carry a declared one.
    /// </summary>
    public required string? OwnershipReason { get; init; }

    public required string Shape { get; init; }

    public required bool RequiresProxy { get; init; }

    public required bool UsesDynamicInterfaceProxy { get; init; }

    public VtblProjection? InstanceVtbl { get; set; }

    public VtblProjection? TypeVtbl { get; set; }

    /// <summary>
    /// The edge this type was reached by from the analyzer-facing roots, or
    /// null when nothing an analyzer can hold leads to it.
    /// </summary>
    public string? ReachedBy { get; set; }

    public bool IsReachable => ReachedBy is not null;
}

internal sealed class ProjectionModel
{
    private readonly Dictionary<string, MemberProjection>
        _membersBySignature;
    private readonly HashSet<INamedTypeSymbol> _proxyTypes;
    private readonly Dictionary<INamedTypeSymbol, VtblProjection>
        _instanceVtbls;
    private readonly Dictionary<ISymbol, TypeProjection> _typesBySymbol;

    private ProjectionModel(
        IReadOnlyList<IAssemblySymbol> assemblies,
        IReadOnlyList<MemberProjection> members,
        IReadOnlyList<ProjectedCall> calls,
        HashSet<INamedTypeSymbol> proxyTypes,
        IReadOnlyList<VtblProjection> vtbls)
    {
        Assemblies = assemblies;
        Members = members;
        Calls = calls;
        Vtbls = vtbls;
        _proxyTypes = proxyTypes;
        _instanceVtbls =
            new Dictionary<INamedTypeSymbol, VtblProjection>(
                SymbolEqualityComparer.Default);
        foreach (VtblProjection vtbl in vtbls
            .Where(vtbl => !vtbl.IsTypeVtbl))
        {
            _instanceVtbls.Add(
                vtbl.FacadeType,
                vtbl);
        }
        _membersBySignature = members.ToDictionary(
            member => member.CanonicalSignature,
            StringComparer.Ordinal);
        Types = CreateTypes(proxyTypes, vtbls);
        ProjectionClosure.Compute(Types, calls);
        _typesBySymbol = Types.ToDictionary(
            type => (ISymbol)type.Symbol,
            SymbolEqualityComparer.Default);

        string identityInput = string.Join(
            "\n",
            assemblies
                .OrderBy(
                    assembly => assembly.Identity.ToString(),
                    StringComparer.Ordinal)
                .Select(assembly => assembly.Identity.ToString())
                .Concat(calls
                    .OrderBy(
                        operation => operation.CanonicalId,
                        StringComparer.Ordinal)
                    .Select(operation =>
                        $"{operation.CanonicalId}|" +
                        $"{operation.GeneratedName}|" +
                        $"{operation.Vtbl?.Name}|" +
                        $"{operation.Strategy}|" +
                        $"{operation.WireSignature}|" +
                        $"{operation.UnsupportedReason}|" +
                        $"{operation.OverrideReason}"))
                .Concat(Types.Select(type =>
                    $"{type.CanonicalId}|" +
                    $"{type.Ownership}|" +
                    $"{type.Shape}|" +
                    $"{type.RequiresProxy}|" +
                    $"{type.UsesDynamicInterfaceProxy}")));
        byte[] modelHash =
            SHA256.HashData(Encoding.UTF8.GetBytes(identityInput));
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                "RoslynAot.RoslynProjection.v2|" +
                Convert.ToHexString(modelHash).ToLowerInvariant()));
        Identity = Convert.ToHexString(hash).ToLowerInvariant();
        IdentityLow = BitConverter.ToInt64(hash, 0);
        IdentityHigh = BitConverter.ToInt64(hash, 8);
        ControlVtblId = CreateVtblId(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    "RoslynAot.RoslynControlVtbl.v2")));
    }

    public IReadOnlyList<IAssemblySymbol> Assemblies { get; }

    public IReadOnlyList<MemberProjection> Members { get; }

    public IReadOnlyList<ProjectedCall> Calls { get; }

    public IReadOnlyList<VtblProjection> Vtbls { get; }

    public IReadOnlyList<TypeProjection> Types { get; }

    public string Identity { get; }

    public long IdentityLow { get; }

    public long IdentityHigh { get; }

    public Guid ControlVtblId { get; }

    public IEnumerable<ProjectedCall> SupportedCalls =>
        Calls.Where(call => call.IsSupported)
            .OrderBy(
                call => call.CanonicalSignature,
                StringComparer.Ordinal);

    public bool TryGetMember(
        ISymbol symbol,
        out MemberProjection projection) =>
        _membersBySignature.TryGetValue(
            CanonicalSignatureBuilder.GetMemberSignature(symbol),
            out projection!);

    private static IReadOnlyList<TypeProjection> CreateTypes(
        HashSet<INamedTypeSymbol> proxyTypes,
        IReadOnlyList<VtblProjection> vtbls)
    {
        var byType = new Dictionary<INamedTypeSymbol, TypeProjection>(
            SymbolEqualityComparer.Default);
        foreach (INamedTypeSymbol type in proxyTypes
            .Concat(vtbls.Select(vtbl => vtbl.FacadeType)))
        {
            if (byType.ContainsKey(type))
            {
                continue;
            }

            string canonicalId =
                CanonicalSignatureBuilder.GetCanonicalId(type);
            bool declared = ProjectionTypeOwnership.TryGet(
                canonicalId,
                out TypeOwnershipEntry entry);
            byType.Add(
                type,
                new TypeProjection
                {
                    Symbol = type,
                    CanonicalId = canonicalId,
                    Ownership = declared
                        ? entry.Ownership
                        : DeriveOwnership(type, proxyTypes),
                    OwnershipReason = declared ? entry.Reason : null,
                    Shape = GetShape(type),
                    RequiresProxy = proxyTypes.Contains(type),
                    UsesDynamicInterfaceProxy =
                        proxyTypes.Contains(type) &&
                        IsDynamicInterfaceProxyCandidate(type),
                });
        }

        foreach (VtblProjection vtbl in vtbls)
        {
            TypeProjection projection = byType[vtbl.FacadeType];
            if (vtbl.IsTypeVtbl)
            {
                projection.TypeVtbl = vtbl;
            }
            else
            {
                projection.InstanceVtbl = vtbl;
            }
        }

        return
        [
            .. byType.Values.OrderBy(
                type => type.CanonicalId,
                StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The default when no ownership is declared. Derivation is a Step 2
    /// convenience, not the end state: Step 3 requires every type to declare.
    /// </summary>
    private static TypeOwnership DeriveOwnership(
        INamedTypeSymbol type,
        HashSet<INamedTypeSymbol> proxyTypes) =>
        type.IsStatic
            ? TypeOwnership.Facade
            : type.IsValueType && proxyTypes.Contains(type)
                ? TypeOwnership.Value
                : TypeOwnership.Remote;

    private static string GetShape(INamedTypeSymbol type) =>
        type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => type.IsStatic
                ? "static class"
                : type.IsAbstract
                    ? "abstract class"
                    : type.IsSealed
                        ? "sealed class"
                        : "class",
        };

    /// <summary>
    /// Whether an analyzer can reach the type this call is declared on. Today
    /// this is reported, not enforced: withdrawing the unreachable set is a
    /// behavior change the differential corpus has to clear first.
    /// </summary>
    public bool IsReachable(ProjectedCall call) =>
        _typesBySymbol.TryGetValue(
            call.Symbol.ContainingType,
            out TypeProjection? type) &&
        type.IsReachable;

    public bool RequiresProxy(INamedTypeSymbol type) =>
        _proxyTypes.Contains(type);

    public bool UsesDynamicInterfaceProxy(INamedTypeSymbol type)
        => _proxyTypes.Contains(type) &&
            IsDynamicInterfaceProxyCandidate(type);

    internal static bool IsDynamicInterfaceProxyCandidate(
        INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface)
        {
            return !type.IsGenericType;
        }

        if (type.TypeKind != TypeKind.Class ||
            IsAnalyzerLocalClass(type) ||
            type.InstanceConstructors.Any(
                constructor =>
                    IsVisibleAccessibility(
                        constructor.DeclaredAccessibility)) ||
            type.GetMembers()
                .OfType<IMethodSymbol>()
                .Any(
                    method =>
                        method.MethodKind is
                            MethodKind.UserDefinedOperator or
                            MethodKind.Conversion))
        {
            return false;
        }

        INamedTypeSymbol? baseType = type.BaseType;
        return baseType is null ||
            baseType.SpecialType == SpecialType.System_Object ||
            IsDynamicInterfaceProxyCandidate(baseType);
    }

    /// <summary>
    /// A type the analyzer can hold without a handle, so it cannot be reached
    /// through a proxy that assumes one.
    /// </summary>
    private static bool IsAnalyzerLocalClass(INamedTypeSymbol type) =>
        ProjectionTypeOwnership.TryGet(
            CanonicalSignatureBuilder.GetCanonicalId(type),
            out TypeOwnershipEntry entry) &&
        entry.Ownership is TypeOwnership.Local or TypeOwnership.Dual;

    public VtblProjection GetInstanceVtbl(
        INamedTypeSymbol type) =>
        _instanceVtbls.TryGetValue(type, out var vtbl)
            ? vtbl
            : throw new InvalidOperationException(
                $"Type '{type}' has no generated instance vtbl.");

    public static ProjectionModel Create(
        IEnumerable<IAssemblySymbol> assemblySymbols)
    {
        IAssemblySymbol[] assemblies = assemblySymbols
            .OrderBy(
                assembly => assembly.Identity.ToString(),
                StringComparer.Ordinal)
            .ToArray();
        var classifier = new AbiTypeClassifier(assemblies);
        var members = new List<MemberProjection>();
        var calls = new List<ProjectedCall>();

        foreach (IAssemblySymbol assembly in assemblies)
        {
            VisitNamespace(
                assembly.GlobalNamespace,
                classifier,
                members,
                calls);
        }

        AssignCallNames(calls);
        MemberProjection[] orderedMembers = members
            .OrderBy(
                member => member.CanonicalSignature,
                StringComparer.Ordinal)
            .ToArray();
        ProjectedCall[] orderedCalls = calls
            .OrderBy(
                operation => operation.CanonicalSignature,
                StringComparer.Ordinal)
            .ToArray();
        HashSet<INamedTypeSymbol> proxyTypes =
            CollectProxyTypes(assemblies, orderedCalls);
        IReadOnlyList<VtblProjection> vtbls =
            CreateVtbls(proxyTypes, orderedCalls);
        var model = new ProjectionModel(
            assemblies,
            orderedMembers,
            orderedCalls,
            proxyTypes,
            vtbls);
        ProjectionValidation.Validate(model);
        return model;
    }

    private static void VisitNamespace(
        INamespaceSymbol namespaceSymbol,
        AbiTypeClassifier classifier,
        List<MemberProjection> members,
        List<ProjectedCall> calls)
    {
        foreach (INamespaceSymbol childNamespace in namespaceSymbol
            .GetNamespaceMembers()
            .OrderBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            VisitNamespace(
                childNamespace,
                classifier,
                members,
                calls);
        }

        foreach (INamedTypeSymbol type in namespaceSymbol
            .GetTypeMembers()
            .Where(IsVisibleType)
            .OrderBy(
                CanonicalSignatureBuilder.GetTypeName,
                StringComparer.Ordinal))
        {
            VisitType(type, classifier, members, calls);
        }
    }

    private static void VisitType(
        INamedTypeSymbol type,
        AbiTypeClassifier classifier,
        List<MemberProjection> members,
        List<ProjectedCall> calls)
    {
        members.Add(
            new MemberProjection
            {
                Symbol = type,
                CanonicalId = CanonicalSignatureBuilder.GetCanonicalId(type),
                CanonicalSignature =
                    CanonicalSignatureBuilder.GetMemberSignature(type),
                Calls = [],
            });

        foreach (ISymbol symbol in type.GetMembers()
            .Where(IsVisibleMember)
            .OrderBy(
                CanonicalSignatureBuilder.GetMemberSignature,
                StringComparer.Ordinal))
        {
            if (symbol is INamedTypeSymbol nestedType &&
                IsVisibleType(nestedType))
            {
                VisitType(
                    nestedType,
                    classifier,
                    members,
                    calls);
                continue;
            }

            if (symbol is IMethodSymbol method &&
                method.MethodKind is MethodKind.PropertyGet
                    or MethodKind.PropertySet
                    or MethodKind.EventAdd
                    or MethodKind.EventRemove)
            {
                continue;
            }

            IReadOnlyList<ProjectedCall> memberCalls =
                CreateCalls(symbol, classifier);
            members.Add(
                new MemberProjection
                {
                    Symbol = symbol,
                    CanonicalId = CanonicalSignatureBuilder.GetCanonicalId(symbol),
                    CanonicalSignature =
                        CanonicalSignatureBuilder.GetMemberSignature(symbol),
                    Calls = memberCalls,
                });
            calls.AddRange(memberCalls);
        }
    }

    private static IReadOnlyList<ProjectedCall> CreateCalls(
        ISymbol symbol,
        AbiTypeClassifier classifier) =>
        symbol switch
        {
            IMethodSymbol method =>
                [CreateOperation(method, classifier)],
            IPropertySymbol property =>
                new[] { property.GetMethod, property.SetMethod }
                    .Where(method => method is not null)
                    .Select(method => CreateOperation(method!, classifier))
                    .ToArray(),
            IEventSymbol @event =>
                new[] { @event.AddMethod, @event.RemoveMethod }
                    .Where(method => method is not null)
                    .Select(method => CreateOperation(method!, classifier))
                    .ToArray(),
            _ => [],
        };

    private static ProjectedCall CreateOperation(
        IMethodSymbol method,
        AbiTypeClassifier classifier)
    {
        string canonicalId = CanonicalSignatureBuilder.GetCanonicalId(method);
        string canonicalSignature =
            CanonicalSignatureBuilder.GetOperationSignature(method);
        ProjectionStrategy strategy = ClassifyMemberStrategy(method);
        AbiTypePlan? receiver =
            RequiresReceiver(strategy) && !method.IsStatic
            ? classifier.Classify(
                method.ContainingType,
                NullableAnnotation.NotAnnotated,
                AbiTypePosition.Receiver)
            : null;
        ParameterProjection[] parameters = method.Parameters
            .Select(parameter => new ParameterProjection(
                parameter,
                classifier.Classify(
                    parameter.Type,
                    parameter.NullableAnnotation,
                    AbiTypePosition.Parameter)))
            .ToArray();
        AbiTypePlan returnValue =
            strategy == ProjectionStrategy.Constructor
                ? classifier.Classify(
                    method.ContainingType,
                    NullableAnnotation.NotAnnotated,
                    AbiTypePosition.ConstructorReturn)
                : method.ReturnsVoid
                    ? classifier.Classify(
                        method.ReturnType,
                        NullableAnnotation.NotAnnotated,
                        AbiTypePosition.Return)
                    : classifier.Classify(
                        method.ReturnType,
                        method.ReturnNullableAnnotation,
                        AbiTypePosition.Return);
        if (returnValue.Kind == AbiTypeKind.ObjectHandle &&
            method.GetReturnTypeAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() ==
                "System.Diagnostics.CodeAnalysis.MaybeNullAttribute"))
        {
            returnValue = returnValue with { IsNullable = true };
        }

        string? overrideReason = null;
        ProjectionOverride? projectionOverride = null;
        if (ProjectionOverrides.TryGet(canonicalId, out ProjectionOverride found))
        {
            projectionOverride = found;
            overrideReason = found.Reason;
            if (found.ReturnIsNullable is bool isNullable)
            {
                returnValue = returnValue with { IsNullable = isNullable };
            }

            strategy = found.Strategy ?? strategy;
        }

        string? unsupportedReason =
            strategy == ProjectionStrategy.Unsupported &&
            projectionOverride?.Strategy == ProjectionStrategy.Unsupported
                ? projectionOverride.Reason
                : ValidateOperation(
                    method,
                    strategy,
                    receiver,
                    parameters,
                    returnValue);

        if (unsupportedReason is not null)
        {
            strategy = ProjectionStrategy.Unsupported;
        }

        return new ProjectedCall
        {
            Symbol = method,
            CanonicalId = canonicalId,
            CanonicalSignature = canonicalSignature,
            BaseName = GetOperationBaseName(method),
            GeneratedName = string.Empty,
            Strategy = strategy,
            UnsupportedReason = unsupportedReason,
            OverrideReason = overrideReason,
            Receiver = receiver,
            ReturnValue = returnValue,
            Parameters = parameters,
        };
    }

    private static ProjectionStrategy ClassifyMemberStrategy(
        IMethodSymbol method)
    {
        if (method.MethodKind == MethodKind.Constructor)
        {
            return ProjectionStrategy.Constructor;
        }

        if (method.MethodKind == MethodKind.PropertyGet)
        {
            return ProjectionStrategy.PropertyGet;
        }

        if (method.MethodKind == MethodKind.PropertySet)
        {
            return ProjectionStrategy.PropertySet;
        }

        if (IsDisposePattern(method))
        {
            return ProjectionStrategy.Dispose;
        }

        if (method.MethodKind == MethodKind.Ordinary)
        {
            return method.IsStatic
                ? ProjectionStrategy.StaticMethod
                : ProjectionStrategy.InstanceMethod;
        }

        return ProjectionStrategy.Unsupported;
    }

    private static string? ValidateOperation(
        IMethodSymbol method,
        ProjectionStrategy strategy,
        AbiTypePlan? receiver,
        IReadOnlyList<ParameterProjection> parameters,
        AbiTypePlan returnValue)
    {
        if (strategy == ProjectionStrategy.Unsupported)
        {
            return $"Method kind '{method.MethodKind}' is not supported.";
        }

        if (method.DeclaredAccessibility != Accessibility.Public)
        {
            return "Only public members can be dispatched by RoslynInterop.";
        }

        if (TryGetBlockingAttribute(method, out string? attributeReason) ||
            (method.AssociatedSymbol is not null &&
                TryGetBlockingAttribute(
                    method.AssociatedSymbol,
                    out attributeReason)) ||
            TryGetBlockingAttribute(
                method.ContainingType,
                out attributeReason))
        {
            return attributeReason;
        }

        if ((method.ContainingType.TypeKind == TypeKind.Interface ||
            method.IsAbstract) &&
            !IsDynamicInterfaceProxyCandidate(method.ContainingType))
        {
            return "Declaration-only members have no facade implementation body.";
        }

        if (method.IsGenericMethod)
        {
            return "Generic methods are not supported.";
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return "ref returns are not supported.";
        }

        if (RequiresReceiver(strategy) &&
            !method.IsStatic &&
            method.ContainingType.IsValueType &&
            !method.IsReadOnly)
        {
            return "Mutable facade value receivers are not supported.";
        }

        if (receiver is { IsSupported: false })
        {
            return $"Receiver is unsupported: {receiver.UnsupportedReason}";
        }

        foreach (ParameterProjection parameter in parameters)
        {
            if (parameter.Symbol.RefKind != RefKind.None)
            {
                return "ref, in, and out parameters are not supported.";
            }

            if (!parameter.AbiType.IsSupported)
            {
                return $"Parameter '{parameter.Symbol.Name}' is unsupported: " +
                    parameter.AbiType.UnsupportedReason;
            }
        }

        if (!returnValue.IsSupported)
        {
            return $"Return type is unsupported: " +
                returnValue.UnsupportedReason;
        }

        if (strategy == ProjectionStrategy.Constructor &&
            method.ContainingType.IsAbstract)
        {
            return "Abstract types cannot be constructed.";
        }

        return null;
    }

    private static bool TryGetBlockingAttribute(
        ISymbol symbol,
        out string? reason)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            string? attributeName =
                attribute.AttributeClass?.ToDisplayString();
            switch (attributeName)
            {
                case "System.ObsoleteAttribute":
                    reason =
                        "Obsolete APIs are not projected into compiler dispatch.";
                    return true;
                case "System.Diagnostics.CodeAnalysis.ExperimentalAttribute":
                    reason =
                        "Experimental APIs are not projected into compiler dispatch.";
                    return true;
                case "System.Runtime.Versioning.RequiresPreviewFeaturesAttribute":
                    reason =
                        "Preview APIs are not projected into compiler dispatch.";
                    return true;
            }
        }

        reason = null;
        return false;
    }

    private static bool RequiresReceiver(ProjectionStrategy strategy) =>
        strategy is ProjectionStrategy.InstanceMethod
            or ProjectionStrategy.PropertyGet
            or ProjectionStrategy.PropertySet
            or ProjectionStrategy.Dispose;

    private static bool IsDisposePattern(IMethodSymbol method) =>
        !method.IsStatic &&
        method.Name == nameof(IDisposable.Dispose) &&
        method.Parameters.IsEmpty &&
        method.ReturnsVoid &&
        method.ContainingType.AllInterfaces.Any(
            @interface =>
                CanonicalSignatureBuilder.GetMetadataTypeName(@interface) ==
                "System.IDisposable");

    private static HashSet<INamedTypeSymbol> CollectProxyTypes(
        IReadOnlyList<IAssemblySymbol> assemblies,
        IEnumerable<ProjectedCall> calls)
    {
        var assemblyNames = assemblies
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);
        var proxyTypes = new HashSet<INamedTypeSymbol>(
            SymbolEqualityComparer.Default);

        foreach (ProjectedCall call in calls
            .Where(call => call.IsSupported))
        {
            AddProxyType(call.Receiver?.RemoteType);
            foreach (ParameterProjection parameter in call.Parameters)
            {
                AddProxyType(parameter.AbiType.RemoteType);
            }

            AddProxyType(call.ReturnValue.RemoteType);
            AddProxyType(call.ReturnValue.CollectionElementType);
        }

        INamedTypeSymbol[] polymorphicReturnTypes =
            [.. new HashSet<INamedTypeSymbol>(
                calls
                    .Where(call => call.IsSupported)
                    .Select(call => call.ReturnValue.RemoteType)
                    .OfType<INamedTypeSymbol>()
                    .Where(type =>
                        type.TypeKind == TypeKind.Class &&
                        !type.IsSealed),
                SymbolEqualityComparer.Default)];
        foreach (INamedTypeSymbol candidate in assemblies
            .SelectMany(GetVisibleTypes)
            .Where(IsExternallyReferenceable))
        {
            if (polymorphicReturnTypes.Any(
                    returnType => IsOrDerivesFrom(candidate, returnType)))
            {
                AddProxyType(candidate);
            }
        }

        foreach (INamedTypeSymbol type in proxyTypes.ToArray())
        {
            for (INamedTypeSymbol? baseType = type.BaseType;
                 baseType is not null;
                 baseType = baseType.BaseType)
            {
                AddProxyType(baseType);
            }
        }

        return proxyTypes;

        void AddProxyType(INamedTypeSymbol? type)
        {
            if (type is null ||
                !assemblyNames.Contains(type.ContainingAssembly.Name) ||
                type.TypeKind is not (
                    TypeKind.Class or
                    TypeKind.Struct or
                    TypeKind.Interface) ||
                type.IsStatic ||
                type.IsGenericType ||
                type.IsRefLikeType ||
                HasVisibleInstanceFields(type) &&
                    !IsDynamicInterfaceProxyCandidate(type))
            {
                return;
            }

            proxyTypes.Add(type);
        }
    }

    private static bool HasVisibleInstanceFields(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                break;
            }

            if (current.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field =>
                    !field.IsStatic &&
                    !field.IsConst &&
                    field.DeclaredAccessibility is
                        Accessibility.Public or
                        Accessibility.Protected or
                        Accessibility.ProtectedOrInternal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetVisibleTypes(
        IAssemblySymbol assembly) =>
        GetVisibleTypes(assembly.GlobalNamespace);

    private static IEnumerable<INamedTypeSymbol> GetVisibleTypes(
        INamespaceSymbol namespaceSymbol)
    {
        foreach (INamespaceSymbol childNamespace in
                 namespaceSymbol.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol type in GetVisibleTypes(childNamespace))
            {
                yield return type;
            }
        }

        foreach (INamedTypeSymbol type in namespaceSymbol
            .GetTypeMembers()
            .Where(IsVisibleType))
        {
            foreach (INamedTypeSymbol visibleType in GetVisibleTypes(type))
            {
                yield return visibleType;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetVisibleTypes(
        INamedTypeSymbol type)
    {
        yield return type;
        foreach (INamedTypeSymbol nestedType in type
            .GetTypeMembers()
            .Where(IsVisibleType))
        {
            foreach (INamedTypeSymbol visibleType in
                     GetVisibleTypes(nestedType))
            {
                yield return visibleType;
            }
        }
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

    private static bool IsOrDerivesFrom(
        INamedTypeSymbol type,
        INamedTypeSymbol baseType)
    {
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<VtblProjection>
        CreateVtbls(
            IEnumerable<INamedTypeSymbol> proxyTypes,
            IEnumerable<ProjectedCall> calls)
    {
        var definitions = new Dictionary<string, VtblDefinition>(
            StringComparer.Ordinal);

        foreach (INamedTypeSymbol type in proxyTypes)
        {
            GetOrAdd(type, isTypeVtbl: false);
        }

        foreach (ProjectedCall call in calls
            .Where(call => call.IsSupported))
        {
            bool isTypeVtbl =
                call.Symbol.IsStatic ||
                call.Strategy == ProjectionStrategy.Constructor;
            GetOrAdd(
                    call.Symbol.ContainingType,
                    isTypeVtbl)
                .Members.Add(call);
        }

        foreach (IGrouping<string, VtblDefinition> group in
            definitions.Values.GroupBy(
                definition => definition.BaseName,
                StringComparer.Ordinal))
        {
            VtblDefinition[] ordered = group
                .OrderBy(
                    definition => definition.Key,
                    StringComparer.Ordinal)
                .ToArray();
            foreach (VtblDefinition definition in ordered)
            {
                definition.Name = ordered.Length == 1
                    ? definition.BaseName
                    : $"{definition.BaseName}_" +
                        GetStableSuffix(definition.Key);
            }
        }

        var result = new List<VtblProjection>(
            definitions.Count);
        foreach (VtblDefinition definition in definitions.Values
            .OrderBy(definition => definition.Key, StringComparer.Ordinal))
        {
            ProjectedCall[] vtblMembers =
                definition.Members
                    .OrderBy(
                        operation => operation.CanonicalSignature,
                        StringComparer.Ordinal)
                    .ToArray();
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{definition.Key}|" +
                    string.Join(
                        "\n",
                        vtblMembers.Select(
                            operation =>
                                operation.CanonicalSignature))));
            var projection = new VtblProjection
            {
                FacadeType = definition.Type,
                IsTypeVtbl = definition.IsTypeVtbl,
                Name = definition.Name,
                FactoryMethodName =
                    $"Get{definition.Name[1..]}",
                VtblId = CreateVtblId(hash),
                Members = vtblMembers,
            };
            foreach (ProjectedCall call in vtblMembers)
            {
                call.Vtbl = projection;
            }

            result.Add(projection);
        }

        var instanceVtbls = result
            .Where(vtbl => !vtbl.IsTypeVtbl)
            .ToDictionary(
                vtbl =>
                    $"{vtbl.FacadeType.ContainingAssembly.Name}|" +
                    CanonicalSignatureBuilder.GetMetadataTypeName(
                        vtbl.FacadeType),
                StringComparer.Ordinal);
        foreach (VtblProjection vtbl in result.Where(
            vtbl => !vtbl.IsTypeVtbl))
        {
            INamedTypeSymbol? baseType = vtbl.FacadeType.BaseType;
            if (baseType is null)
            {
                continue;
            }

            string baseKey =
                $"{baseType.ContainingAssembly.Name}|" +
                CanonicalSignatureBuilder.GetMetadataTypeName(baseType);
            if (instanceVtbls.TryGetValue(
                    baseKey,
                    out VtblProjection? baseVtbl))
            {
                vtbl.BaseVtbl = baseVtbl;
            }
        }

        foreach (ProjectedCall call in calls)
        {
            string typeKey =
                $"{call.Symbol.ContainingType.ContainingAssembly.Name}|" +
                CanonicalSignatureBuilder.GetMetadataTypeName(
                    call.Symbol.ContainingType);
            instanceVtbls.TryGetValue(
                typeKey,
                out VtblProjection? instanceVtbl);
            call.ContainingInstanceVtbl = instanceVtbl;
        }

        return result;

        VtblDefinition GetOrAdd(
            INamedTypeSymbol type,
            bool isTypeVtbl)
        {
            string key =
                $"{type.ContainingAssembly.Name}|" +
                $"{CanonicalSignatureBuilder.GetMetadataTypeName(type)}|" +
                $"{isTypeVtbl}";
            if (definitions.TryGetValue(key, out var definition))
            {
                return definition;
            }

            string typeName = string.Concat(
                GetContainingTypes(type).Select(
                    containingType => Sanitize(containingType.Name)));
            definition = new VtblDefinition
            {
                Key = key,
                Type = type,
                IsTypeVtbl = isTypeVtbl,
                BaseName = $"I{typeName}" +
                    (isTypeVtbl && !type.IsStatic
                        ? "TypeVtbl"
                        : "Vtbl"),
            };
            definitions.Add(key, definition);
            return definition;
        }
    }

    private sealed class VtblDefinition
    {
        public required string Key { get; init; }

        public required INamedTypeSymbol Type { get; init; }

        public required bool IsTypeVtbl { get; init; }

        public required string BaseName { get; init; }

        public string Name { get; set; } = string.Empty;

        public List<ProjectedCall> Members { get; } = [];
    }

    private static bool IsVisibleType(INamedTypeSymbol type) =>
        IsVisibleAccessibility(type.DeclaredAccessibility) &&
        !type.IsImplicitlyDeclared;

    private static bool IsVisibleMember(ISymbol symbol) =>
        !symbol.IsImplicitlyDeclared &&
        (IsVisibleAccessibility(symbol.DeclaredAccessibility) ||
            symbol is IMethodSymbol
            {
                MethodKind: MethodKind.ExplicitInterfaceImplementation
            } ||
            symbol is IPropertySymbol
            {
                ExplicitInterfaceImplementations.IsEmpty: false
            });

    private static bool IsVisibleAccessibility(Accessibility accessibility) =>
        accessibility is Accessibility.Public
            or Accessibility.Protected
            or Accessibility.ProtectedOrInternal;

    private static string GetOperationBaseName(IMethodSymbol method)
    {
        var builder = new StringBuilder();
        foreach (INamedTypeSymbol type in GetContainingTypes(
            method.ContainingType))
        {
            if (builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(Sanitize(type.Name));
        }

        builder.Append('_');
        if (method.MethodKind == MethodKind.PropertyGet)
        {
            builder.Append("get_");
            builder.Append(
                Sanitize(method.AssociatedSymbol?.Name ?? method.Name));
        }
        else if (method.MethodKind == MethodKind.PropertySet)
        {
            builder.Append("set_");
            builder.Append(
                Sanitize(method.AssociatedSymbol?.Name ?? method.Name));
        }
        else if (method.MethodKind == MethodKind.EventAdd)
        {
            builder.Append("add_");
            builder.Append(
                Sanitize(method.AssociatedSymbol?.Name ?? method.Name));
        }
        else if (method.MethodKind == MethodKind.EventRemove)
        {
            builder.Append("remove_");
            builder.Append(
                Sanitize(method.AssociatedSymbol?.Name ?? method.Name));
        }
        else if (method.MethodKind == MethodKind.Constructor)
        {
            builder.Append("ctor");
        }
        else
        {
            builder.Append(Sanitize(method.MetadataName));
        }

        return builder.ToString();
    }

    private static IEnumerable<INamedTypeSymbol> GetContainingTypes(
        INamedTypeSymbol type)
    {
        var types = new Stack<INamedTypeSymbol>();
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.ContainingType)
        {
            types.Push(current);
        }

        return types;
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(
                char.IsLetterOrDigit(character) || character == '_'
                    ? character
                    : '_');
        }

        return builder.ToString();
    }

    private static void AssignCallNames(
        IEnumerable<ProjectedCall> calls)
    {
        foreach (IGrouping<string, ProjectedCall> group in calls
            .GroupBy(
                operation => operation.BaseName,
                StringComparer.Ordinal))
        {
            ProjectedCall[] ordered = group
                .OrderBy(
                    operation => operation.CanonicalSignature,
                    StringComparer.Ordinal)
                .ToArray();
            foreach (ProjectedCall operation in ordered)
            {
                operation.GeneratedName = ordered.Length == 1
                    ? operation.BaseName
                    : $"{operation.BaseName}_" +
                        GetStableSuffix(operation.CanonicalSignature);
            }
        }
    }

    private static string GetStableSuffix(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static Guid CreateVtblId(byte[] hash)
    {
        byte[] guidBytes = hash[..16];
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }
}

internal sealed class AbiTypeClassifier
{
    private readonly HashSet<string> _facadeAssemblies;

    public AbiTypeClassifier(IEnumerable<IAssemblySymbol> assemblies)
    {
        _facadeAssemblies = assemblies
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);
    }

    public AbiTypePlan Classify(
        ITypeSymbol type,
        NullableAnnotation nullableAnnotation,
        AbiTypePosition position)
    {
        if (type.SpecialType == SpecialType.System_Void)
        {
            return Supported(AbiTypeKind.Void, "void", type);
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return Supported(AbiTypeKind.Boolean, "int", type);
        }

        string? integralAbiType = GetIntegralAbiType(type.SpecialType);
        if (integralAbiType is not null)
        {
            return Supported(
                AbiTypeKind.Integral,
                integralAbiType,
                type);
        }

        if (type is INamedTypeSymbol
            {
                TypeKind: TypeKind.Enum,
                EnumUnderlyingType: { } underlyingType
            })
        {
            string? enumAbiType =
                GetIntegralAbiType(underlyingType.SpecialType);
            return enumAbiType is null
                ? Unsupported(
                    type,
                    "The enum underlying type is not supported.")
                : Supported(AbiTypeKind.Enum, enumAbiType, type);
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            AbiTypePlan elementPlan = Classify(
                arrayType.ElementType,
                arrayType.ElementNullableAnnotation,
                AbiTypePosition.Parameter);
            if (position == AbiTypePosition.Parameter &&
                arrayType.Rank == 1 &&
                elementPlan.Kind == AbiTypeKind.ObjectHandle)
            {
                return Supported(
                    AbiTypeKind.ObjectArray,
                    "long",
                    type);
            }

            return Unsupported(type, "Arrays are not supported.");
        }

        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
        {
            return Unsupported(
                type,
                "Pointers and function pointers are not supported.");
        }

        if (type.TypeKind == TypeKind.Delegate)
        {
            return Unsupported(type, "Delegates are not supported.");
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return new AbiTypePlan(
                AbiTypeKind.Utf16String,
                "string",
                type,
                nullableAnnotation == NullableAnnotation.Annotated,
                UnsupportedReason: null);
        }

        if (type is ITypeParameterSymbol)
        {
            return Unsupported(
                type,
                "Generic substitutions are not supported.");
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return Unsupported(
                type,
                $"Type category '{type.TypeKind}' is not supported.");
        }

        if (namedType.OriginalDefinition.SpecialType ==
                SpecialType.System_Nullable_T &&
            namedType.TypeArguments is [ITypeSymbol nullableValue])
        {
            AbiTypePlan valuePlan = Classify(
                nullableValue,
                NullableAnnotation.NotAnnotated,
                position);
            return valuePlan.Kind == AbiTypeKind.ValueHandle
                ? new AbiTypePlan(
                    AbiTypeKind.NullableHandle,
                    "long",
                    type,
                    IsNullable: true,
                    UnsupportedReason: null)
                : Unsupported(
                    type,
                    "Only nullable facade value handles are supported.");
        }

        if (namedType.IsGenericType)
        {
            if (position == AbiTypePosition.Return &&
                namedType.TypeArguments is [ITypeSymbol elementType])
            {
                if (elementType.SpecialType == SpecialType.System_String &&
                    IsSupportedCollectionInterface(namedType))
                {
                    return Supported(
                        AbiTypeKind.StringCollection,
                        "long",
                        type);
                }

                AbiTypePlan elementPlan = Classify(
                    elementType,
                    elementType.NullableAnnotation,
                    AbiTypePosition.Return);
                if (elementPlan.Kind == AbiTypeKind.ObjectHandle &&
                    (IsEnumerableInterface(namedType) ||
                        IsProxyableObjectImmutableArray(
                            namedType,
                            elementType)))
                {
                    return Supported(
                        AbiTypeKind.ObjectCollection,
                        "long",
                        type);
                }
            }

            return Unsupported(
                type,
                "Generic substitutions are not supported.");
        }

        if (!_facadeAssemblies.Contains(
                namedType.ContainingAssembly.Name))
        {
            return Unsupported(
                type,
                "The type is not part of a generated facade assembly.");
        }

        if (namedType.TypeKind == TypeKind.Interface)
        {
            return new AbiTypePlan(
                AbiTypeKind.ObjectHandle,
                "long",
                type,
                nullableAnnotation == NullableAnnotation.Annotated,
                UnsupportedReason: null);
        }

        if (HasExternallyVisibleInstanceFields(namedType) &&
            !ProjectionModel.IsDynamicInterfaceProxyCandidate(namedType))
        {
            return Unsupported(
                type,
                "Facade types with externally visible non-const instance " +
                "fields require field-state mirroring.");
        }

        if (namedType.TypeKind == TypeKind.Class)
        {
            return new AbiTypePlan(
                AbiTypeKind.ObjectHandle,
                "long",
                type,
                nullableAnnotation == NullableAnnotation.Annotated,
                UnsupportedReason: null);
        }

        if (namedType.TypeKind == TypeKind.Struct &&
            !namedType.IsRefLikeType)
        {
            return Supported(
                AbiTypeKind.ValueHandle,
                "long",
                type);
        }

        return Unsupported(
            type,
            $"Facade type category '{namedType.TypeKind}' is not supported.");
    }

    private static bool HasExternallyVisibleInstanceFields(
        INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.BaseType)
        {
            if (current.SpecialType == SpecialType.System_Object)
            {
                break;
            }

            if (current.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field =>
                    !field.IsStatic &&
                    !field.IsConst &&
                    field.DeclaredAccessibility is
                        Accessibility.Public or
                        Accessibility.Protected or
                        Accessibility.ProtectedOrInternal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSupportedCollectionInterface(
        INamedTypeSymbol type) =>
        IsEnumerableInterface(type) ||
        type.OriginalDefinition is
        {
            Name: "ICollection",
            Arity: 1,
            ContainingNamespace:
            {
                Name: "Generic",
                ContainingNamespace:
                {
                    Name: "Collections",
                    ContainingNamespace:
                    {
                        Name: "System",
                        ContainingNamespace.IsGlobalNamespace: true
                    }
                }
            }
        };

    private static bool IsProxyableObjectImmutableArray(
        INamedTypeSymbol type,
        ITypeSymbol elementType) =>
        IsImmutableArray(type) &&
        elementType is INamedTypeSymbol elementNamedType &&
        (ProjectionModel.IsDynamicInterfaceProxyCandidate(
            elementNamedType) ||
            CanonicalSignatureBuilder.GetMetadataTypeName(
                elementNamedType) ==
            "Microsoft.CodeAnalysis.AttributeData");

    private static bool IsImmutableArray(
        ITypeSymbol type) =>
        type is INamedTypeSymbol
        {
            OriginalDefinition:
            {
            Name: "ImmutableArray",
            Arity: 1,
            ContainingNamespace:
            {
                Name: "Immutable",
                ContainingNamespace:
                {
                    Name: "Collections",
                    ContainingNamespace:
                    {
                        Name: "System",
                        ContainingNamespace.IsGlobalNamespace: true
                    }
                }
            }
            }
        };

    private static bool IsEnumerableInterface(
        INamedTypeSymbol type) =>
        type.OriginalDefinition is
        {
            Name: "IEnumerable",
            Arity: 1,
            ContainingNamespace:
            {
                Name: "Generic",
                ContainingNamespace:
                {
                    Name: "Collections",
                    ContainingNamespace:
                    {
                        Name: "System",
                        ContainingNamespace.IsGlobalNamespace: true
                    }
                }
            }
        };

    private static AbiTypePlan Supported(
        AbiTypeKind kind,
        string abiType,
        ITypeSymbol sourceType) =>
        new(
            kind,
            abiType,
            sourceType,
            IsNullable: false,
            UnsupportedReason: null);

    private static AbiTypePlan Unsupported(
        ITypeSymbol sourceType,
        string reason) =>
        new(
            AbiTypeKind.Unsupported,
            string.Empty,
            sourceType,
            IsNullable: false,
            UnsupportedReason: reason);

    private static string? GetIntegralAbiType(SpecialType specialType) =>
        specialType switch
        {
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Byte => "byte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 or SpecialType.System_Char => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            _ => null,
        };
}

internal static class CanonicalSignatureBuilder
{
    /// <summary>
    /// The canonical model key. <see cref="DocumentationCommentId"/> already
    /// distinguishes overloads, generic arity, ref-ness, and conversion return
    /// types; the assembly name is prepended because a documentation comment id
    /// is only unique within one assembly.
    /// </summary>
    public static string GetCanonicalId(ISymbol symbol) =>
        $"[{symbol.ContainingAssembly?.Identity.Name}]" +
        (DocumentationCommentId.CreateDeclarationId(symbol) ??
            throw new InvalidOperationException(
                "No documentation comment id exists for " +
                $"'{symbol.ToDisplayString()}'."));

    public static string GetMemberSignature(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol type =>
                $"{type.ContainingAssembly.Identity}::type:" +
                GetTypeName(type),
            IMethodSymbol method => GetOperationSignature(method),
            IPropertySymbol property =>
                $"{property.ContainingAssembly.Identity}::property:" +
                $"{GetTypeName(property.ContainingType)}::" +
                $"{property.MetadataName}" +
                $"({string.Join(",", property.Parameters.Select(GetParameter))})" +
                $":{GetTypeName(property.Type)}",
            IEventSymbol @event =>
                $"{@event.ContainingAssembly.Identity}::event:" +
                $"{GetTypeName(@event.ContainingType)}::" +
                $"{@event.MetadataName}:{GetTypeName(@event.Type)}",
            IFieldSymbol field =>
                $"{field.ContainingAssembly.Identity}::field:" +
                $"{GetTypeName(field.ContainingType)}::" +
                $"{field.MetadataName}:{GetTypeName(field.Type)}",
            _ =>
                $"{symbol.ContainingAssembly?.Identity}::{symbol.Kind}:" +
                symbol.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat),
        };

    public static string GetOperationSignature(IMethodSymbol method) =>
        $"{method.ContainingAssembly.Identity}::method:" +
        $"{GetTypeName(method.ContainingType)}::{method.MetadataName}``" +
        $"{method.Arity}" +
        $"({string.Join(",", method.Parameters.Select(GetParameter))})" +
        $"->{GetTypeName(method.ReturnType)}";

    public static string GetTypeName(ITypeSymbol type) =>
        type switch
        {
            IArrayTypeSymbol array =>
                $"{GetTypeName(array.ElementType)}" +
                $"[{new string(',', array.Rank - 1)}]",
            IPointerTypeSymbol pointer =>
                $"{GetTypeName(pointer.PointedAtType)}*",
            ITypeParameterSymbol parameter =>
                parameter.TypeParameterKind == TypeParameterKind.Method
                    ? $"!!{parameter.Ordinal}"
                    : $"!{parameter.Ordinal}",
            INamedTypeSymbol named => GetNamedTypeName(named),
            IDynamicTypeSymbol => "System.Object",
            IFunctionPointerTypeSymbol functionPointer =>
                functionPointer.Signature.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat),
            _ => type.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat),
        };

    public static string GetMetadataTypeName(INamedTypeSymbol type)
    {
        var containingTypes = new Stack<string>();
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.ContainingType)
        {
            containingTypes.Push(current.MetadataName);
        }

        string namespaceName = type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString() + ".";
        return namespaceName + string.Join("+", containingTypes);
    }

    private static string GetNamedTypeName(INamedTypeSymbol type)
    {
        string metadataName =
            GetMetadataTypeName(type.OriginalDefinition);
        if (type.TypeArguments.IsEmpty)
        {
            return $"[{type.ContainingAssembly.Identity.Name}]" +
                metadataName;
        }

        return $"[{type.ContainingAssembly.Identity.Name}]" +
            $"{metadataName}<" +
            $"{string.Join(",", type.TypeArguments.Select(GetTypeName))}>";
    }

    private static string GetParameter(IParameterSymbol parameter) =>
        $"{parameter.RefKind}:{GetTypeName(parameter.Type)}";
}

internal static class CSharpName
{
    public static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ||
        SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? $"@{name}"
            : name;
}
