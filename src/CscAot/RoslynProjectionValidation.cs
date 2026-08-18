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

        // Migration Step 4 retired control-scoped handle identity: the
        // process now has exactly one handle table (RoslynInterop.Shared), so
        // there is no second interop for a foreign handle to be rejected by.
        // What identity now guarantees instead is dedup — the same object
        // crossing twice gets the same handle, which is what the reverse map
        // in RoslynHandleTable.Add exists for.
        long rootHandleAgain = interop.AddObject(root);
        if (rootHandleAgain != rootHandle)
        {
            throw new InvalidOperationException(
                "The same Roslyn object crossed twice did not reuse its handle.");
        }

        // String collections cross as handles so that membership is answered
        // by the collection Roslyn built, with its own comparer. Copying the
        // contents into a string[] and probing that would substitute ordinal
        // equality — the defect this asserts against is a *wrong answer*, not
        // an exception, so a case-insensitive source is what makes it visible.
        var caseInsensitive = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Alpha",
            "Beta",
        };
        long collectionHandle = interop.AddObject(caseInsensitive);
        AssertSuccess(
            interop.StringCollectionContains(
                collectionHandle,
                "ALPHA",
                out int containsMismatchedCase));
        if (containsMismatchedCase == 0)
        {
            throw new InvalidOperationException(
                "A string collection answered Contains with ordinal equality " +
                "instead of its own comparer.");
        }

        AssertSuccess(
            interop.GetCollectionCount(collectionHandle, out int collectionCount));
        if (collectionCount != caseInsensitive.Count)
        {
            throw new InvalidOperationException(
                "A string collection's count did not survive the projection.");
        }

        // Enumeration is the one operation the handle cannot answer a question
        // at a time, so it snapshots; nothing else in the corpus is guaranteed
        // to reach that path.
        AssertSuccess(
            interop.SnapshotStringCollection(
                collectionHandle,
                out long snapshotHandle));
        AssertSuccess(
            interop.GetCollectionCount(snapshotHandle, out int snapshotCount));
        if (snapshotCount != caseInsensitive.Count)
        {
            throw new InvalidOperationException(
                "A string collection snapshot did not preserve its elements.");
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
