using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.DotNet.ApiSymbolExtensions;
using Microsoft.DotNet.ApiSymbolExtensions.Filtering;
using Microsoft.DotNet.GenAPI;

namespace RoslynAot.RoslynFacadeGenerator;

internal sealed class FacadeDeclarationTransform
{
    private const string UnsupportedStatement =
        "throw new global::System.PlatformNotSupportedException(" +
        "\"This Roslyn API is not implemented by RoslynAot.\");";

    private readonly ProjectionModel _model;
    private readonly SyntaxGenerator _syntaxGenerator;
    private readonly ISymbolFilter _symbolFilter = new IncludeAllSymbolFilter();

    public FacadeDeclarationTransform(ProjectionModel model)
    {
        _model = model;
        _syntaxGenerator = SyntaxGenerator.GetGenerator(
            new AdhocWorkspace(),
            LanguageNames.CSharp);
    }

    public SyntaxNode Transform(ISymbol symbol, SyntaxNode declaration)
    {
        symbol = ResolveDeclarationSymbol(symbol, declaration);

        if (symbol is INamedTypeSymbol type)
        {
            SyntaxNode completedDeclaration =
                new SynthesizedBodyRewriter().Visit(declaration)
                ?? declaration;
            return AddProxyMembers(type, completedDeclaration);
        }

        if (!_model.TryGetMember(symbol, out MemberProjection projection))
        {
            return declaration;
        }

        if (declaration is ConversionOperatorDeclarationSyntax
                conversionDeclaration &&
            projection.Symbol is IMethodSymbol conversionSymbol &&
            ConversionUsesDynamicInterface(conversionSymbol))
        {
            string suffix = projection.Calls
                .SingleOrDefault()?
                .GeneratedName ?? "Conversion";
            return ParseMember(
                "private static void " +
                "__RoslynAotOmittedConversion_" +
                $"{suffix}() {{ }}");
        }

        return declaration switch
        {
            ConstructorDeclarationSyntax constructor =>
                RewriteConstructor(constructor, projection),
            MethodDeclarationSyntax method =>
                RewriteMethod(method, projection),
            OperatorDeclarationSyntax @operator =>
                RewriteOperator(@operator, projection),
            ConversionOperatorDeclarationSyntax conversion =>
                RewriteConversion(conversion, projection),
            PropertyDeclarationSyntax property =>
                RewriteProperty(property, projection),
            IndexerDeclarationSyntax indexer =>
                RewriteIndexer(indexer, projection),
            EventDeclarationSyntax @event =>
                RewriteEvent(@event, projection),
            FieldDeclarationSyntax field
                when symbol is IFieldSymbol fieldSymbol =>
                RewriteField(field, fieldSymbol),
            _ => declaration,
        };
    }

    private static ISymbol ResolveDeclarationSymbol(
        ISymbol symbol,
        SyntaxNode declaration)
    {
        if (symbol is not IMethodSymbol method ||
            declaration is not MethodDeclarationSyntax methodDeclaration ||
            method.Parameters.Length ==
                methodDeclaration.ParameterList.Parameters.Count)
        {
            return symbol;
        }

        IMethodSymbol[] candidates = method.ContainingType
            .GetMembers(method.Name)
            .OfType<IMethodSymbol>()
            .Where(candidate =>
                candidate.MethodKind == method.MethodKind &&
                candidate.Parameters.Length ==
                    methodDeclaration.ParameterList.Parameters.Count)
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : symbol;
    }

    private bool ConversionUsesDynamicInterface(
        IMethodSymbol conversion) =>
        conversion.Parameters.Any(
            parameter =>
                parameter.Type is INamedTypeSymbol type &&
                _model.UsesDynamicInterfaceProxy(type)) ||
        conversion.ReturnType is INamedTypeSymbol returnType &&
        _model.UsesDynamicInterfaceProxy(returnType);

    private SyntaxNode AddProxyMembers(
        INamedTypeSymbol type,
        SyntaxNode declaration)
    {
        if (!_model.RequiresProxy(type) ||
            declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return declaration;
        }

        if (_model.UsesDynamicInterfaceProxy(type))
        {
            return AddDynamicInterfaceProxyMembers(
                type,
                typeDeclaration);
        }

        VtblProjection instanceVtbl =
            _model.GetInstanceVtbl(type);
        string vtblType =
            $"global::RoslynAot.Abi.{instanceVtbl.Name}";
        string getVtblExpression =
            "global::RoslynAot.RoslynFacade.RoslynVtblFactory." +
            $"{instanceVtbl.FactoryMethodName}(controlVtbl)";
        string typeName = typeDeclaration.Identifier.ValueText;
        string readonlyModifier = type.IsValueType ? "readonly " : string.Empty;
        string constructorInitializer =
            type.TypeKind == TypeKind.Class &&
            type.BaseType is not null &&
            _model.RequiresProxy(type.BaseType)
                ? " : base(controlVtbl, vtbl, handle)"
                : string.Empty;
        string initializeStruct =
            type.IsValueType ? "this = default; " : string.Empty;
        string getVtblMember = type.IsValueType
            ? $"internal {vtblType} " +
                "__RoslynAotGetVtbl() { " +
                "if (__roslynAotVtbl is not null) " +
                "return __roslynAotVtbl; " +
                "if (__roslynAotHandle == 0) " +
                "return global::RoslynAot.RoslynFacade.RoslynVtblFactory." +
                $"{instanceVtbl.FactoryMethodName}(" +
                "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime.GetCurrentControlVtbl()); " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no vtbl.\"); }"
            : $"internal {vtblType} " +
                "__RoslynAotGetVtbl() => __roslynAotVtbl ?? " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no vtbl.\");";
        string getControlVtblMember = type.IsValueType
            ? "internal global::RoslynAot.Abi.IRoslynControlVtbl " +
                "__RoslynAotGetControlVtbl() { " +
                "if (__roslynAotControlVtbl is not null) " +
                "return __roslynAotControlVtbl; " +
                "if (__roslynAotHandle == 0) " +
                "return global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
                "GetCurrentControlVtbl(); " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no control vtbl.\"); }"
            : "internal global::RoslynAot.Abi.IRoslynControlVtbl " +
                "__RoslynAotGetControlVtbl() => __roslynAotControlVtbl ?? " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no control vtbl.\");";
        var members = new List<MemberDeclarationSyntax>
        {
            ParseMember(
                "private " + readonlyModifier +
                "global::RoslynAot.Abi.IRoslynControlVtbl? " +
                "__roslynAotControlVtbl;"),
            ParseMember(
                $"private {readonlyModifier}{vtblType}? " +
                "__roslynAotVtbl;"),
            ParseMember($"private {readonlyModifier}long __roslynAotHandle;"),
            ParseMember(
                $"internal {typeName}(" +
                "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl, " +
                $"{vtblType} vtbl, " +
                $"long handle){constructorInitializer} {{ " +
                initializeStruct +
                "__roslynAotControlVtbl = controlVtbl ?? throw new " +
                "global::System.ArgumentNullException(nameof(controlVtbl)); " +
                "__roslynAotVtbl = vtbl ?? throw new " +
                "global::System.ArgumentNullException(nameof(vtbl)); " +
                "__roslynAotHandle = handle != 0 ? handle : throw new " +
                "global::System.ArgumentOutOfRangeException(nameof(handle)); }"),
            ParseMember(getVtblMember),
            ParseMember(getControlVtblMember),
            // Migration Step 4 retired control-scoped handle identity: a
            // handle already means one specific object process-wide, so
            // there is nothing left to verify by comparing controlVtbl
            // references here.
            ParseMember(
                "internal long __RoslynAotGetHandle(" +
                "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl) => " +
                "__roslynAotHandle;"),
        };
        if (type.TypeKind == TypeKind.Class &&
            !typeDeclaration.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Any(constructor =>
                    constructor.ParameterList.Parameters.Count == 0))
        {
            members.Insert(
                0,
                ParseMember($"internal {typeName}() {{ }}"));
        }

        string fullyQualifiedType = type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat);
        if (type.IsAbstract)
        {
            ClassDeclarationSyntax proxy = SyntaxFactory.ClassDeclaration(
                    "__RoslynAotProxy")
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.SealedKeyword))
                .AddBaseListTypes(
                    SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName(fullyQualifiedType)))
                .AddMembers(
                    ParseMember(
                        "internal __RoslynAotProxy(" +
                        "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl, " +
                        $"{vtblType} vtbl, " +
                        "long handle) : base(controlVtbl, vtbl, handle) { }"));
            proxy = proxy.AddMembers(
                GetAbstractMembers(type)
                    .Select(CreateProxyOverride)
                    .ToArray());
            members.Add(proxy);
            members.Add(
                ParseMember(
                    $"internal static {fullyQualifiedType} __RoslynAotCreateProxy(" +
                    "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl, " +
                    "long handle) => new __RoslynAotProxy(" +
                    $"controlVtbl, {getVtblExpression}, handle);"));
        }
        else
        {
            members.Add(
                ParseMember(
                    $"internal static {fullyQualifiedType} __RoslynAotCreateProxy(" +
                    "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl, " +
                    $"long handle) => new {typeName}(" +
                    $"controlVtbl, {getVtblExpression}, handle);"));
        }

        return typeDeclaration.AddMembers([.. members]);
    }

    private SyntaxNode AddDynamicInterfaceProxyMembers(
        INamedTypeSymbol type,
        TypeDeclarationSyntax declaration)
    {
        VtblProjection instanceVtbl = _model.GetInstanceVtbl(type);
        string vtblType =
            $"global::RoslynAot.Abi.{instanceVtbl.Name}";
        string fullyQualifiedType = type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat);
        var memberRewriter = new InterfaceMemberRewriter();
        IEnumerable<MemberDeclarationSyntax> sourceMembers =
            type.TypeKind == TypeKind.Interface
                ? RewriteInterfaceMembers(type, declaration.Members)
                : declaration.Members;
        var interfaceDeclaration = SyntaxFactory.InterfaceDeclaration(
                declaration.Identifier)
            .WithAttributeLists(
                FilterInterfaceAttributes(declaration.AttributeLists))
            .WithModifiers(
                new SyntaxTokenList(
                    declaration.Modifiers.Where(
                        modifier =>
                            !modifier.IsKind(SyntaxKind.AbstractKeyword) &&
                            !modifier.IsKind(SyntaxKind.SealedKeyword))))
            .WithTypeParameterList(declaration.TypeParameterList)
            .WithBaseList(
                RewriteInterfaceBaseList(type, declaration.BaseList))
            .WithConstraintClauses(declaration.ConstraintClauses)
            .WithMembers(
                SyntaxFactory.List(
                    sourceMembers
                        .Where(
                            member =>
                                member is not ConstructorDeclarationSyntax &&
                                (member is not FieldDeclarationSyntax field ||
                                    field.Modifiers.Any(
                                        modifier =>
                                            modifier.IsKind(
                                                SyntaxKind.StaticKeyword))))
                        .Select(
                            member =>
                                (MemberDeclarationSyntax)memberRewriter
                                    .Visit(member)!)))
            .AddMembers(
                ParseMember(
                    "private global::RoslynAot.RoslynFacade.RoslynObjectProxy " +
                    "__RoslynAotGetProxy() => " +
                    "(global::RoslynAot.RoslynFacade.RoslynObjectProxy)" +
                    "(global::System.Object)this;"),
                ParseMember(
                    $"public {vtblType} __RoslynAotGetVtbl() => " +
                    "global::RoslynAot.RoslynFacade.RoslynVtblFactory." +
                    $"{instanceVtbl.FactoryMethodName}(" +
                    "__RoslynAotGetControlVtbl());"),
                ParseMember(
                    "public global::RoslynAot.Abi.IRoslynControlVtbl " +
                    "__RoslynAotGetControlVtbl() => " +
                    "__RoslynAotGetProxy().ControlVtbl;"),
                ParseMember(
                    "public long __RoslynAotGetHandle(" +
                    "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl) => " +
                    "__RoslynAotGetProxy().GetHandle(controlVtbl);"),
                ParseMember(
                    $"internal static {fullyQualifiedType} " +
                    "__RoslynAotCreateProxy(" +
                    "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl, " +
                    "long handle) => " +
                    $"({fullyQualifiedType})(global::System.Object)" +
                    "global::RoslynAot.RoslynFacade.RoslynObjectProxy." +
                    "GetOrCreate(controlVtbl, handle);"),
                ParseMember(
                    "[global::System.Runtime.InteropServices." +
                    "DynamicInterfaceCastableImplementation] " +
                    $"[global::System.Runtime.InteropServices.Guid(" +
                    $"\"{instanceVtbl.VtblId:D}\")] " +
                    "internal interface __RoslynAotImplementation : " +
                    $"{fullyQualifiedType} {{ }}"));

        return interfaceDeclaration;
    }

    private IEnumerable<MemberDeclarationSyntax> RewriteInterfaceMembers(
        INamedTypeSymbol type,
        SyntaxList<MemberDeclarationSyntax> members)
    {
        var symbolsByName = type.GetMembers()
            .GroupBy(static symbol => symbol.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => new Queue<ISymbol>(group),
                StringComparer.Ordinal);

        foreach (MemberDeclarationSyntax member in members)
        {
            string? name = member switch
            {
                MethodDeclarationSyntax method =>
                    method.Identifier.ValueText,
                PropertyDeclarationSyntax property =>
                    property.Identifier.ValueText,
                IndexerDeclarationSyntax => "this[]",
                EventDeclarationSyntax @event =>
                    @event.Identifier.ValueText,
                FieldDeclarationSyntax field =>
                    field.Declaration.Variables.FirstOrDefault()
                        ?.Identifier.ValueText,
                _ => null,
            };
            if (name is null ||
                !symbolsByName.TryGetValue(name, out Queue<ISymbol>? symbols) ||
                symbols.Count == 0)
            {
                yield return member;
                continue;
            }

            ISymbol symbol = symbols.Dequeue();
            if (!_model.TryGetMember(symbol, out MemberProjection projection))
            {
                yield return member;
                continue;
            }

            yield return member switch
            {
                MethodDeclarationSyntax method =>
                    RewriteMethod(method, projection),
                PropertyDeclarationSyntax property =>
                    RewriteProperty(property, projection),
                IndexerDeclarationSyntax indexer =>
                    RewriteIndexer(indexer, projection),
                EventDeclarationSyntax @event =>
                    RewriteEvent(@event, projection),
                FieldDeclarationSyntax field
                    when symbol is IFieldSymbol fieldSymbol =>
                    RewriteField(field, fieldSymbol),
                _ => member,
            };
        }
    }

    private static SyntaxList<AttributeListSyntax> FilterInterfaceAttributes(
        SyntaxList<AttributeListSyntax> attributeLists) =>
        SyntaxFactory.List(
            attributeLists
                .Select(
                    list =>
                        list.WithAttributes(
                            SyntaxFactory.SeparatedList(
                                list.Attributes.Where(
                                    attribute =>
                                        !attribute.Name
                                            .ToString()
                                            .EndsWith(
                                                "DebuggerDisplay",
                                                StringComparison.Ordinal)))))
                .Where(list => list.Attributes.Count != 0));

    private static BaseListSyntax? RewriteInterfaceBaseList(
        INamedTypeSymbol type,
        BaseListSyntax? baseList)
    {
        if (baseList is null)
        {
            return null;
        }

        SeparatedSyntaxList<BaseTypeSyntax> types = baseList.Types;
        if (type.BaseType?.SpecialType == SpecialType.System_Object &&
            types.Count != 0)
        {
            types = types.RemoveAt(0);
        }

        return types.Count == 0
            ? null
            : baseList.WithTypes(types);
    }

    private static FieldDeclarationSyntax RewriteField(
        FieldDeclarationSyntax declaration,
        IFieldSymbol symbol)
    {
        if (!symbol.IsStatic || symbol.IsConst)
        {
            return declaration;
        }

        ExpressionSyntax? wellKnownInitializer =
            GetWellKnownFieldInitializer(symbol);
        if (wellKnownInitializer is not null)
        {
            return declaration.WithDeclaration(
                declaration.Declaration.WithVariables(
                    SyntaxFactory.SeparatedList(
                        declaration.Declaration.Variables.Select(
                            variable => variable.WithInitializer(
                                SyntaxFactory.EqualsValueClause(
                                    wellKnownInitializer))))));
        }

        string message =
            $"Static Roslyn field '{symbol.ToDisplayString()}' " +
            "is not implemented by RoslynAot.";
        string fieldType = symbol.Type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat);
        ExpressionSyntax initializer = SyntaxFactory.ParseExpression(
            "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
            $"UnsupportedStaticField<{fieldType}>(" +
            SymbolDisplay.FormatLiteral(message, quote: true) +
            ")");
        return declaration.WithDeclaration(
            declaration.Declaration.WithVariables(
                SyntaxFactory.SeparatedList(
                    declaration.Declaration.Variables.Select(
                        variable => variable.WithInitializer(
                            SyntaxFactory.EqualsValueClause(initializer))))));
    }

    private static ExpressionSyntax? GetWellKnownFieldInitializer(
        IFieldSymbol symbol) =>
        ProjectionOverrides.TryGetFieldInitializer(
            CanonicalSignatureBuilder.GetCanonicalId(symbol),
            out string initializer)
            ? SyntaxFactory.ParseExpression(initializer)
            : null;

    private IEnumerable<ISymbol> GetAbstractMembers(INamedTypeSymbol type)
    {
        var slots = new HashSet<string>(StringComparer.Ordinal);
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers()
                .Where(member =>
                    member.IsAbstract ||
                    member.IsOverride ||
                    member.IsVirtual)
                .Where(member => member.DeclaredAccessibility is
                    Accessibility.Public or
                    Accessibility.Protected or
                    Accessibility.ProtectedOrInternal)
                .Where(member => member is not IMethodSymbol
                {
                    AssociatedSymbol: not null
                })
                .OrderBy(
                    CanonicalSignatureBuilder.GetMemberSignature,
                    StringComparer.Ordinal))
            {
                string slot = GetOverrideSlot(member);
                if (slots.Add(slot) && member.IsAbstract)
                {
                    yield return member;
                }
            }
        }
    }

    private static string GetOverrideSlot(ISymbol member)
    {
        ISymbol root = member;
        switch (member)
        {
            case IMethodSymbol method:
                while (method.OverriddenMethod is { } overriddenMethod)
                {
                    method = overriddenMethod;
                }

                root = method;
                break;
            case IPropertySymbol property:
                while (property.OverriddenProperty is { } overriddenProperty)
                {
                    property = overriddenProperty;
                }

                root = property;
                break;
            case IEventSymbol @event:
                while (@event.OverriddenEvent is { } overriddenEvent)
                {
                    @event = overriddenEvent;
                }

                root = @event;
                break;
        }

        return CanonicalSignatureBuilder.GetMemberSignature(root);
    }

    private MemberDeclarationSyntax CreateProxyOverride(ISymbol symbol)
    {
        SyntaxNode declaration =
            _syntaxGenerator
                .DeclarationExt(symbol, _symbolFilter)
                .AddMemberAttributes(
                    _syntaxGenerator,
                    symbol,
                    _symbolFilter);
        // The override is the only place an abstract member's remoting body can
        // live, so the model has to be consulted here rather than assumed
        // empty. GetBody falls back to the unsupported body on its own.
        MemberProjection? projection =
            _model.TryGetMember(symbol, out MemberProjection found)
                ? found
                : null;
        return declaration switch
        {
            MethodDeclarationSyntax method =>
                RewriteProxyMethod(
                    method,
                    (IMethodSymbol)symbol,
                    projection),
            PropertyDeclarationSyntax property =>
                RewriteProxyProperty(property, projection),
            IndexerDeclarationSyntax indexer =>
                RewriteProxyIndexer(indexer, projection),
            EventDeclarationSyntax @event =>
                RewriteProxyEvent(@event, projection),
            EventFieldDeclarationSyntax eventField =>
                RewriteProxyEventField(eventField, projection),
            _ => throw new InvalidOperationException(
                $"Unsupported abstract proxy member '{symbol}'."),
        };
    }

    private static MethodDeclarationSyntax RewriteProxyMethod(
        MethodDeclarationSyntax declaration,
        IMethodSymbol symbol,
        MemberProjection? projection)
    {
        SyntaxList<TypeParameterConstraintClauseSyntax> constraints =
            SyntaxFactory.List(
                symbol.TypeParameters
                    .Where(parameter =>
                        !parameter.HasReferenceTypeConstraint &&
                        !parameter.HasValueTypeConstraint &&
                        !parameter.HasNotNullConstraint &&
                        !parameter.HasUnmanagedTypeConstraint &&
                        !parameter.HasConstructorConstraint &&
                        parameter.ConstraintTypes.IsEmpty)
                    .Select(parameter =>
                        CreateDefaultConstraint(parameter.Name)));
        return declaration
            .WithModifiers(GetOverrideModifiers(declaration.Modifiers))
            .WithConstraintClauses(constraints)
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(GetBody(projection?.Calls.SingleOrDefault()));
    }

    private static TypeParameterConstraintClauseSyntax
        CreateDefaultConstraint(string typeParameterName)
    {
        var method = (MethodDeclarationSyntax)
            (SyntaxFactory.ParseMemberDeclaration(
                "void M<T>() where T : default { }")
            ?? throw new InvalidOperationException(
                "Could not parse a default generic constraint."));
        return method.ConstraintClauses[0].WithName(
            SyntaxFactory.IdentifierName(typeParameterName));
    }

    private static PropertyDeclarationSyntax RewriteProxyProperty(
        PropertyDeclarationSyntax declaration,
        MemberProjection? projection)
    {
        if (declaration.AccessorList is null)
        {
            return declaration;
        }

        return declaration
            .WithModifiers(GetOverrideModifiers(declaration.Modifiers))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(
                declaration.AccessorList.WithAccessors(
                    SyntaxFactory.List(
                        RewriteAccessors(
                            declaration.AccessorList.Accessors,
                            projection))));
    }

    private static IndexerDeclarationSyntax RewriteProxyIndexer(
        IndexerDeclarationSyntax declaration,
        MemberProjection? projection)
    {
        if (declaration.AccessorList is null)
        {
            return declaration;
        }

        return declaration
            .WithModifiers(GetOverrideModifiers(declaration.Modifiers))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(
                declaration.AccessorList.WithAccessors(
                    SyntaxFactory.List(
                        RewriteAccessors(
                            declaration.AccessorList.Accessors,
                            projection))));
    }

    private static EventDeclarationSyntax RewriteProxyEvent(
        EventDeclarationSyntax declaration,
        MemberProjection? projection)
    {
        if (declaration.AccessorList is null)
        {
            return declaration;
        }

        return declaration
            .WithModifiers(GetOverrideModifiers(declaration.Modifiers))
            .WithAccessorList(
                declaration.AccessorList.WithAccessors(
                    SyntaxFactory.List(
                        RewriteAccessors(
                            declaration.AccessorList.Accessors,
                            projection))));
    }

    private static EventDeclarationSyntax RewriteProxyEventField(
        EventFieldDeclarationSyntax declaration,
        MemberProjection? projection)
    {
        VariableDeclaratorSyntax variable =
            declaration.Declaration.Variables.Single();
        return SyntaxFactory.EventDeclaration(
                declaration.Declaration.Type,
                variable.Identifier)
            .WithAttributeLists(declaration.AttributeLists)
            .WithModifiers(
                GetOverrideModifiers(declaration.Modifiers))
            .WithAccessorList(
                SyntaxFactory.AccessorList(
                    SyntaxFactory.List(
                    [
                        SyntaxFactory.AccessorDeclaration(
                                SyntaxKind.AddAccessorDeclaration)
                            .WithBody(GetUnsupportedBody()),
                        SyntaxFactory.AccessorDeclaration(
                                SyntaxKind.RemoveAccessorDeclaration)
                            .WithBody(GetUnsupportedBody()),
                    ])));
    }

    private static SyntaxTokenList GetOverrideModifiers(
        SyntaxTokenList modifiers)
    {
        var tokens = modifiers
            .Where(token =>
                !token.IsKind(SyntaxKind.AbstractKeyword) &&
                !token.IsKind(SyntaxKind.VirtualKeyword) &&
                !token.IsKind(SyntaxKind.OverrideKeyword))
            .ToList();
        tokens.Add(SyntaxFactory.Token(SyntaxKind.OverrideKeyword));
        return SyntaxFactory.TokenList(tokens);
    }

    private static BlockSyntax GetUnsupportedBody() =>
        SyntaxFactory.Block(
            SyntaxFactory.ParseStatement(UnsupportedStatement));

    private static ConstructorDeclarationSyntax RewriteConstructor(
        ConstructorDeclarationSyntax declaration,
        MemberProjection projection)
    {
        if (declaration.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
            declaration.Body is null)
        {
            return declaration;
        }

        return declaration
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(GetBody(projection.Calls.SingleOrDefault()));
    }

    private MethodDeclarationSyntax RewriteMethod(
        MethodDeclarationSyntax declaration,
        MemberProjection projection)
    {
        bool dynamicInterfaceMember =
            projection.Symbol.ContainingType is INamedTypeSymbol type &&
            _model.UsesDynamicInterfaceProxy(type);
        if ((declaration.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
                declaration.Body is null) &&
            !dynamicInterfaceMember)
        {
            return declaration;
        }

        return declaration
            .WithModifiers(
                RemoveModifiers(
                    declaration.Modifiers,
                    SyntaxKind.AbstractKeyword))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(GetBody(projection.Calls.SingleOrDefault()));
    }

    private static OperatorDeclarationSyntax RewriteOperator(
        OperatorDeclarationSyntax declaration,
        MemberProjection projection) =>
        declaration
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(GetBody(projection.Calls.SingleOrDefault()));

    private static ConversionOperatorDeclarationSyntax RewriteConversion(
        ConversionOperatorDeclarationSyntax declaration,
        MemberProjection projection) =>
        declaration
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(GetBody(projection.Calls.SingleOrDefault()));

    private PropertyDeclarationSyntax RewriteProperty(
        PropertyDeclarationSyntax declaration,
        MemberProjection projection)
    {
        bool dynamicInterfaceMember =
            projection.Symbol.ContainingType is INamedTypeSymbol type &&
            _model.UsesDynamicInterfaceProxy(type);
        if (declaration.AccessorList is null ||
            projection.Symbol is IPropertySymbol { IsAbstract: true } &&
            !dynamicInterfaceMember)
        {
            return declaration;
        }

        return declaration
            .WithModifiers(
                RemoveModifiers(
                    declaration.Modifiers,
                    SyntaxKind.AbstractKeyword))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(
                declaration.AccessorList.WithAccessors(
                    RewriteAccessors(
                        declaration.AccessorList.Accessors,
                        projection)));
    }

    private IndexerDeclarationSyntax RewriteIndexer(
        IndexerDeclarationSyntax declaration,
        MemberProjection projection)
    {
        bool dynamicInterfaceMember =
            projection.Symbol.ContainingType is INamedTypeSymbol type &&
            _model.UsesDynamicInterfaceProxy(type);
        if (declaration.AccessorList is null ||
            projection.Symbol is IPropertySymbol { IsAbstract: true } &&
            !dynamicInterfaceMember)
        {
            return declaration;
        }

        return declaration
            .WithModifiers(
                RemoveModifiers(
                    declaration.Modifiers,
                    SyntaxKind.AbstractKeyword))
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithAccessorList(
                declaration.AccessorList.WithAccessors(
                    RewriteAccessors(
                        declaration.AccessorList.Accessors,
                        projection)));
    }

    private EventDeclarationSyntax RewriteEvent(
        EventDeclarationSyntax declaration,
        MemberProjection projection)
    {
        bool dynamicInterfaceMember =
            projection.Symbol.ContainingType is INamedTypeSymbol type &&
            _model.UsesDynamicInterfaceProxy(type);
        if (declaration.AccessorList is null ||
            projection.Symbol is IEventSymbol { IsAbstract: true } &&
            !dynamicInterfaceMember)
        {
            return declaration;
        }

        return declaration
            .WithModifiers(
                RemoveModifiers(
                    declaration.Modifiers,
                    SyntaxKind.AbstractKeyword))
            .WithAccessorList(
                declaration.AccessorList.WithAccessors(
                    RewriteAccessors(
                        declaration.AccessorList.Accessors,
                        projection)));
    }

    private static SyntaxTokenList RemoveModifiers(
        SyntaxTokenList modifiers,
        params SyntaxKind[] kinds) =>
        new(
            modifiers.Where(
                modifier =>
                    !kinds.Contains(modifier.Kind())));

    private static SyntaxList<AccessorDeclarationSyntax> RewriteAccessors(
        SyntaxList<AccessorDeclarationSyntax> accessors,
        MemberProjection? projection)
    {
        var rewritten = new List<AccessorDeclarationSyntax>(accessors.Count);
        foreach (AccessorDeclarationSyntax accessor in accessors)
        {
            MethodKind methodKind = accessor.Kind() switch
            {
                SyntaxKind.GetAccessorDeclaration => MethodKind.PropertyGet,
                SyntaxKind.SetAccessorDeclaration or
                    SyntaxKind.InitAccessorDeclaration => MethodKind.PropertySet,
                SyntaxKind.AddAccessorDeclaration => MethodKind.EventAdd,
                SyntaxKind.RemoveAccessorDeclaration => MethodKind.EventRemove,
                _ => MethodKind.Ordinary,
            };
            ProjectedCall? operation = projection?.Calls
                .FirstOrDefault(candidate =>
                    candidate.Symbol.MethodKind == methodKind);

            rewritten.Add(
                accessor
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(GetBody(operation)));
        }

        return SyntaxFactory.List(rewritten);
    }

    private static BlockSyntax GetBody(ProjectedCall? operation)
    {
        if (operation is not null &&
            AnalyzerLocalFacadeEmitter.TryGetStatements(
                operation,
                out IReadOnlyList<string> localStatements))
        {
            return SyntaxFactory.Block(
                localStatements.Select(
                    statement => SyntaxFactory.ParseStatement(statement)));
        }

        if (operation is null || !operation.IsSupported)
        {
            return SyntaxFactory.Block(
                SyntaxFactory.ParseStatement(UnsupportedStatement));
        }

        return SyntaxFactory.Block(
            FacadeBodyEmitter.GetStatements(operation)
                .Select(statement => SyntaxFactory.ParseStatement(statement)));
    }

    private static MemberDeclarationSyntax ParseMember(string source) =>
        SyntaxFactory.ParseMemberDeclaration(source)
        ?? throw new InvalidOperationException(
            $"Could not parse generated facade member: {source}");

    private sealed class IncludeAllSymbolFilter : ISymbolFilter
    {
        public bool Include(ISymbol symbol) => true;
    }

    private sealed class InterfaceMemberRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(
            MethodDeclarationSyntax node)
        {
            MethodDeclarationSyntax rewritten =
                (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            return rewritten
                .WithModifiers(
                    SealGenericMethod(
                        RewriteModifiers(node.Modifiers),
                        node))
                .WithConstraintClauses(
                    SyntaxFactory.List(
                        rewritten.ConstraintClauses
                            .Select(
                                clause =>
                                    clause.WithConstraints(
                                        SyntaxFactory.SeparatedList(
                                            clause.Constraints.Where(
                                                constraint =>
                                                    constraint
                                                        .ToString() !=
                                                    "default"))))
                            .Where(
                                clause =>
                                    clause.Constraints.Count != 0)));
        }

        public override SyntaxNode? VisitPropertyDeclaration(
            PropertyDeclarationSyntax node) =>
            ((PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!)
                .WithModifiers(RewriteModifiers(node.Modifiers));

        public override SyntaxNode? VisitIndexerDeclaration(
            IndexerDeclarationSyntax node) =>
            ((IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!)
                .WithModifiers(RewriteModifiers(node.Modifiers));

        public override SyntaxNode? VisitEventDeclaration(
            EventDeclarationSyntax node) =>
            ((EventDeclarationSyntax)base.VisitEventDeclaration(node)!)
                .WithModifiers(RewriteModifiers(node.Modifiers));

        public override SyntaxNode? VisitOperatorDeclaration(
            OperatorDeclarationSyntax node) =>
            ((OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!)
                .WithModifiers(RewriteModifiers(node.Modifiers));

        public override SyntaxNode? VisitConversionOperatorDeclaration(
            ConversionOperatorDeclarationSyntax node) =>
            ((ConversionOperatorDeclarationSyntax)base
                .VisitConversionOperatorDeclaration(node)!)
                .WithModifiers(RewriteModifiers(node.Modifiers));

        /// <summary>
        /// Makes a generic interface method non-virtual, which is what keeps it
        /// off the generic virtual dispatch path.
        /// </summary>
        /// <remarks>
        /// A generic virtual method cannot be dispatched through
        /// <c>IDynamicInterfaceCastable</c>: the runtime looks for a GVM slot
        /// mapping on the concrete target, <c>RoslynObjectProxy</c> does not
        /// statically implement the interface, and the type loader *fails
        /// fast* — killing the compiler and every other analyzer's diagnostics
        /// rather than raising AD0001. Sealing the member means the call
        /// resolves directly to this body instead, so the same unsupported
        /// member throws an exception an analyzer host can catch.
        ///
        /// This is safe precisely because no generic method is projected today
        /// — they are all unsupported and all carry a throwing body. Giving one
        /// a real implementation means giving it a statically implemented shim
        /// on the proxy to forward to; see docs/GENERIC-VIRTUAL-DISPATCH.md.
        /// </remarks>
        private static SyntaxTokenList SealGenericMethod(
            SyntaxTokenList modifiers,
            MethodDeclarationSyntax declaration) =>
            declaration.TypeParameterList is { Parameters.Count: > 0 } &&
            declaration.Body is not null &&
            !modifiers.Any(SyntaxKind.StaticKeyword)
                ? modifiers.Add(
                    SyntaxFactory.Token(SyntaxKind.SealedKeyword))
                : modifiers;

        private static SyntaxTokenList RewriteModifiers(
            SyntaxTokenList modifiers) =>
            RemoveModifiers(
                modifiers,
                SyntaxKind.AbstractKeyword,
                SyntaxKind.VirtualKeyword,
                SyntaxKind.OverrideKeyword,
                SyntaxKind.SealedKeyword);
    }

    private sealed class SynthesizedBodyRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitConstructorDeclaration(
            ConstructorDeclarationSyntax node)
        {
            ConstructorDeclarationSyntax rewritten =
                (ConstructorDeclarationSyntax)
                (base.VisitConstructorDeclaration(node) ?? node);
            return rewritten.Body is null &&
                rewritten.ExpressionBody is null
                    ? rewritten.WithBody(SyntaxFactory.Block())
                    : rewritten;
        }
    }
}

internal static class FacadeBodyEmitter
{
    public static IEnumerable<string> GetStatements(
        ProjectedCall operation)
    {
        IMethodSymbol method = operation.Symbol;
        if (operation.Strategy == ProjectionStrategy.Constructor &&
            method.ContainingType.IsValueType)
        {
            yield return "this = default;";
        }

        VtblProjection vtbl = operation.Vtbl
            ?? throw new InvalidOperationException(
                $"Supported member '{operation.GeneratedName}' has no vtbl.");
        string vtblType =
            $"global::RoslynAot.Abi.{vtbl.Name}";
        string controlVtblExpression = operation.HasReceiver
            ? "__RoslynAotGetControlVtbl()"
            : "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
                "GetCurrentControlVtbl()";
        yield return
            "global::RoslynAot.Abi.IRoslynControlVtbl controlVtbl = " +
            $"{controlVtblExpression};";
        string vtblExpression = operation.HasReceiver
            ? "__RoslynAotGetVtbl()"
            : "global::RoslynAot.RoslynFacade.RoslynVtblFactory." +
                $"{vtbl.FactoryMethodName}(" +
                "controlVtbl)";
        yield return
            $"{vtblType} vtbl = " +
            $"{vtblExpression};";

        var arguments = new List<string>();
        if (operation.HasReceiver)
        {
            if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
            {
                yield return
                    $"{operation.Receiver!.AbiType} __roslynAotReceiver = " +
                    "__RoslynAotGetHandle(controlVtbl);";
                arguments.Add("__roslynAotReceiver");
            }
            else
            {
                arguments.Add("__RoslynAotGetHandle(controlVtbl)");
            }
        }

        arguments.AddRange(operation.Parameters.Select(
            parameter => GetFacadeArgument(parameter, "controlVtbl")));

        string invocationPrefix =
            $"vtbl.{operation.GeneratedName}(";
        if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
        {
            arguments.Add("buffer");
            arguments.Add("bufferLength");
            arguments.Add("out requiredLength");
            string nullableSuppression =
                operation.ReturnValue.IsNullable ? string.Empty : "!";
            yield return
                "return global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
                "ReadUtf16String(controlVtbl, " +
                "(nint buffer, int bufferLength, out int requiredLength) => " +
                $"{invocationPrefix}{string.Join(", ", arguments)}))" +
                nullableSuppression + ";";
            yield break;
        }

        if (operation.ReturnValue.Kind == AbiTypeKind.Void)
        {
            yield return
                $"int status = {invocationPrefix}{string.Join(", ", arguments)});";
            yield return
                "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
                "ThrowIfFailed(controlVtbl, status);";
            yield break;
        }

        string resultType = operation.ReturnValue.AbiType;
        arguments.Add($"out {resultType} result");
        yield return
            $"int status = {invocationPrefix}{string.Join(", ", arguments)});";
        yield return
            "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
            "ThrowIfFailed(controlVtbl, status);";
        if (operation.Strategy == ProjectionStrategy.Constructor)
        {
            VtblProjection instanceVtbl =
                operation.ContainingInstanceVtbl
                ?? throw new InvalidOperationException(
                    $"Constructor '{operation.GeneratedName}' has no instance vtbl.");
            yield return
                "__roslynAotControlVtbl = controlVtbl;";
            yield return
                "__roslynAotVtbl = " +
                "global::RoslynAot.RoslynFacade.RoslynVtblFactory." +
                $"{instanceVtbl.FactoryMethodName}(controlVtbl);";
            yield return "__roslynAotHandle = result;";
            yield break;
        }

        yield return $"return {GetFacadeResult(operation.ReturnValue, "result")};";
    }

    private static string GetFacadeArgument(
        ParameterProjection parameter,
        string controlVtbl)
    {
        string name = CSharpName.EscapeIdentifier(
            parameter.Symbol.Name);
        return
        parameter.AbiType.Kind switch
        {
            AbiTypeKind.Boolean =>
                $"{name} ? 1 : 0",
            AbiTypeKind.Enum =>
                $"({parameter.AbiType.AbiType}){name}",
            AbiTypeKind.Integral
                when parameter.Symbol.Type.SpecialType ==
                    SpecialType.System_Char =>
                $"(ushort){name}",
            AbiTypeKind.Integral =>
                name,
            AbiTypeKind.ObjectHandle =>
                parameter.AbiType.IsNullable
                    ? $"{name} is null ? 0L : " +
                        $"{name}.__RoslynAotGetHandle({controlVtbl})"
                    : $"{name}.__RoslynAotGetHandle({controlVtbl})",
            AbiTypeKind.ValueHandle =>
                $"{name}.__RoslynAotGetHandle({controlVtbl})",
            AbiTypeKind.NullableHandle =>
                $"{name}.HasValue ? " +
                $"{name}.Value.__RoslynAotGetHandle({controlVtbl}) : 0L",
            AbiTypeKind.ObjectArray =>
                "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
                $"CreateObjectCollectionHandle({controlVtbl}, " +
                $"global::System.Array.ConvertAll({name}, " +
                $"item => item.__RoslynAotGetHandle({controlVtbl})))",
            AbiTypeKind.Utf16String => name,
            _ => throw new InvalidOperationException(
                $"No facade argument conversion exists for " +
                $"'{parameter.Symbol.ToDisplayString()}'."),
        };
    }

    private static string GetFacadeResult(
        AbiTypePlan result,
        string expression) =>
        result.Kind switch
        {
            AbiTypeKind.Boolean =>
                $"{expression} != 0",
            AbiTypeKind.Enum =>
                $"({result.SourceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})" +
                expression,
            AbiTypeKind.Integral
                when result.SourceType.SpecialType ==
                    SpecialType.System_Char =>
                $"(char){expression}",
            AbiTypeKind.Integral => expression,
            AbiTypeKind.StringCollection =>
                WrapCollectionResult(
                    result,
                    "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
                    $"ReadStringCollection(controlVtbl, {expression})"),
            AbiTypeKind.ObjectCollection =>
                GetObjectCollectionCreation(result, expression),
            AbiTypeKind.ObjectHandle =>
                result.IsNullable
                    ? $"{expression} == 0 ? null : " +
                        GetProxyCreation(result, expression)
                    : GetProxyCreation(result, expression),
            AbiTypeKind.ValueHandle =>
                GetProxyCreation(result, expression),
            AbiTypeKind.NullableHandle =>
                $"{expression} == 0 ? null : " +
                GetProxyCreation(result, expression),
            _ => throw new InvalidOperationException(
                $"No facade result conversion exists for " +
                $"'{result.SourceType.ToDisplayString()}'."),
        };

    private static string GetObjectCollectionCreation(
        AbiTypePlan plan,
        string expression)
    {
        if (plan.SourceType is not INamedTypeSymbol
            {
                TypeArguments: [INamedTypeSymbol elementType]
            })
        {
            throw new InvalidOperationException(
                $"Collection ABI plan '{plan.SourceType}' has no element type.");
        }

        var elementPlan = new AbiTypePlan(
            AbiTypeKind.ObjectHandle,
            "long",
            elementType,
            elementType.NullableAnnotation == NullableAnnotation.Annotated,
            UnsupportedReason: null);
        string typeName = elementType.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat);
        string collection =
            "global::RoslynAot.RoslynFacade.RoslynFacadeRuntime." +
            $"ReadObjectCollection<{typeName}>(" +
            $"controlVtbl, {expression}, " +
            $"static (controlVtbl, handle) => " +
            $"{GetProxyCreation(elementPlan, "handle")})";
        return WrapCollectionResult(plan, collection);
    }

    private static string WrapCollectionResult(
        AbiTypePlan plan,
        string expression) =>
        plan.SourceType is INamedTypeSymbol
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
        }
            ? "global::System.Collections.Immutable.ImmutableArray." +
                $"CreateRange({expression})"
            : expression;

    private static string GetProxyCreation(
        AbiTypePlan plan,
        string expression)
    {
        INamedTypeSymbol remoteType = plan.RemoteType
            ?? throw new InvalidOperationException(
                $"ABI plan '{plan.Kind}' has no remote type.");
        string typeName = remoteType.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{typeName}." +
            $"__RoslynAotCreateProxy(controlVtbl, {expression})";
    }
}

internal static class AnalyzerLocalFacadeEmitter
{
    public static bool TryGetStatements(
        ProjectedCall operation,
        out IReadOnlyList<string> statements)
    {
        if (!ProjectionOverrides.TryGet(
                operation.CanonicalId,
                out ProjectionOverride entry) ||
            entry.LocalStatements is null)
        {
            statements = [];
            return false;
        }

        statements = entry.RemoteFallback
            ? [.. entry.LocalStatements, .. RemoteStatements(operation)]
            : entry.LocalStatements;
        return true;
    }

    private static IReadOnlyList<string> RemoteStatements(
        ProjectedCall operation) =>
        operation.IsSupported
            ? [.. FacadeBodyEmitter.GetStatements(operation)]
            :
            [
                "throw new global::System.PlatformNotSupportedException(\"This Roslyn API is not implemented by RoslynAot.\");",
            ];
}
