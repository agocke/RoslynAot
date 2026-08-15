using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.DotNet.ApiSymbolExtensions;
using Microsoft.DotNet.ApiSymbolExtensions.Filtering;
using Microsoft.DotNet.GenAPI;

namespace AnalyzeAot.RoslynFacadeGenerator;

internal sealed class FacadeDeclarationTransform
{
    private const string UnsupportedStatement =
        "throw new global::System.PlatformNotSupportedException(" +
        "\"This Roslyn API is not implemented by AnalyzeAot.\");";

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

    private SyntaxNode AddProxyMembers(
        INamedTypeSymbol type,
        SyntaxNode declaration)
    {
        if (!_model.RequiresProxy(type) ||
            declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return declaration;
        }

        VtblProjection instanceVtbl =
            _model.GetInstanceVtbl(type);
        string vtblType =
            $"global::AnalyzeAot.Abi.{instanceVtbl.Name}";
        string getVtblExpression =
            "global::AnalyzeAot.RoslynFacade.RoslynVtblFactory." +
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
                "__AnalyzeAotGetVtbl() { " +
                "if (__analyzeAotVtbl is not null) " +
                "return __analyzeAotVtbl; " +
                "if (__analyzeAotHandle == 0) " +
                "return global::AnalyzeAot.RoslynFacade.RoslynVtblFactory." +
                $"{instanceVtbl.FactoryMethodName}(" +
                "global::AnalyzeAot.RoslynFacade.RoslynFacadeRuntime.GetCurrentControlVtbl()); " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no vtbl.\"); }"
            : $"internal {vtblType} " +
                "__AnalyzeAotGetVtbl() => __analyzeAotVtbl ?? " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no vtbl.\");";
        string getControlVtblMember = type.IsValueType
            ? "internal global::AnalyzeAot.Abi.IRoslynControlVtbl " +
                "__AnalyzeAotGetControlVtbl() { " +
                "if (__analyzeAotControlVtbl is not null) " +
                "return __analyzeAotControlVtbl; " +
                "if (__analyzeAotHandle == 0) " +
                "return global::AnalyzeAot.RoslynFacade.RoslynFacadeRuntime." +
                "GetCurrentControlVtbl(); " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no control vtbl.\"); }"
            : "internal global::AnalyzeAot.Abi.IRoslynControlVtbl " +
                "__AnalyzeAotGetControlVtbl() => __analyzeAotControlVtbl ?? " +
                "throw new global::System.InvalidOperationException(" +
                "\"This Roslyn facade value has no control vtbl.\");";
        var members = new List<MemberDeclarationSyntax>
        {
            ParseMember(
                "private " + readonlyModifier +
                "global::AnalyzeAot.Abi.IRoslynControlVtbl? " +
                "__analyzeAotControlVtbl;"),
            ParseMember(
                $"private {readonlyModifier}{vtblType}? " +
                "__analyzeAotVtbl;"),
            ParseMember($"private {readonlyModifier}long __analyzeAotHandle;"),
            ParseMember(
                $"internal {typeName}(" +
                "global::AnalyzeAot.Abi.IRoslynControlVtbl controlVtbl, " +
                $"{vtblType} vtbl, " +
                $"long handle){constructorInitializer} {{ " +
                initializeStruct +
                "__analyzeAotControlVtbl = controlVtbl ?? throw new " +
                "global::System.ArgumentNullException(nameof(controlVtbl)); " +
                "__analyzeAotVtbl = vtbl ?? throw new " +
                "global::System.ArgumentNullException(nameof(vtbl)); " +
                "__analyzeAotHandle = handle != 0 ? handle : throw new " +
                "global::System.ArgumentOutOfRangeException(nameof(handle)); }"),
            ParseMember(getVtblMember),
            ParseMember(getControlVtblMember),
            ParseMember(
                "internal long __AnalyzeAotGetHandle(" +
                "global::AnalyzeAot.Abi.IRoslynControlVtbl controlVtbl) { " +
                "global::AnalyzeAot.Abi.IRoslynControlVtbl actual = " +
                "__AnalyzeAotGetControlVtbl(); " +
                "if (!global::System.Object.ReferenceEquals(actual, controlVtbl)) " +
                "throw new global::System.InvalidOperationException(" +
                "\"Roslyn facade values cannot cross control vtbl identities.\"); " +
                "return __analyzeAotHandle; }"),
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
                    "__AnalyzeAotProxy")
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
                    SyntaxFactory.Token(SyntaxKind.SealedKeyword))
                .AddBaseListTypes(
                    SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName(fullyQualifiedType)))
                .AddMembers(
                    ParseMember(
                        "internal __AnalyzeAotProxy(" +
                        "global::AnalyzeAot.Abi.IRoslynControlVtbl controlVtbl, " +
                        $"{vtblType} vtbl, " +
                        "long handle) : base(controlVtbl, vtbl, handle) { }"));
            proxy = proxy.AddMembers(
                GetAbstractMembers(type)
                    .Select(CreateProxyOverride)
                    .ToArray());
            members.Add(proxy);
            members.Add(
                ParseMember(
                    $"internal static {fullyQualifiedType} __AnalyzeAotCreateProxy(" +
                    "global::AnalyzeAot.Abi.IRoslynControlVtbl controlVtbl, " +
                    "long handle) => new __AnalyzeAotProxy(" +
                    $"controlVtbl, {getVtblExpression}, handle);"));
        }
        else
        {
            members.Add(
                ParseMember(
                    $"internal static {fullyQualifiedType} __AnalyzeAotCreateProxy(" +
                    "global::AnalyzeAot.Abi.IRoslynControlVtbl controlVtbl, " +
                    $"long handle) => new {typeName}(" +
                    $"controlVtbl, {getVtblExpression}, handle);"));
        }

        return typeDeclaration.AddMembers([.. members]);
    }

    private static FieldDeclarationSyntax RewriteField(
        FieldDeclarationSyntax declaration,
        IFieldSymbol symbol)
    {
        if (!symbol.IsStatic || symbol.IsConst)
        {
            return declaration;
        }

        string message =
            $"Static Roslyn field '{symbol.ToDisplayString()}' " +
            "is not implemented by AnalyzeAot.";
        string fieldType = symbol.Type.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat);
        ExpressionSyntax initializer = SyntaxFactory.ParseExpression(
            "global::AnalyzeAot.RoslynFacade.RoslynFacadeRuntime." +
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
        return declaration switch
        {
            MethodDeclarationSyntax method =>
                RewriteProxyMethod(
                    method,
                    (IMethodSymbol)symbol),
            PropertyDeclarationSyntax property =>
                RewriteProxyProperty(property),
            IndexerDeclarationSyntax indexer =>
                RewriteProxyIndexer(indexer),
            EventDeclarationSyntax @event =>
                RewriteProxyEvent(@event),
            EventFieldDeclarationSyntax eventField =>
                RewriteProxyEventField(eventField),
            _ => throw new InvalidOperationException(
                $"Unsupported abstract proxy member '{symbol}'."),
        };
    }

    private static MethodDeclarationSyntax RewriteProxyMethod(
        MethodDeclarationSyntax declaration,
        IMethodSymbol symbol)
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
            .WithBody(GetUnsupportedBody());
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
        PropertyDeclarationSyntax declaration)
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
                        declaration.AccessorList.Accessors.Select(
                            accessor => accessor
                                .WithBody(GetUnsupportedBody())
                                .WithExpressionBody(null)
                                .WithSemicolonToken(default)))));
    }

    private static IndexerDeclarationSyntax RewriteProxyIndexer(
        IndexerDeclarationSyntax declaration)
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
                        declaration.AccessorList.Accessors.Select(
                            accessor => accessor
                                .WithBody(GetUnsupportedBody())
                                .WithExpressionBody(null)
                                .WithSemicolonToken(default)))));
    }

    private static EventDeclarationSyntax RewriteProxyEvent(
        EventDeclarationSyntax declaration)
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
                        declaration.AccessorList.Accessors.Select(
                            accessor => accessor
                                .WithBody(GetUnsupportedBody())
                                .WithExpressionBody(null)
                                .WithSemicolonToken(default)))));
    }

    private static EventDeclarationSyntax RewriteProxyEventField(
        EventFieldDeclarationSyntax declaration)
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

    private static MethodDeclarationSyntax RewriteMethod(
        MethodDeclarationSyntax declaration,
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

    private static PropertyDeclarationSyntax RewriteProperty(
        PropertyDeclarationSyntax declaration,
        MemberProjection projection) =>
        declaration.AccessorList is null ||
        projection.Symbol is IPropertySymbol
        {
            IsAbstract: true
        } ||
        projection.Symbol.ContainingType?.TypeKind == TypeKind.Interface
            ? declaration
            : declaration
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(
                    declaration.AccessorList.WithAccessors(
                        RewriteAccessors(
                            declaration.AccessorList.Accessors,
                            projection)));

    private static IndexerDeclarationSyntax RewriteIndexer(
        IndexerDeclarationSyntax declaration,
        MemberProjection projection) =>
        declaration.AccessorList is null ||
        projection.Symbol is IPropertySymbol
        {
            IsAbstract: true
        } ||
        projection.Symbol.ContainingType?.TypeKind == TypeKind.Interface
            ? declaration
            : declaration
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(
                    declaration.AccessorList.WithAccessors(
                        RewriteAccessors(
                            declaration.AccessorList.Accessors,
                            projection)));

    private static EventDeclarationSyntax RewriteEvent(
        EventDeclarationSyntax declaration,
        MemberProjection projection)
    {
        if (declaration.AccessorList is null ||
            projection.Symbol is IEventSymbol { IsAbstract: true } ||
            projection.Symbol.ContainingType?.TypeKind == TypeKind.Interface)
        {
            return declaration;
        }

        return declaration.WithAccessorList(
            declaration.AccessorList.WithAccessors(
                RewriteAccessors(
                    declaration.AccessorList.Accessors,
                    projection)));
    }

    private static SyntaxList<AccessorDeclarationSyntax> RewriteAccessors(
        SyntaxList<AccessorDeclarationSyntax> accessors,
        MemberProjection projection)
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
            ProjectedCall? operation = projection.Calls
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
            $"global::AnalyzeAot.Abi.{vtbl.Name}";
        string controlVtblExpression = operation.HasReceiver
            ? "__AnalyzeAotGetControlVtbl()"
            : "global::AnalyzeAot.RoslynFacade.RoslynFacadeRuntime." +
                "GetCurrentControlVtbl()";
        yield return
            "global::AnalyzeAot.Abi.IRoslynControlVtbl controlVtbl = " +
            $"{controlVtblExpression};";
        string vtblExpression = operation.HasReceiver
            ? "__AnalyzeAotGetVtbl()"
            : "global::AnalyzeAot.RoslynFacade.RoslynVtblFactory." +
                $"{vtbl.FactoryMethodName}(" +
                "controlVtbl)";
        yield return
            $"{vtblType} vtbl = " +
            $"{vtblExpression};";

        var arguments = new List<string>();
        if (operation.HasReceiver)
        {
            if (operation.ReturnValue.Kind == AbiTypeKind.Utf8String)
            {
                yield return
                    $"{operation.Receiver!.AbiType} __analyzeAotReceiver = " +
                    "__AnalyzeAotGetHandle(controlVtbl);";
                arguments.Add("__analyzeAotReceiver");
            }
            else
            {
                arguments.Add("__AnalyzeAotGetHandle(controlVtbl)");
            }
        }

        arguments.AddRange(operation.Parameters.Select(
            parameter => GetFacadeArgument(parameter, "controlVtbl")));

        string invocationPrefix =
            $"vtbl.{operation.GeneratedName}(";
        if (operation.ReturnValue.Kind == AbiTypeKind.Utf8String)
        {
            arguments.Add("buffer");
            arguments.Add("bufferLength");
            arguments.Add("out requiredLength");
            string nullableSuppression =
                operation.ReturnValue.IsNullable ? string.Empty : "!";
            yield return
                "return global::AnalyzeAot.RoslynFacade.RoslynFacadeRuntime." +
                "ReadUtf8String(controlVtbl, " +
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
                "global::AnalyzeAot.RoslynFacade.RoslynFacadeRuntime." +
                "ThrowIfFailed(controlVtbl, status);";
            yield break;
        }

        string resultType = operation.ReturnValue.AbiType;
        arguments.Add($"out {resultType} result");
        yield return
            $"int status = {invocationPrefix}{string.Join(", ", arguments)});";
        yield return
            "global::AnalyzeAot.RoslynFacade.RoslynFacadeRuntime." +
            "ThrowIfFailed(controlVtbl, status);";
        if (operation.Strategy == ProjectionStrategy.Constructor)
        {
            VtblProjection instanceVtbl =
                operation.ContainingInstanceVtbl
                ?? throw new InvalidOperationException(
                    $"Constructor '{operation.GeneratedName}' has no instance vtbl.");
            yield return
                "__analyzeAotControlVtbl = controlVtbl;";
            yield return
                "__analyzeAotVtbl = " +
                "global::AnalyzeAot.RoslynFacade.RoslynVtblFactory." +
                $"{instanceVtbl.FactoryMethodName}(controlVtbl);";
            yield return "__analyzeAotHandle = result;";
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
                        $"{name}.__AnalyzeAotGetHandle({controlVtbl})"
                    : $"{name}.__AnalyzeAotGetHandle({controlVtbl})",
            AbiTypeKind.ValueHandle =>
                $"{name}.__AnalyzeAotGetHandle({controlVtbl})",
            AbiTypeKind.NullableHandle =>
                $"{name}.HasValue ? " +
                $"{name}.Value.__AnalyzeAotGetHandle({controlVtbl}) : 0L",
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

    private static string GetProxyCreation(
        AbiTypePlan plan,
        string expression)
    {
        INamedTypeSymbol remoteType = plan.RemoteType
            ?? throw new InvalidOperationException(
                $"ABI plan '{plan.Kind}' has no remote type.");
        return $"{remoteType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}." +
            $"__AnalyzeAotCreateProxy(controlVtbl, {expression})";
    }
}

internal static class AnalyzerLocalFacadeEmitter
{
    public static bool TryGetStatements(
        ProjectedCall operation,
        out IReadOnlyList<string> statements)
    {
        IMethodSymbol method = operation.Symbol;
        string containingType = method.ContainingType.ToDisplayString();

        if (containingType == "Microsoft.CodeAnalysis.DiagnosticDescriptor")
        {
            if (method.MethodKind == MethodKind.Constructor &&
                method.Parameters.Length == 9 &&
                method.Parameters[1].Type.SpecialType ==
                    SpecialType.System_String)
            {
                statements =
                [
                    "__AnalyzeAotInitializeLocal(id, title, messageFormat, category, defaultSeverity, isEnabledByDefault, description, helpLinkUri, customTags);",
                ];
                return true;
            }

            if (method.Name is "get_Id" or "get_Category")
            {
                string propertyName = method.Name[4..];
                statements = WithRemoteFallback(
                    operation,
                    $"if (__AnalyzeAotIsLocal) return __AnalyzeAotLocal{propertyName};");
                return true;
            }

            if (method.Name is
                "get_DefaultSeverity" or
                "get_IsEnabledByDefault")
            {
                string propertyName = method.Name[4..];
                statements = WithRemoteFallback(
                    operation,
                    $"if (__AnalyzeAotIsLocal) return __AnalyzeAotLocal{propertyName};");
                return true;
            }

            if (method.Name == "Equals" &&
                method.Parameters is
                [
                    {
                        Type: INamedTypeSymbol parameterType
                    }
                ] &&
                SymbolEqualityComparer.Default.Equals(
                    parameterType,
                    method.ContainingType))
            {
                statements = WithRemoteFallback(
                    operation,
                    "if (__AnalyzeAotIsLocal) return global::System.Object.ReferenceEquals(this, other);");
                return true;
            }

            if (method.Name == "GetHashCode" &&
                method.Parameters.IsEmpty)
            {
                statements = WithRemoteFallback(
                    operation,
                    "if (__AnalyzeAotIsLocal) return global::System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);");
                return true;
            }
        }

        if (containingType == "Microsoft.CodeAnalysis.Diagnostic" &&
            method.Name == "Create" &&
            method.IsStatic &&
            method.Parameters is
            [
                { Name: "descriptor" },
                { Name: "location" },
                { Name: "messageArgs", Type: IArrayTypeSymbol }
            ])
        {
            statements =
            [
                "return __AnalyzeAotCreateLocal(descriptor, location, messageArgs);",
            ];
            return true;
        }

        if (containingType == "Microsoft.CodeAnalysis.SyntaxNode" &&
            method.Name == "GetLocation" &&
            method.Parameters.IsEmpty)
        {
            statements =
            [
                "return global::Microsoft.CodeAnalysis.Location.__AnalyzeAotCreateLocal(Span);",
            ];
            return true;
        }

        if (containingType ==
                "Microsoft.CodeAnalysis.Diagnostics.SyntaxNodeAnalysisContext" &&
            method.Name == "get_Node")
        {
            statements =
            [
                "return __AnalyzeAotGetLocalNode();",
            ];
            return true;
        }

        if (containingType ==
                "Microsoft.CodeAnalysis.Diagnostics.SyntaxNodeAnalysisContext" &&
            method.Name == "ReportDiagnostic")
        {
            statements = WithRemoteFallback(
                operation,
                "if (__AnalyzeAotTryReportLocal(diagnostic)) return;");
            return true;
        }

        if (containingType ==
                "Microsoft.CodeAnalysis.Diagnostics.AnalysisContext" &&
            method.Name == "RegisterSyntaxNodeAction" &&
            method.IsGenericMethod &&
            method.Parameters.Length == 2 &&
            method.Parameters[1].Type is IArrayTypeSymbol)
        {
            statements =
            [
                "global::System.ArgumentNullException.ThrowIfNull(action);",
                "global::System.ArgumentNullException.ThrowIfNull(syntaxKinds);",
                "RegisterSyntaxNodeAction(action, global::System.Collections.Immutable.ImmutableArray.CreateRange(syntaxKinds));",
            ];
            return true;
        }

        statements = [];
        return false;
    }

    private static IReadOnlyList<string> WithRemoteFallback(
        ProjectedCall operation,
        string localStatement)
    {
        var statements = new List<string> { localStatement };
        if (operation.IsSupported)
        {
            statements.AddRange(FacadeBodyEmitter.GetStatements(operation));
        }
        else
        {
            statements.Add(
                "throw new global::System.PlatformNotSupportedException(\"This Roslyn API is not implemented by AnalyzeAot.\");");
        }

        return statements;
    }
}
