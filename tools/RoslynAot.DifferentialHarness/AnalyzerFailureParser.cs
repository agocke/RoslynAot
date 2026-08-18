using System.Text.RegularExpressions;

namespace RoslynAot.DifferentialHarness;

/// <summary>
/// A structured reading of one AD0001 message. Coupled to two
/// repo-owned format strings:
///   - src/RoslynAot.AnalyzerRuntime/AnalyzerExport.cs FormatFailure:
///     "RoslynAot analyzer '{analyzerName}' failed during {operation}:"
///   - src/CscAot/NativeDiagnosticAnalyzer.cs InvokeWithHost:
///     "Analyzer transport operation for [{diagnosticIds}] failed with
///     0x{result:x8}."
/// If either string changes shape, parsing degrades to ParseFailed=true
/// rather than silently dropping the failure from the burn-down.
/// </summary>
internal sealed record AnalyzerFailure(
    IReadOnlyList<string> RuleIds,
    string? AnalyzerType,
    string? ActionKind,
    string? ExceptionType,
    string? ExceptionMessage,
    string? FailingMember,
    bool ParseFailed,
    string RawMessage);

internal static partial class AnalyzerFailureParser
{
    public static AnalyzerFailure Parse(string ad0001Message)
    {
        Match ruleIdsMatch = RuleIdsPattern().Match(ad0001Message);
        Match analyzerMatch = AnalyzerPattern().Match(ad0001Message);
        Match exceptionMatch = ExceptionPattern().Match(ad0001Message);
        MatchCollection frameMatches = FramePattern().Matches(ad0001Message);

        IReadOnlyList<string> ruleIds = ruleIdsMatch.Success
            ? ruleIdsMatch.Groups["ids"].Value
                .Split(',', StringSplitOptions.TrimEntries |
                    StringSplitOptions.RemoveEmptyEntries)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray()
            : [];

        string? failingMember = null;
        foreach (Match frame in frameMatches)
        {
            string member = frame.Groups["member"].Value;
            if (!member.StartsWith("RoslynAot.AnalyzerRuntime.", StringComparison.Ordinal) &&
                !member.StartsWith("RoslynAot.Csc.", StringComparison.Ordinal) &&
                !member.StartsWith("Microsoft.CodeAnalysis.Diagnostics.AnalyzerExecutor", StringComparison.Ordinal))
            {
                failingMember = NormalizeMember(member);
                break;
            }
        }

        bool parseFailed = !analyzerMatch.Success;

        return new AnalyzerFailure(
            ruleIds,
            analyzerMatch.Success ? analyzerMatch.Groups["type"].Value : null,
            analyzerMatch.Success ? analyzerMatch.Groups["action"].Value : null,
            exceptionMatch.Success ? exceptionMatch.Groups["type"].Value : null,
            exceptionMatch.Success ? exceptionMatch.Groups["message"].Value : null,
            failingMember,
            parseFailed,
            ad0001Message);
    }

    /// <summary>
    /// Strips the generated proxy class out of a frame, so the burn-down names
    /// the Roslyn member an analyzer called rather than the facade's
    /// implementation of it. A remoted abstract member's body lives in the
    /// proxy, so without this the same member reads as
    /// <c>SyntaxReference.__RoslynAotProxy.GetSyntax</c> or
    /// <c>SyntaxReference.GetSyntax</c> depending on how it was projected.
    /// Generated <em>members</em> such as <c>__RoslynAotGetControlVtbl</c> are
    /// deliberately left alone: naming one is the finding.
    /// </summary>
    private static string NormalizeMember(string member) =>
        member
            .Replace(".__RoslynAotProxy.", ".", StringComparison.Ordinal)
            .Replace(
                ".__RoslynAotImplementation.",
                ".",
                StringComparison.Ordinal);

    [GeneratedRegex(@"operation for \[(?<ids>[^\]]*)\] failed with 0x[0-9a-f]{8}\.")]
    private static partial Regex RuleIdsPattern();

    [GeneratedRegex(@"RoslynAot analyzer '(?<type>[^']+)' failed during (?<action>.+?) action:")]
    private static partial Regex AnalyzerPattern();

    [GeneratedRegex(@"action:\r?\n(?<type>[\w.+`]+): (?<message>.*)")]
    private static partial Regex ExceptionPattern();

    /// <summary>
    /// A stack frame's member name, including the type-argument list on a
    /// generic method.
    /// </summary>
    /// <remarks>
    /// That list is not decoration. Without it the pattern cannot match
    /// <c>SyntaxNode.FirstAncestorOrSelf[TNode](...)</c> at all, so the search
    /// for the failing member skips the frame and attributes the failure to
    /// the next one down — the analyzer's own method. The burn-down then names
    /// analyzer code where it should name the Roslyn member, and it does so
    /// silently, which is the one outcome this parser's contract says it will
    /// not produce. Generic methods are not a rare shape here: the roadmap puts
    /// roughly half the remaining failures at generics.
    /// </remarks>
    [GeneratedRegex(@"\n\s+at (?<member>[\w.<>`]+\.[\w_<>]+(?:\[[\w,]+\])?)\(")]
    private static partial Regex FramePattern();
}
