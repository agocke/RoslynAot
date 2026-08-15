using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoslynAot.Abi;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynAot.Csc;

internal static class RoslynProjectionValidation
{
    private static readonly StrategyBasedComWrappers s_comWrappers = new();
    private static readonly List<nint> s_libraries = [];

    public static unsafe void Run(string? clientPath = null)
    {
        var interop = new RoslynInterop();
        var textSpanVtbl = new TextSpanVtblDispatcher(interop);
        var syntaxNodeVtbl = new SyntaxNodeVtblDispatcher(interop);
        var parseOptionsTypeVtbl =
            new CSharpParseOptionsTypeVtblDispatcher(interop);
        var syntaxFactoryVtbl = new SyntaxFactoryVtblDispatcher(interop);
        var parserVtbl = new SyntaxTokenParserVtblDispatcher(interop);
        var parserResultVtbl =
            new SyntaxTokenParserResultVtblDispatcher(interop);

        AssertSuccess(
            textSpanVtbl.TextSpan_get_Start(
                receiver: 0,
                out int defaultSpanStart));
        AssertSuccess(
            textSpanVtbl.TextSpan_get_Length(
                receiver: 0,
                out int defaultSpanLength));
        if (defaultSpanStart != default(TextSpan).Start ||
            defaultSpanLength != default(TextSpan).Length)
        {
            throw new InvalidOperationException(
                "A zero value handle did not resolve to default(TextSpan).");
        }

        var root = CSharpSyntaxTree.ParseText(
            "// \u03c0 \ud83d\ude00\nclass C { }").GetRoot();
        long rootHandle = interop.AddObject(root);
        AssertSuccess(
            syntaxNodeVtbl.SyntaxNode_get_RawKind(
                rootHandle,
                out int rawKind));
        if (rawKind != root.RawKind)
        {
            throw new InvalidOperationException(
                "SyntaxNode.RawKind did not round-trip through the projection.");
        }

        var foreignInterop = new RoslynInterop();
        var foreignSyntaxNodeVtbl =
            new SyntaxNodeVtblDispatcher(foreignInterop);
        if (foreignSyntaxNodeVtbl.SyntaxNode_get_RawKind(
                rootHandle,
                out _) != RoslynAbi.InvalidArgument)
        {
            throw new InvalidOperationException(
                "A Roslyn handle was accepted by the wrong interop identity.");
        }

        AssertSuccess(
            parseOptionsTypeVtbl.CSharpParseOptions_get_Default(
                out long optionsHandle));

        string sourceText = " // \u03c0 \ud83d\ude00\nclass C { }";
        long sourceHandle;
        fixed (char* source = sourceText)
        {
            AssertSuccess(
                interop.CreateSourceTextUtf16(
                    (nint)source,
                    sourceText.Length,
                    checksumAlgorithm: 1,
                    out sourceHandle));
        }

        long parserHandle = CreateParser(
            syntaxFactoryVtbl,
            sourceHandle,
            optionsHandle);
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_ParseNextToken(
                parserHandle,
                out long resultHandle));
        AssertSuccess(
            parserResultVtbl.SyntaxTokenParser_Result_get_Token(
                resultHandle,
                out long tokenHandle));
        AssertSuccess(
            parserResultVtbl.SyntaxTokenParser_Result_get_ContextualKind(
                resultHandle,
                out _));
        if (tokenHandle == 0)
        {
            throw new InvalidOperationException(
                "SyntaxTokenParser.Result.Token returned no handle.");
        }

        AssertSuccess(
            parserVtbl.SyntaxTokenParser_ResetTo(
                parserHandle,
                resultHandle));
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_Dispose(parserHandle));
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_Dispose(parserHandle));

        long triviaParser = CreateParser(
            syntaxFactoryVtbl,
            sourceHandle,
            optionsHandle);
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_Dispose(parserHandle));
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_ParseLeadingTrivia(
                triviaParser,
                out _));
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_ParseTrailingTrivia(
                triviaParser,
                out _));
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_Dispose(triviaParser));

        long skipParser = CreateParser(
            syntaxFactoryVtbl,
            sourceHandle,
            optionsHandle);
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_SkipForwardTo(skipParser, 1));
        AssertSuccess(
            parserVtbl.SyntaxTokenParser_Dispose(skipParser));

        ValidateConcurrentErrors(
            interop,
            syntaxNodeVtbl,
            parserVtbl,
            rootHandle,
            skipParser);

        if (clientPath is not null)
        {
            RunFacadeClient(interop, rootHandle, clientPath);
        }

        Console.WriteLine("Roslyn projection validation passed.");
    }

    private static unsafe void RunFacadeClient(
        RoslynInterop interop,
        long rootHandle,
        string clientPath)
    {
        nint library = NativeLibrary.Load(clientPath);
        s_libraries.Add(library);
        nint export = NativeLibrary.GetExport(
            library,
            "roslyn_aot_validate_roslyn_projection");
        var validate =
            (delegate* unmanaged[Cdecl]<nint, long, int>)export;
        nint interopPointer =
            s_comWrappers.GetOrCreateComInterfaceForObject(
                interop,
                CreateComInterfaceFlags.None);
        try
        {
            int result = validate(interopPointer, rootHandle);
            if (result != RoslynAbi.Success)
            {
                throw new InvalidOperationException(
                    $"The generated facade client failed with 0x{result:x8}.");
            }

            GC.KeepAlive(interop);
        }
        finally
        {
            AnalyzerAbi.Release(interopPointer);
        }
    }

    private static long CreateParser(
        SyntaxFactoryVtblDispatcher syntaxFactoryVtbl,
        long sourceHandle,
        long optionsHandle)
    {
        AssertSuccess(
            syntaxFactoryVtbl.SyntaxFactory_CreateTokenParser(
                sourceHandle,
                optionsHandle,
                out long parserHandle));
        return parserHandle;
    }

    private static void ValidateConcurrentErrors(
        RoslynInterop interop,
        SyntaxNodeVtblDispatcher syntaxNodeVtbl,
        SyntaxTokenParserVtblDispatcher parserVtbl,
        long validNodeHandle,
        long disposedParserHandle)
    {
        using var barrier = new Barrier(2);
        RoslynRemoteErrorKind argumentKind = default;
        RoslynRemoteErrorKind disposedKind = default;

        Task argumentTask = Task.Run(
            () =>
            {
                int status = syntaxNodeVtbl.SyntaxNode_get_RawKind(
                    receiver: 0,
                    out _);
                barrier.SignalAndWait();
                if (status != RoslynAbi.InvalidArgument)
                {
                    throw new InvalidOperationException(
                        "Expected an invalid-argument projection failure.");
                }

                argumentKind = ReadErrorKind(interop);
            });
        Task disposedTask = Task.Run(
            () =>
            {
                int status =
                    parserVtbl.SyntaxTokenParser_ParseNextToken(
                        disposedParserHandle,
                        out _);
                barrier.SignalAndWait();
                if (status != RoslynAbi.ObjectDisposed)
                {
                    throw new InvalidOperationException(
                        "Expected an object-disposed projection failure.");
                }

                disposedKind = ReadErrorKind(interop);
            });
        Task.WaitAll(argumentTask, disposedTask);

        if (argumentKind != RoslynRemoteErrorKind.Argument ||
            disposedKind != RoslynRemoteErrorKind.ObjectDisposed)
        {
            throw new InvalidOperationException(
                "Concurrent Roslyn errors were not isolated per thread.");
        }

        AssertSuccess(
            syntaxNodeVtbl.SyntaxNode_get_RawKind(
                validNodeHandle,
                out _));
    }

    private static unsafe RoslynRemoteErrorKind ReadErrorKind(
        RoslynInterop interop)
    {
        AssertSuccess(
            interop.CopyLastErrorUtf16(
                0,
                0,
                out int charCount,
                out RoslynRemoteErrorKind errorKind));
        char[] chars = new char[Math.Max(charCount, 1)];
        fixed (char* buffer = chars)
        {
            AssertSuccess(
                interop.CopyLastErrorUtf16(
                    (nint)buffer,
                    charCount,
                    out int copiedCharCount,
                    out RoslynRemoteErrorKind copiedKind));
            if (copiedCharCount != charCount ||
                copiedKind != errorKind)
            {
                throw new InvalidOperationException(
                    "Roslyn error details changed while being copied.");
            }
        }

        return errorKind;
    }

    private static void AssertSuccess(int status)
    {
        if (status != RoslynAbi.Success)
        {
            throw new InvalidOperationException(
                $"Roslyn projection validation failed with 0x{status:x8}.");
        }
    }
}
