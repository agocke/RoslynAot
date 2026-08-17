using System.Text.Json.Serialization;

namespace RoslynAot.DifferentialHarness;

internal sealed class SarifLog
{
    [JsonPropertyName("runs")]
    public List<SarifRun> Runs { get; set; } = [];
}

internal sealed class SarifRun
{
    [JsonPropertyName("tool")]
    public SarifTool Tool { get; set; } = new();

    [JsonPropertyName("results")]
    public List<SarifResult> Results { get; set; } = [];
}

internal sealed class SarifTool
{
    [JsonPropertyName("driver")]
    public SarifDriver Driver { get; set; } = new();
}

internal sealed class SarifDriver
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("rules")]
    public List<SarifRule> Rules { get; set; } = [];
}

internal sealed class SarifRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("defaultConfiguration")]
    public SarifRuleConfiguration? DefaultConfiguration { get; set; }
}

internal sealed class SarifRuleConfiguration
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }
}

internal sealed class SarifResult
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";

    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("message")]
    public SarifMessage Message { get; set; } = new();

    [JsonPropertyName("locations")]
    public List<SarifLocation> Locations { get; set; } = [];

    [JsonPropertyName("relatedLocations")]
    public List<SarifLocation> RelatedLocations { get; set; } = [];

    [JsonPropertyName("properties")]
    public Dictionary<string, object?> Properties { get; set; } = [];

    [JsonPropertyName("suppressions")]
    public List<object> Suppressions { get; set; } = [];

    [JsonPropertyName("fixes")]
    public List<object> Fixes { get; set; } = [];
}

internal sealed class SarifMessage
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal sealed class SarifLocation
{
    [JsonPropertyName("physicalLocation")]
    public SarifPhysicalLocation? PhysicalLocation { get; set; }
}

internal sealed class SarifPhysicalLocation
{
    [JsonPropertyName("artifactLocation")]
    public SarifArtifactLocation? ArtifactLocation { get; set; }

    [JsonPropertyName("region")]
    public SarifRegion? Region { get; set; }
}

internal sealed class SarifArtifactLocation
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

internal sealed class SarifRegion
{
    [JsonPropertyName("startLine")]
    public int? StartLine { get; set; }

    [JsonPropertyName("startColumn")]
    public int? StartColumn { get; set; }

    [JsonPropertyName("endLine")]
    public int? EndLine { get; set; }

    [JsonPropertyName("endColumn")]
    public int? EndColumn { get; set; }
}
