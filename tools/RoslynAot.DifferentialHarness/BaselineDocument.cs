using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynAot.DifferentialHarness;

internal sealed class BaselineDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("ruleIds")]
    public List<string> RuleIds { get; set; } = [];

    [JsonPropertyName("entries")]
    public List<BaselineEntry> Entries { get; set; } = [];

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions s_readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static BaselineDocument FromBurndown(IReadOnlyList<BurndownEntry> entries)
    {
        var document = new BaselineDocument
        {
            RuleIds = entries
                .Select(e => e.RuleId)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList(),
            Entries = entries
                .OrderBy(e => e.RuleId, StringComparer.Ordinal)
                .Select(e => new BaselineEntry
                {
                    RuleId = e.RuleId,
                    AnalyzerType = e.AnalyzerType,
                    Status = e.Status.ToString(),
                    Reason = e.Reason is null
                        ? null
                        : new BaselineReason
                        {
                            Kind = e.Reason.Kind,
                            Case = e.Reason.Case,
                            ActionKind = e.Reason.ActionKind,
                            ExceptionType = e.Reason.ExceptionType,
                            FailingMember = e.Reason.FailingMember,
                            Detail = e.Reason.Detail,
                        },
                })
                .ToList(),
        };
        return document;
    }

    public static BaselineDocument? Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<BaselineDocument>(
                File.ReadAllText(path), s_readOptions)
            : null;

    /// <summary>
    /// Writes exactly the bytes the comparator will read back, so
    /// --update-baseline followed by a rerun always produces a clean
    /// Match: sorted keys, LF line endings, trailing newline.
    /// </summary>
    public void Save(string path)
    {
        string json = JsonSerializer.Serialize(this, s_writeOptions)
            .Replace("\r\n", "\n") + "\n";
        File.WriteAllText(path, json);
    }
}

internal sealed class BaselineEntry
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";

    [JsonPropertyName("analyzerType")]
    public string? AnalyzerType { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("reason")]
    public BaselineReason? Reason { get; set; }
}

internal sealed class BaselineReason
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("case")]
    public string Case { get; set; } = "";

    [JsonPropertyName("actionKind")]
    public string? ActionKind { get; set; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; set; }

    [JsonPropertyName("failingMember")]
    public string? FailingMember { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}
