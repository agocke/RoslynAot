using Microsoft.CodeAnalysis;
using Microsoft.DotNet.ApiSymbolExtensions;
using Microsoft.DotNet.ApiSymbolExtensions.Logging;
using Microsoft.DotNet.GenAPI;
using System.Reflection;

namespace RoslynAot.RoslynFacadeGenerator;

internal static class FacadeSourceGenerator
{
    public static void Generate(
        IReadOnlyList<string> assemblyPaths,
        IReadOnlyList<string> referencePaths,
        string outputPath)
    {
        string[] assemblies =
            [.. assemblyPaths.Select(Path.GetFullPath)];
        string[] references =
            [.. GetReferencePaths(assemblies, referencePaths)];
        string outputDirectory = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(outputDirectory);
        string temporaryDirectory =
            Path.Combine(outputDirectory, ".intermediate");
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }

        Directory.CreateDirectory(temporaryDirectory);
        string facadesDirectory = Path.Combine(
            outputDirectory,
            "Facades");
        if (Directory.Exists(facadesDirectory))
        {
            Directory.Delete(facadesDirectory, recursive: true);
        }

        Directory.CreateDirectory(facadesDirectory);

        var log = new ConsoleLog(MessageImportance.Normal);
        try
        {
            (
                IAssemblySymbolLoader loader,
                Dictionary<string, IAssemblySymbol> assemblySymbols) =
                AssemblySymbolLoader.CreateFromFiles(
                    log,
                    assemblies,
                    references,
                    assembliesToExclude: [],
                    respectInternals: false);
            ProjectionModel model =
                ProjectionModel.Create(assemblySymbols.Values);
            var transform = new FacadeDeclarationTransform(model);

            GenAPIApp.Run(
                log,
                loader,
                assemblySymbols,
                temporaryDirectory,
                headerFile: null,
                exceptionMessage:
                    FacadeDeclarationTransform.UnsupportedMessage,
                excludeApiFiles: null,
                excludeAttributesFiles: null,
                respectInternals: false,
                includeAssemblyAttributes: true,
                transform.Transform);

            if (log.HasLoggedErrors)
            {
                throw new InvalidOperationException(
                    "Facade source generation reported errors.");
            }

            foreach (string assemblyPath in assemblies)
            {
                string assemblyName =
                    AssemblyName.GetAssemblyName(assemblyPath).Name
                    ?? throw new InvalidOperationException(
                        $"Assembly '{assemblyPath}' has no name.");
                string combinedSourcePath = Path.Combine(
                    temporaryDirectory,
                    $"{assemblyName}.cs");
                GeneratedSourceLayout.WriteAssembly(
                    combinedSourcePath,
                    facadesDirectory,
                    assemblyName);
            }

            ProjectionOutputEmitter.WriteFacadeRuntime(
                model,
                facadesDirectory);
            ProjectionOutputEmitter.WriteNonFacadeOutputs(
                model,
                outputDirectory);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static IEnumerable<string> GetReferencePaths(
        IReadOnlyList<string> assemblyPaths,
        IReadOnlyList<string> referencePaths)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in referencePaths)
        {
            paths.Add(Path.GetFullPath(path));
        }

        foreach (string assemblyPath in assemblyPaths)
        {
            string? directory = Path.GetDirectoryName(assemblyPath);
            if (directory is not null)
            {
                paths.Add(directory);
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string
            trustedPlatformAssemblies)
        {
            foreach (string path in trustedPlatformAssemblies.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries))
            {
                string? directory = Path.GetDirectoryName(path);
                if (directory is not null)
                {
                    paths.Add(directory);
                }
            }
        }

        return paths;
    }
}
