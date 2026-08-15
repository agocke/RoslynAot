namespace AnalyzeAot.RoslynFacadeGenerator;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0] switch
            {
                "inspect" => Inspect(args[1..]),
                "generate" => Generate(args[1..]),
                "generate-analyzer-entrypoint" =>
                    GenerateAnalyzerEntryPoint(args[1..]),
                _ => PrintUsageAndFail(),
            };
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or BadImageFormatException
                or ArgumentException
                or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static int GenerateAnalyzerEntryPoint(string[] args)
    {
        var referencePaths = new List<string>();
        string? outputPath = null;
        string? generatedNamespace = null;
        string? language = null;
        string? assemblyPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when index + 1 < args.Length:
                    outputPath = args[++index];
                    break;
                case "--namespace" when index + 1 < args.Length:
                    generatedNamespace = args[++index];
                    break;
                case "--language" when index + 1 < args.Length:
                    language = args[++index];
                    break;
                case "--reference" when index + 1 < args.Length:
                    referencePaths.Add(args[++index]);
                    break;
                case "--output":
                case "--namespace":
                case "--language":
                case "--reference":
                    throw new ArgumentException(
                        $"Missing value for '{args[index]}'.");
                case string when assemblyPath is null:
                    assemblyPath = args[index];
                    break;
                default:
                    throw new ArgumentException(
                        $"Unexpected argument '{args[index]}'.");
            }
        }

        if (assemblyPath is null ||
            outputPath is null ||
            generatedNamespace is null ||
            language is null)
        {
            return PrintUsageAndFail();
        }

        AnalyzerEntryPointSourceGenerator.Generate(
            assemblyPath,
            referencePaths,
            outputPath,
            generatedNamespace,
            language);
        return 0;
    }

    private static int Inspect(string[] assemblyPaths)
    {
        if (assemblyPaths.Length == 0)
        {
            return PrintUsageAndFail();
        }

        bool succeeded = true;
        foreach (string assemblyPath in assemblyPaths)
        {
            try
            {
                AssemblySurface surface =
                    AssemblySurfaceInspector.Inspect(assemblyPath);
                PrintSurface(surface);
            }
            catch (Exception exception)
                when (exception is IOException
                    or UnauthorizedAccessException
                    or BadImageFormatException)
            {
                Console.Error.WriteLine(
                    $"Failed to inspect '{assemblyPath}': {exception.Message}");
                succeeded = false;
            }
        }

        return succeeded ? 0 : 1;
    }

    private static int Generate(string[] args)
    {
        var assemblyPaths = new List<string>();
        var referencePaths = new List<string>();
        string? outputPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when index + 1 < args.Length:
                    outputPath = args[++index];
                    break;
                case "--reference" when index + 1 < args.Length:
                    referencePaths.Add(args[++index]);
                    break;
                case "--output" or "--reference":
                    throw new ArgumentException(
                        $"Missing value for '{args[index]}'.");
                default:
                    assemblyPaths.Add(args[index]);
                    break;
            }
        }

        if (assemblyPaths.Count == 0 || outputPath is null)
        {
            return PrintUsageAndFail();
        }

        foreach (string assemblyPath in assemblyPaths)
        {
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    "Input assembly was not found.",
                    assemblyPath);
            }
        }

        foreach (string referencePath in referencePaths)
        {
            if (!File.Exists(referencePath)
                && !Directory.Exists(referencePath))
            {
                throw new FileNotFoundException(
                    "Reference path was not found.",
                    referencePath);
            }
        }

        FacadeSourceGenerator.Generate(
            assemblyPaths,
            referencePaths,
            outputPath);

        return 0;
    }

    private static void PrintSurface(AssemblySurface surface)
    {
        Console.WriteLine(surface.Identity);
        Console.WriteLine($"  Path: {surface.Path}");
        Console.WriteLine($"  Public and protected API:");
        Console.WriteLine($"    Types:        {surface.TypeCount}");
        Console.WriteLine($"    Constructors: {surface.ConstructorCount}");
        Console.WriteLine($"    Methods:      {surface.MethodCount}");
        Console.WriteLine($"    Properties:   {surface.PropertyCount}");
        Console.WriteLine($"    Events:       {surface.EventCount}");
        Console.WriteLine($"    Fields:       {surface.FieldCount}");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              AnalyzeAot.RoslynFacadeGenerator inspect <assembly> [<assembly> ...]
              AnalyzeAot.RoslynFacadeGenerator generate --output <directory>
                [--reference <path>] <assembly> [<assembly> ...]
              AnalyzeAot.RoslynFacadeGenerator generate-analyzer-entrypoint
                --output <file> --namespace <namespace> --language <language>
                [--reference <path>] <analyzer-assembly>

            The inspect command reports the public and protected API surface that
            will be used as input to facade and COM projection generation.

            The generate command writes one executable facade stub source file
            per input assembly together with synchronized ABI, compiler dispatch,
            and manifest trees. Unsupported concrete members explicitly throw
            PlatformNotSupportedException.

            The generate-analyzer-entrypoint command discovers every concrete
            DiagnosticAnalyzer for the selected language and writes a NativeAOT
            module bootstrap that instantiates them without runtime assembly
            scanning.
            """);
    }

    private static int PrintUsageAndFail()
    {
        PrintUsage();
        return 1;
    }
}
