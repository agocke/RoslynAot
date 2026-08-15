using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AnalyzeAot.Abi;
using AnalyzeAot.RoslynFacade;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AnalyzeAot.RoslynProjection.Client;

public static class ProjectionClient
{
    [UnmanagedCallersOnly(
        EntryPoint = "analyze_aot_validate_roslyn_projection",
        CallConvs = [typeof(CallConvCdecl)])]
    public static int Validate(nint interopPointer, long nodeHandle)
    {
        try
        {
            IRoslynControlVtbl controlVtbl =
                RoslynFacadeRuntime.GetOrCreateControlVtbl(
                    interopPointer);
            using (RoslynFacadeRuntime.Enter(controlVtbl))
            {
                TextSpan span = default;
                if (span.Start != 0 || span.Length != 0)
                {
                    return RoslynAbi.Failure;
                }

                SyntaxNode node =
                    SyntaxNode.__AnalyzeAotCreateProxy(
                        controlVtbl,
                        nodeHandle);
                if (node.RawKind == 0)
                {
                    return RoslynAbi.Failure;
                }

                CompilationUnitSyntax compilationUnit =
                    CompilationUnitSyntax.__AnalyzeAotCreateProxy(
                        controlVtbl,
                        nodeHandle);
                if (compilationUnit.RawKind != node.RawKind)
                {
                    return RoslynAbi.Failure;
                }

                _ = compilationUnit.EndOfFileToken;

                long textHandle =
                    RoslynFacadeRuntime.CreateSourceTextHandle(
                        controlVtbl,
                        " // trivia\nclass C { }",
                        (int)SourceHashAlgorithm.Sha1);
                SourceText text =
                    SourceText.__AnalyzeAotCreateProxy(
                        controlVtbl,
                        textHandle);
                CSharpParseOptions options = CSharpParseOptions.Default;

                using (SyntaxTokenParser parser =
                    SyntaxFactory.CreateTokenParser(text, options))
                {
                    SyntaxTokenParser.Result result =
                        parser.ParseNextToken();
                    SyntaxToken token = result.Token;
                    _ = result.ContextualKind;
                    parser.ResetTo(result);
                    GC.KeepAlive(token);
                }

                using (SyntaxTokenParser triviaParser =
                    SyntaxFactory.CreateTokenParser(text, options))
                {
                    _ = triviaParser.ParseLeadingTrivia();
                    _ = triviaParser.ParseTrailingTrivia();
                }

                SyntaxTokenParser skipParser =
                    SyntaxFactory.CreateTokenParser(text, options);
                skipParser.SkipForwardTo(1);
                skipParser.Dispose();
                skipParser.Dispose();
                try
                {
                    _ = skipParser.ParseNextToken();
                    return RoslynAbi.Failure;
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    _ = SymbolEqualityComparer.Default;
                    return RoslynAbi.Failure;
                }
                catch (TypeInitializationException exception)
                    when (exception.InnerException is
                        PlatformNotSupportedException)
                {
                }
            }

            return RoslynAbi.Success;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return RoslynAbi.Failure;
        }
    }
}
