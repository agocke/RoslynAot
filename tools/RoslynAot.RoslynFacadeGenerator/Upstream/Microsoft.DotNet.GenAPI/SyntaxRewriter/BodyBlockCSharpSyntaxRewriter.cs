// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Microsoft.DotNet.GenAPI.SyntaxRewriter
{
    /// <summary>
    /// Represents a <see cref="CSharpSyntaxVisitor{TResult}"/> which descends an entire <see cref="CSharpSyntaxNode"/> graph and
    /// modify visited constructor, method declarations SyntaxNodes in depth-first order.
    /// Rewrites body with default implementation details.
    /// </summary>
    public class BodyBlockCSharpSyntaxRewriter(string? _exceptionMessage) : CSharpSyntaxRewriter
    {
        /// <inheritdoc />
        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            // visit subtree first to normalize type names.
            if (base.VisitConstructorDeclaration(node) is not ConstructorDeclarationSyntax rs)
                return null;

            return rs.WithBody(GetUnsupportedBody());
        }

        /// <inheritdoc />
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            // visit subtree first to normalize type names.
            if (base.VisitMethodDeclaration(node) is not MethodDeclarationSyntax rs)
                return null;

            if (rs.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword)) || rs.Body is null)
            {
                return rs;
            }

            if (rs.ExpressionBody is not null)
            {
                rs = rs.WithExpressionBody(null);
            }

            rs = rs.WithBody(GetUnsupportedBody());

            return rs.WithParameterList(rs.ParameterList.WithTrailingTrivia(SyntaxFactory.Space));
        }

        /// <inheritdoc />
        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            // visit subtree first to normalize type names.
            return base.VisitOperatorDeclaration(node) is OperatorDeclarationSyntax rs ?
                rs.WithBody(GetUnsupportedBody()).WithParameterList(rs.ParameterList.WithTrailingTrivia(SyntaxFactory.Space)) :
                null;
        }

        /// <inheritdoc />
        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            return base.VisitConversionOperatorDeclaration(node) is ConversionOperatorDeclarationSyntax rs ?
                rs.WithBody(GetUnsupportedBody()).WithParameterList(rs.ParameterList.WithTrailingTrivia(SyntaxFactory.Space)) :
                null;
        }

        /// <inheritdoc />
        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            switch (node.Kind())
            {
                case SyntaxKind.GetAccessorDeclaration:
                case SyntaxKind.InitAccessorDeclaration:
                case SyntaxKind.SetAccessorDeclaration:
                case SyntaxKind.AddAccessorDeclaration:
                case SyntaxKind.RemoveAccessorDeclaration:
                    {
                        var accessorListSyntax = (AccessorListSyntax?)node.Parent;
                        if (accessorListSyntax?.Parent == null) break;

                        if (accessorListSyntax?.Parent is IndexerDeclarationSyntax indexerDeclarationSyntax)
                        {
                            var typeDeclarationSyntax = (TypeDeclarationSyntax?)indexerDeclarationSyntax.Parent;

                            if (indexerDeclarationSyntax.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword)) ||
                                (typeDeclarationSyntax != null && typeDeclarationSyntax.Keyword.IsKind(SyntaxKind.InterfaceKeyword)))
                            {
                                return node.WithSemicolonToken(node.SemicolonToken);
                            }

                            return ProcessPropertyDeclarationSyntax(node);
                        }
                        else if (accessorListSyntax?.Parent is PropertyDeclarationSyntax propertyDeclarationSyntax)
                        {
                            var typeDeclarationSyntax = (TypeDeclarationSyntax?)propertyDeclarationSyntax.Parent;

                            if (propertyDeclarationSyntax.Modifiers.Any(token => token.IsKind(SyntaxKind.AbstractKeyword)) ||
                                (typeDeclarationSyntax != null && typeDeclarationSyntax.Keyword.IsKind(SyntaxKind.InterfaceKeyword)))
                            {
                                return node.WithSemicolonToken(node.SemicolonToken);
                            }

                            return ProcessPropertyDeclarationSyntax(node);
                        }
                    }
                    break;
            }

            return base.VisitAccessorDeclaration(node);
        }

        private BlockSyntax GetUnsupportedBody() =>
            _exceptionMessage is not null ?
                GetMethodBodyFromText($"throw new global::System.PlatformNotSupportedException(\"{_exceptionMessage}\");") :
                GetMethodBodyFromText("throw null;");

        private AccessorDeclarationSyntax? ProcessPropertyDeclarationSyntax(AccessorDeclarationSyntax node)
        {
            node = node.WithBody(GetUnsupportedBody());

            return node.WithSemicolonToken(default)
                .WithKeyword(node.Keyword.WithTrailingTrivia(SyntaxFactory.Space));
        }

        private static BlockSyntax GetMethodBodyFromText(string text) =>
            SyntaxFactory.Block(SyntaxFactory.ParseStatement(text))
                .WithTrailingTrivia(SyntaxFactory.Space);
    }
}
