namespace RoslynAot.DifferentialHarness;

internal static class Program
{
    private const int ExitMatch = 0;
    private const int ExitRegression = 1;
    private const int ExitStale = 2;
    private const int ExitEnvironmentError = 3;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return ExitEnvironmentError;
        }

        try
        {
            return args[0] switch
            {
                "inventory" => RunInventory(ParseOptions(args[1..])),
                "run" => RunDifferential(ParseOptions(args[1..])),
                "-h" or "--help" => PrintUsageAndSucceed(),
                _ => PrintUsageAndFail(),
            };
        }
        catch (HarnessEnvironmentException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return ExitEnvironmentError;
        }
    }

    private static int RunInventory(HarnessOptions options)
    {
        HarnessEnvironment environment = HarnessEnvironment.Resolve(options);
        Console.WriteLine($"Repo root:            {environment.RepoRoot}");
        Console.WriteLine($"SDK directory:         {environment.SdkDirectory}");
        Console.WriteLine($"Reference directory:   {environment.ReferenceDirectory}");
        Console.WriteLine($"Analyzer directory:    {environment.AnalyzerDirectory}");
        Console.WriteLine($"Roslyn bincore:        {environment.RoslynBincoreDirectory}");
        Console.WriteLine($"Native compiler:       {environment.NativeCompilerPath}");
        Console.WriteLine($"Native module:         {environment.NativeModulePath}");

        if (!File.Exists(environment.NativeCompilerPath) ||
            !File.Exists(environment.NativeModulePath))
        {
            Console.WriteLine();
            Console.WriteLine(
                "Native compiler and/or module are not published yet. Run " +
                "'run' (which publishes automatically) or pass --no-publish " +
                "only after publishing them yourself.");
            return ExitEnvironmentError;
        }

        string workDirectory = Path.Combine(
            ResolveOutputDirectory(environment, options), "probe");
        IReadOnlyList<string> ruleIds =
            RuleInventory.ProbeNativeRuleIds(environment, workDirectory);
        Console.WriteLine();
        Console.WriteLine($"Native module rule catalog ({ruleIds.Count}):");
        Console.WriteLine("  " + string.Join(", ", ruleIds));

        Console.WriteLine();
        Console.WriteLine("Generated globalconfig:");
        Console.WriteLine(
            GlobalConfigGenerator.Generate(ruleIds, ReadOptionsPreamble(environment)));
        return ExitMatch;
    }

    private static int RunDifferential(HarnessOptions options)
    {
        HarnessEnvironment environment = HarnessEnvironment.Resolve(options);
        if (!options.NoPublish)
        {
            ToolchainPublisher.PublishAll(environment, Console.Out);
        }

        if (!File.Exists(environment.NativeCompilerPath))
        {
            throw new HarnessEnvironmentException(
                $"Native compiler not found: '{environment.NativeCompilerPath}'. " +
                "Run without --no-publish, or publish it yourself first.");
        }

        if (!File.Exists(environment.NativeModulePath))
        {
            throw new HarnessEnvironmentException(
                $"Native module not found: '{environment.NativeModulePath}'. " +
                "Run without --no-publish, or publish it yourself first.");
        }

        if (options.Filter is not null && options.UpdateBaseline)
        {
            throw new HarnessEnvironmentException(
                "--update-baseline cannot be combined with --filter: a " +
                "filtered run only covers part of the rule set and would " +
                "write an incomplete baseline.");
        }

        string outputDirectory = ResolveOutputDirectory(environment, options);
        PrepareOutputDirectory(outputDirectory);

        IReadOnlyList<string> allRuleIds = RuleInventory.ProbeNativeRuleIds(
            environment, Path.Combine(outputDirectory, "probe"));
        IReadOnlyList<string> ruleIds = ApplyFilter(allRuleIds, options.Filter);

        string generatedDirectory = Path.Combine(outputDirectory, "generated");
        Directory.CreateDirectory(generatedDirectory);
        string globalConfigPath =
            Path.Combine(generatedDirectory, "corpus.globalconfig");
        File.WriteAllText(
            globalConfigPath,
            GlobalConfigGenerator.Generate(ruleIds, ReadOptionsPreamble(environment)));

        string corpusRoot = Path.Combine(environment.RepoRoot, "corpus");
        IReadOnlyList<CorpusCase> allCases = CorpusCase.LoadAll(corpusRoot);

        // A full run exercises every corpus case, including ones like
        // Baseline/Empty that declare no rule in the module's catalog -
        // they are still worth running for their own sake. --filter is
        // the explicit request to narrow which cases run at all.
        var ruleIdSet = new HashSet<string>(ruleIds, StringComparer.Ordinal);
        IReadOnlyList<CorpusCase> cases = options.Filter is null
            ? allCases
            : allCases.Where(c => c.DeclaredRuleIds.Any(ruleIdSet.Contains)).ToArray();
        if (cases.Count == 0)
        {
            throw new HarnessEnvironmentException(
                $"No corpus case under '{corpusRoot}' declares any rule " +
                $"matching --filter '{options.Filter}'. Nothing to run.");
        }

        var runner = new CompilationRunner(
            environment, globalConfigPath, options.TimeoutSeconds);
        string casesRoot = Path.Combine(outputDirectory, "cases");
        var comparableRuleIds = new HashSet<string>(ruleIds, StringComparer.Ordinal);

        var evaluations = new List<CaseEvaluation>(cases.Count);
        foreach (CorpusCase corpusCase in cases)
        {
            Console.WriteLine($"Running {corpusCase.Name} ...");
            evaluations.Add(
                CaseEvaluator.Evaluate(
                    environment, runner, corpusCase, comparableRuleIds, casesRoot));
        }

        // A failed managed compile makes the entire comparison meaningless:
        // with no baseline diagnostics, every native diagnostic looks
        // "extra" and every rule flips to Fail. Refuse to produce a
        // burn-down (and especially refuse to write it as a baseline)
        // rather than recording that garbage as the ratchet.
        foreach (CaseEvaluation evaluation in evaluations)
        {
            if (evaluation.ManagedResult.TimedOut ||
                evaluation.ManagedResult.Crashed ||
                !evaluation.ManagedResult.SarifProduced)
            {
                throw new HarnessEnvironmentException(
                    $"The managed baseline compilation for case " +
                    $"'{evaluation.Case.Name}' did not produce diagnostics " +
                    $"(exit code " +
                    $"{evaluation.ManagedResult.ExitCode?.ToString() ?? "none"}" +
                    $"{(evaluation.ManagedResult.TimedOut ? ", timed out" : "")}). " +
                    $"See {evaluation.ManagedResult.StdErrPath}.");
            }
        }

        BurndownResult burndownResult =
            BurndownBuilder.Build(ruleIds, evaluations);
        if (burndownResult.UnattributableFailures.Count > 0)
        {
            throw new HarnessEnvironmentException(
                "Some native failures could not be attributed to any rule, " +
                "so they would be missing from the burn-down:" +
                Environment.NewLine + "  " +
                string.Join(
                    Environment.NewLine + "  ",
                    burndownResult.UnattributableFailures));
        }

        IReadOnlyList<BurndownEntry> burndown = burndownResult.Entries;

        var nativeRuleIdSet =
            new HashSet<string>(allRuleIds, StringComparer.Ordinal);
        List<string> ledger = options.NoLedger
            ? []
            : evaluations
                .SelectMany(e => e.ManagedRuleCatalog)
                .Distinct(StringComparer.Ordinal)
                .Where(id => !nativeRuleIdSet.Contains(id))
                .Where(id => !id.StartsWith("CS", StringComparison.Ordinal))
                .ToList();

        bool fullRun = options.Filter is null;
        string baselinePath =
            Path.Combine(environment.RepoRoot, "eng", "differential-baseline.json");

        if (!fullRun)
        {
            HarnessReport partialReport = ReportWriter.Build(
                environment, ruleIds, evaluations, burndown, ledger, null);
            WriteReports(partialReport, outputDirectory);
            Console.WriteLine();
            Console.WriteLine(
                $"Filtered run ({options.Filter}) - baseline not checked. " +
                $"See {outputDirectory}.");
            return ExitMatch;
        }

        if (options.UpdateBaseline)
        {
            BaselineDocument.FromBurndown(burndown).Save(baselinePath);
            HarnessReport updateReport = ReportWriter.Build(
                environment, ruleIds, evaluations, burndown, ledger, null);
            WriteReports(updateReport, outputDirectory);
            Console.WriteLine($"Baseline written to {baselinePath}.");
            return ExitMatch;
        }

        BaselineDocument? baseline = BaselineDocument.Load(baselinePath);
        BaselineComparisonResult comparison =
            BaselineComparer.Compare(baseline, burndown);
        HarnessReport report = ReportWriter.Build(
            environment, ruleIds, evaluations, burndown, ledger, comparison);
        WriteReports(report, outputDirectory);

        Console.WriteLine();
        Console.WriteLine($"Verdict: {comparison.Verdict}");
        foreach (string regression in comparison.Regressions)
        {
            Console.Error.WriteLine($"REGRESSION: {regression}");
        }

        foreach (string stale in comparison.StaleReasons)
        {
            Console.WriteLine($"STALE: {stale}");
        }

        Console.WriteLine($"Report written to {outputDirectory}.");

        return comparison.Verdict switch
        {
            BaselineVerdict.Match => ExitMatch,
            BaselineVerdict.Regression => ExitRegression,
            BaselineVerdict.Stale => ExitStale,
            _ => ExitEnvironmentError,
        };
    }

    private const string OutputMarkerFileName = ".differential-harness-output";

    /// <summary>
    /// Clears the output directory, but only if it is empty or was
    /// created by a previous harness run. --output points a recursive
    /// delete at a caller-supplied path, so refuse anything that isn't
    /// demonstrably ours.
    /// </summary>
    private static void PrepareOutputDirectory(string outputDirectory)
    {
        string markerPath = Path.Combine(outputDirectory, OutputMarkerFileName);
        if (Directory.Exists(outputDirectory))
        {
            bool isEmpty = !Directory.EnumerateFileSystemEntries(outputDirectory)
                .Any();
            if (!isEmpty && !File.Exists(markerPath))
            {
                throw new HarnessEnvironmentException(
                    $"Refusing to clear '{outputDirectory}': it is not empty " +
                    $"and was not created by this harness (no " +
                    $"{OutputMarkerFileName} marker). Choose a different " +
                    "--output directory.");
            }

            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            markerPath,
            "Created by RoslynAot.DifferentialHarness; contents are " +
            "regenerated on each run.\n");
    }

    private static void WriteReports(HarnessReport report, string outputDirectory)
    {
        ReportWriter.WriteJson(
            report, Path.Combine(outputDirectory, "report.json"));
        ReportWriter.WriteMarkdown(
            report, Path.Combine(outputDirectory, "report.md"));
    }

    private static string? ReadOptionsPreamble(HarnessEnvironment environment)
    {
        string path = Path.Combine(
            environment.RepoRoot, "corpus", "analyzer-options.globalconfig");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    // Must be absolute: CompilationRunner launches both compilers with
    // WorkingDirectory set to the repo root and passes these paths
    // straight through, so a relative --output would have the child write
    // somewhere the harness never looks.
    private static string ResolveOutputDirectory(
        HarnessEnvironment environment, HarnessOptions options) =>
        Path.GetFullPath(
            options.OutputDirectory ??
            Path.Combine(environment.RepoRoot, "artifacts", "differential"));

    private static IReadOnlyList<string> ApplyFilter(
        IReadOnlyList<string> ruleIds, string? filter)
    {
        if (filter is null)
        {
            return ruleIds;
        }

        bool prefix = filter.EndsWith('*');
        string pattern = prefix ? filter[..^1] : filter;
        return ruleIds
            .Where(id => prefix
                ? id.StartsWith(pattern, StringComparison.Ordinal)
                : id == pattern)
            .ToArray();
    }

    private static HarnessOptions ParseOptions(string[] args)
    {
        var options = new HarnessOptions();
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            string Next() => index + 1 < args.Length
                ? args[++index]
                : throw new HarnessEnvironmentException(
                    $"Option '{arg}' requires a value.");

            switch (arg)
            {
                case "--no-publish":
                    options.NoPublish = true;
                    break;
                case "--no-ledger":
                    options.NoLedger = true;
                    break;
                case "--update-baseline":
                    options.UpdateBaseline = true;
                    break;
                case "--filter":
                    options.Filter = Next();
                    break;
                case "--timeout-seconds":
                    options.TimeoutSeconds = int.Parse(Next());
                    break;
                case "--output":
                    options.OutputDirectory = Next();
                    break;
                case "--repo-root":
                    options.RepoRoot = Next();
                    break;
                case "--sdk-directory":
                    options.SdkDirectory = Next();
                    break;
                case "--reference-directory":
                    options.ReferenceDirectory = Next();
                    break;
                case "--analyzer-directory":
                    options.AnalyzerDirectory = Next();
                    break;
                case "--roslyn-bincore-directory":
                    options.RoslynBincoreDirectory = Next();
                    break;
                case "--native-compiler":
                    options.NativeCompilerPath = Next();
                    break;
                case "--native-module":
                    options.NativeModulePath = Next();
                    break;
                default:
                    throw new HarnessEnvironmentException(
                        $"Unrecognized option '{arg}'.");
            }
        }

        return options;
    }

    private static int PrintUsageAndSucceed()
    {
        PrintUsage();
        return ExitMatch;
    }

    private static int PrintUsageAndFail()
    {
        PrintUsage();
        return ExitEnvironmentError;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            RoslynAot.DifferentialHarness

            Usage:
              inventory [options]
                  Resolve the toolchain, probe the native module's rule
                  catalog, and print the globalconfig that would be used.
                  Does not publish or compile the corpus.

              run [options]
                  Publish (unless --no-publish), run the corpus through
                  both compilers, compare, and check the baseline.

            Options:
              --no-publish                Skip 'dotnet publish' for the
                                           native compiler and module.
              --no-ledger                 Skip the coverage ledger.
              --update-baseline           Overwrite eng/differential-baseline.json
                                           with this run's burn-down.
              --filter <id|id*>           Only run rules matching this ID
                                           or prefix. Disables the baseline
                                           check.
              --timeout-seconds <n>       Per-compilation timeout (default 120).
              --output <dir>              Output directory (default
                                           artifacts/differential).
              --repo-root <dir>
              --sdk-directory <dir>
              --reference-directory <dir>
              --analyzer-directory <dir>
              --roslyn-bincore-directory <dir>
              --native-compiler <path>
              --native-module <path>
            """);
    }
}
