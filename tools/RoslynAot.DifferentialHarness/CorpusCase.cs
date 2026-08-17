using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynAot.DifferentialHarness;

/// <summary>
/// One compilation: a directory under corpus/&lt;Rule&gt;/&lt;Case&gt;/
/// holding one or more .cs sources and an optional case.json declaring
/// which rule IDs it is expected to exercise.
/// </summary>
internal sealed record CorpusCase(
    string Name,
    string CaseDirectory,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> DeclaredRuleIds,
    IReadOnlyList<string> ExtraCompilerArguments)
{
    public static IReadOnlyList<CorpusCase> LoadAll(string corpusRoot)
    {
        if (!Directory.Exists(corpusRoot))
        {
            throw new HarnessEnvironmentException(
                $"Corpus directory not found: '{corpusRoot}'.");
        }

        var cases = new List<CorpusCase>();
        foreach (string ruleDirectory in Directory.EnumerateDirectories(corpusRoot)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            string ruleName = Path.GetFileName(ruleDirectory);
            foreach (string caseDirectory in Directory
                .EnumerateDirectories(ruleDirectory)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                cases.Add(Load(ruleName, caseDirectory));
            }
        }

        return cases;
    }

    private static CorpusCase Load(string ruleName, string caseDirectory)
    {
        string caseName = Path.GetFileName(caseDirectory);
        string name = $"{ruleName}/{caseName}";
        var sourceFiles = Directory
            .EnumerateFiles(caseDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (sourceFiles.Length == 0)
        {
            throw new HarnessEnvironmentException(
                $"Corpus case '{name}' has no .cs source files.");
        }

        string caseJsonPath = Path.Combine(caseDirectory, "case.json");
        CorpusCaseFile? caseFile = File.Exists(caseJsonPath)
            ? JsonSerializer.Deserialize<CorpusCaseFile>(
                File.ReadAllText(caseJsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            : null;

        // A case that says nothing about rules is assumed to exercise the
        // rule named by its containing directory - the common case. An
        // explicit (even empty) "rules" array is honored as written, so a
        // case can declare that it targets no particular rule.
        IReadOnlyList<string> declaredRuleIds =
            caseFile?.Rules ?? [ruleName];

        return new CorpusCase(
            name,
            caseDirectory,
            sourceFiles,
            declaredRuleIds,
            caseFile?.ExtraCompilerArguments ?? []);
    }

    private sealed class CorpusCaseFile
    {
        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("rules")]
        public string[]? Rules { get; set; }

        [JsonPropertyName("extraCompilerArguments")]
        public string[]? ExtraCompilerArguments { get; set; }
    }
}
