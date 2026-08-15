using System.Reflection;
using System.Text;

namespace AnalyzeAot.RoslynFacadeGenerator;

internal static class AnalyzerEntryPointSourceGenerator
{
    private const string DiagnosticAnalyzerTypeName =
        "Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer";
    private const string DiagnosticAnalyzerAttributeTypeName =
        "Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerAttribute";

    public static void Generate(
        string assemblyPath,
        IEnumerable<string> referencePaths,
        string outputPath,
        string generatedNamespace,
        string language)
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
        using var loadContext = new MetadataLoadContext(
            new PathAssemblyResolver(resolverPaths));
        Assembly assembly =
            loadContext.LoadFromAssemblyPath(fullAssemblyPath);
        AnalyzerType[] analyzers = assembly
            .GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                !type.ContainsGenericParameters &&
                IsDiagnosticAnalyzer(type) &&
                SupportsLanguage(type, language))
            .Select(CreateAnalyzerType)
            .OrderBy(type => type.MetadataName, StringComparer.Ordinal)
            .ToArray();
        if (analyzers.Length == 0)
        {
            throw new InvalidOperationException(
                $"Assembly '{assembly.FullName}' contains no diagnostic analyzers for language '{language}'.");
        }

        string source = GenerateSource(
            assembly.GetName().Name ??
                throw new InvalidOperationException(
                    "The analyzer assembly has no simple name."),
            generatedNamespace,
            analyzers);
        WriteIfChanged(Path.GetFullPath(outputPath), source);
    }

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
        var source = new StringBuilder(
            """
            // <auto-generated />
            using System.Diagnostics.CodeAnalysis;
            using System.Reflection;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using AnalyzeAot.AnalyzerRuntime;
            using Microsoft.CodeAnalysis.Diagnostics;

            [assembly: TypeMapAssemblyTarget<AnalyzeAot.RoslynFacade.RoslynProxyTypeMap>(
                "Microsoft.CodeAnalysis")]
            [assembly: TypeMapAssemblyTarget<AnalyzeAot.RoslynFacade.RoslynProxyTypeMap>(
                "Microsoft.CodeAnalysis.CSharp")]

            """);
        source.Append("namespace ");
        source.Append(generatedNamespace);
        source.AppendLine(";");
        source.AppendLine();
        source.AppendLine("public static class AnalyzerEntryPoint");
        source.AppendLine("{");

        foreach (AnalyzerType analyzer in analyzers.Where(
                     type => !type.CanConstructDirectly))
        {
            source.Append(
                "    [DynamicDependency(DynamicallyAccessedMemberTypes.All, ");
            AppendStringLiteral(source, analyzer.MetadataName);
            source.Append(", ");
            AppendStringLiteral(source, assemblyName);
            source.AppendLine(")]");
        }

        source.AppendLine("    private static AnalyzerExport CreateExport()");
        source.AppendLine("    {");
        source.AppendLine("        return new AnalyzerExport(");
        for (int index = 0; index < analyzers.Count; index++)
        {
            AnalyzerType analyzer = analyzers[index];
            source.Append("            static () => ");
            if (analyzer.CanConstructDirectly)
            {
                source.Append("new global::");
                source.Append(analyzer.MetadataName.Replace('+', '.'));
                source.Append('(');
                source.Append(')');
            }
            else
            {
                source.Append("CreateAnalyzer(");
                AppendStringLiteral(source, analyzer.MetadataName);
                source.Append(')');
            }

            source.AppendLine(
                index == analyzers.Count - 1 ? ");" : ",");
        }

        source.AppendLine("    }");
        source.AppendLine();
        if (hasReflectionConstructedAnalyzers)
        {
            source.AppendLine(
                "    [UnconditionalSuppressMessage(\"Trimming\", \"IL2026\", Justification = \"The generated DynamicDependency attributes preserve non-public analyzer types.\")]");
            source.AppendLine(
                "    [UnconditionalSuppressMessage(\"Trimming\", \"IL2072\", Justification = \"The generated DynamicDependency attributes preserve non-public analyzer constructors.\")]");
            source.AppendLine(
                "    private static DiagnosticAnalyzer CreateAnalyzer(string typeName) =>");
            source.AppendLine(
                "        (DiagnosticAnalyzer)(Activator.CreateInstance(");
            source.Append("            Assembly.Load(");
            AppendStringLiteral(source, assemblyName);
            source.AppendLine(
                ").GetType(typeName, throwOnError: true)!,");
            source.AppendLine(
                "            nonPublic: true) ??");
            source.AppendLine(
                "        throw new InvalidOperationException($\"Analyzer type '{typeName}' could not be created.\"));");
            source.AppendLine();
        }

        source.AppendLine(
            "    private static readonly AnalyzerExport s_export = CreateExport();");
        source.AppendLine();
        source.AppendLine("    [UnmanagedCallersOnly(");
        source.AppendLine("        EntryPoint = AnalyzerExport.EntryPoint,");
        source.AppendLine(
            "        CallConvs = [typeof(CallConvCdecl)])]");
        source.AppendLine(
            "    public static nint GetAnalyzerModule() => s_export.GetInterface();");
        source.AppendLine("}");
        return source.ToString();
    }

    private static void AppendStringLiteral(
        StringBuilder source,
        string value)
    {
        source.Append('"');
        foreach (char character in value)
        {
            source.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ => character,
            });
        }

        source.Append('"');
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
