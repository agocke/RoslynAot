using System.Reflection;
using System.Text;
using StaticCs;

namespace RoslynAot.RoslynFacadeGenerator;

internal static class AnalyzerEntryPointSourceGenerator
{
    private const string DiagnosticAnalyzerTypeName =
        "Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer";
    private const string DiagnosticAnalyzerAttributeTypeName =
        "Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerAttribute";

    /// <summary>
    /// Lists the analyzer metadata names an entry point would instantiate,
    /// so a build matrix can enumerate them without loading the assembly
    /// itself. Same discovery as <see cref="Generate"/>.
    /// </summary>
    public static IReadOnlyList<string> List(
        string assemblyPath,
        IEnumerable<string> referencePaths,
        string language)
    {
        using MetadataLoadContext loadContext = CreateLoadContext(
            assemblyPath,
            referencePaths,
            out Assembly assembly);
        return Discover(assembly, language, analyzerFilter: null)
            .Select(type => type.MetadataName)
            .ToArray();
    }

    public static void Generate(
        string assemblyPath,
        IEnumerable<string> referencePaths,
        string outputPath,
        string generatedNamespace,
        string language,
        IReadOnlyCollection<string>? analyzerFilter = null)
    {
        using MetadataLoadContext loadContext = CreateLoadContext(
            assemblyPath,
            referencePaths,
            out Assembly assembly);
        AnalyzerType[] analyzers =
            Discover(assembly, language, analyzerFilter);
        if (analyzers.Length == 0)
        {
            throw new InvalidOperationException(
                analyzerFilter is null
                    ? $"Assembly '{assembly.FullName}' contains no diagnostic analyzers for language '{language}'."
                    : $"Assembly '{assembly.FullName}' contains no diagnostic analyzers for language '{language}' matching the requested filter.");
        }

        if (analyzerFilter is not null)
        {
            // A misspelled type would otherwise silently produce a module with
            // fewer analyzers than asked for, which a size measurement would
            // then report as a spurious win.
            var found = analyzers
                .Select(type => type.MetadataName)
                .ToHashSet(StringComparer.Ordinal);
            string[] missing = analyzerFilter
                .Where(name => !found.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Assembly '{assembly.FullName}' has no analyzer for " +
                    $"language '{language}' named: {string.Join(", ", missing)}.");
            }
        }

        string source = GenerateSource(
            assembly.GetName().Name ??
                throw new InvalidOperationException(
                    "The analyzer assembly has no simple name."),
            generatedNamespace,
            analyzers);
        WriteIfChanged(Path.GetFullPath(outputPath), source);
    }

    private static MetadataLoadContext CreateLoadContext(
        string assemblyPath,
        IEnumerable<string> referencePaths,
        out Assembly assembly)
    {
        string fullAssemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullAssemblyPath))
        {
            throw new FileNotFoundException(
                "Analyzer assembly was not found.",
                fullAssemblyPath);
        }

        string[] resolverPaths = CreateResolverPaths(
            fullAssemblyPath,
            referencePaths);
        var loadContext = new MetadataLoadContext(
            new PathAssemblyResolver(resolverPaths));
        assembly = loadContext.LoadFromAssemblyPath(fullAssemblyPath);
        return loadContext;
    }

    private static AnalyzerType[] Discover(
        Assembly assembly,
        string language,
        IReadOnlyCollection<string>? analyzerFilter) =>
        assembly
            .GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                !type.ContainsGenericParameters &&
                IsDiagnosticAnalyzer(type) &&
                SupportsLanguage(type, language))
            .Select(CreateAnalyzerType)
            .Where(type =>
                analyzerFilter is null ||
                analyzerFilter.Contains(type.MetadataName))
            .OrderBy(type => type.MetadataName, StringComparer.Ordinal)
            .ToArray();

    private static string[] CreateResolverPaths(
        string assemblyPath,
        IEnumerable<string> referencePaths)
    {
        var pathsByAssemblyName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddPath(assemblyPath, pathsByAssemblyName, replace: true);
        AddDirectory(
            Path.GetDirectoryName(assemblyPath)!,
            pathsByAssemblyName);
        foreach (string referencePath in referencePaths)
        {
            string fullPath = Path.GetFullPath(referencePath);
            if (Directory.Exists(fullPath))
            {
                AddDirectory(fullPath, pathsByAssemblyName);
            }
            else if (File.Exists(fullPath))
            {
                AddPath(fullPath, pathsByAssemblyName, replace: true);
            }
            else
            {
                throw new FileNotFoundException(
                    "Analyzer reference path was not found.",
                    fullPath);
            }
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            is string trustedPlatformAssemblies)
        {
            foreach (string path in trustedPlatformAssemblies.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                AddPath(path, pathsByAssemblyName, replace: false);
            }
        }

        return pathsByAssemblyName.Values.ToArray();
    }

    private static void AddDirectory(
        string directory,
        Dictionary<string, string> pathsByAssemblyName)
    {
        foreach (string path in Directory.EnumerateFiles(
                     directory,
                     "*.dll",
                     SearchOption.TopDirectoryOnly))
        {
            AddPath(path, pathsByAssemblyName, replace: false);
        }
    }

    private static void AddPath(
        string path,
        Dictionary<string, string> pathsByAssemblyName,
        bool replace)
    {
        try
        {
            string? assemblyName =
                AssemblyName.GetAssemblyName(path).Name;
            if (assemblyName is null)
            {
                return;
            }

            if (replace || !pathsByAssemblyName.ContainsKey(assemblyName))
            {
                pathsByAssemblyName[assemblyName] = path;
            }
        }
        catch (BadImageFormatException)
        {
        }
    }

    private static bool IsDiagnosticAnalyzer(Type type)
    {
        for (Type? current = type.BaseType;
             current is not null;
             current = current.BaseType)
        {
            if (current.FullName == DiagnosticAnalyzerTypeName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SupportsLanguage(Type type, string language)
    {
        foreach (CustomAttributeData attribute in
                 type.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName !=
                DiagnosticAnalyzerAttributeTypeName)
            {
                continue;
            }

            foreach (CustomAttributeTypedArgument argument in
                     attribute.ConstructorArguments)
            {
                if (argument.Value is string value &&
                    value == language)
                {
                    return true;
                }

                if (argument.Value is
                    IReadOnlyCollection<CustomAttributeTypedArgument>
                    values &&
                    values.Any(value =>
                        value.Value is string item &&
                        item == language))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static AnalyzerType CreateAnalyzerType(Type type)
    {
        ConstructorInfo? constructor = type.GetConstructor(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        if (constructor is null)
        {
            throw new InvalidOperationException(
                $"Analyzer type '{type.FullName}' has no parameterless constructor.");
        }

        return new AnalyzerType(
            type.FullName ??
                throw new InvalidOperationException(
                    "An analyzer type has no metadata name."),
            type.IsVisible && constructor.IsPublic);
    }

    private static string GenerateSource(
        string assemblyName,
        string generatedNamespace,
        IReadOnlyList<AnalyzerType> analyzers)
    {
        bool hasReflectionConstructedAnalyzers =
            analyzers.Any(type => !type.CanConstructDirectly);
        var source = new IndentingBuilder(
            """
            // <auto-generated />
            using System.Diagnostics.CodeAnalysis;
            using System.Reflection;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using RoslynAot.AnalyzerRuntime;
            using Microsoft.CodeAnalysis.Diagnostics;

            [assembly: TypeMapAssemblyTarget<RoslynAot.RoslynFacade.RoslynProxyTypeMap>(
                "Microsoft.CodeAnalysis")]
            [assembly: TypeMapAssemblyTarget<RoslynAot.RoslynFacade.RoslynProxyTypeMap>(
                "Microsoft.CodeAnalysis.CSharp")]

            """);
        source.AppendLine($"namespace {generatedNamespace};");
        source.AppendLine("");
        source.AppendLine("public static class AnalyzerEntryPoint");
        source.AppendLine("{");
        source.Indent();

        foreach (AnalyzerType analyzer in analyzers.Where(
                     type => !type.CanConstructDirectly))
        {
            source.AppendLine(
                "[DynamicDependency(DynamicallyAccessedMemberTypes.All, " +
                $"{GetStringLiteral(analyzer.MetadataName)}, " +
                $"{GetStringLiteral(assemblyName)})]");
        }

        source.AppendLine("private static AnalyzerExport CreateExport()");
        source.AppendLine("{");
        source.Indent();
        source.AppendLine("return new AnalyzerExport(");
        source.Indent();
        for (int index = 0; index < analyzers.Count; index++)
        {
            AnalyzerType analyzer = analyzers[index];
            string construction = analyzer.CanConstructDirectly
                ? $"new global::{analyzer.MetadataName.Replace('+', '.')}()"
                : $"CreateAnalyzer({GetStringLiteral(analyzer.MetadataName)})";
            source.AppendLine(
                $"static () => {construction}" +
                (index == analyzers.Count - 1 ? ");" : ","));
        }

        source.Dedent();
        source.Dedent();
        source.AppendLine("}");
        source.AppendLine("");
        if (hasReflectionConstructedAnalyzers)
        {
            source.AppendLine(
                "[UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"The generated DynamicDependency attributes preserve non-public analyzer types.\")]");
            source.AppendLine(
                "[UnconditionalSuppressMessage(\"Trimming\", \"IL2072\", Justification = \"The generated DynamicDependency attributes preserve non-public analyzer constructors.\")]");
            source.AppendLine(
                "private static DiagnosticAnalyzer CreateAnalyzer(string typeName) =>");
            source.Indent();
            source.AppendLine("(DiagnosticAnalyzer)(Activator.CreateInstance(");
            source.Indent();
            source.AppendLine(
                $"Assembly.Load({GetStringLiteral(assemblyName)})" +
                ".GetType(typeName, throwOnError: true)!,");
            source.AppendLine("nonPublic: true) ??");
            source.Dedent();
            source.AppendLine(
                "throw new InvalidOperationException($\"Analyzer type '{typeName}' could not be created.\"));");
            source.Dedent();
            source.AppendLine("");
        }

        source.AppendLine(
            "private static readonly AnalyzerExport s_export = CreateExport();");
        source.AppendLine("");
        source.AppendLine("[UnmanagedCallersOnly(");
        source.Indent();
        source.AppendLine("EntryPoint = AnalyzerExport.EntryPoint,");
        source.AppendLine("CallConvs = [typeof(CallConvCdecl)])]");
        source.Dedent();
        source.AppendLine(
            "public static nint GetAnalyzerModule() => s_export.GetInterface();");
        source.Dedent();
        source.AppendLine("}");
        return source.ToString();
    }

    private static string GetStringLiteral(string value)
    {
        var literal = new StringBuilder();
        literal.Append('"');
        foreach (char character in value)
        {
            literal.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => character.ToString(),
            });
        }

        literal.Append('"');
        return literal.ToString();
    }

    private static void WriteIfChanged(string outputPath, string source)
    {
        if (File.Exists(outputPath) &&
            File.ReadAllText(outputPath) == source)
        {
            return;
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, source);
    }

    private sealed record AnalyzerType(
        string MetadataName,
        bool CanConstructDirectly);
}
