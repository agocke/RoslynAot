using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.DotNet.GenAPI;

namespace AnalyzeAot.RoslynFacadeGenerator;

internal static class GeneratedSourceLayout
{
    public static int WriteAssembly(
        string combinedSourcePath,
        string outputRoot,
        string assemblyName)
    {
        SyntaxTree syntaxTree =
            CSharpSyntaxTree.ParseText(File.ReadAllText(combinedSourcePath));
        Diagnostic? error = syntaxTree.GetDiagnostics()
            .FirstOrDefault(
                diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            throw new InvalidOperationException(
                $"Generated source could not be parsed: {error}");
        }

        var root = (CompilationUnitSyntax)syntaxTree.GetRoot();
        string safeAssemblyName = GetSafePathSegment(assemblyName);
        string assemblyDirectory = Path.Combine(
            outputRoot,
            safeAssemblyName);
        if (Directory.Exists(assemblyDirectory))
        {
            Directory.Delete(assemblyDirectory, recursive: true);
        }

        Directory.CreateDirectory(assemblyDirectory);
        File.Delete(Path.Combine(outputRoot, $"{safeAssemblyName}.cs"));

        int fileCount = 0;
        if (root.AttributeLists.Count > 0)
        {
            CompilationUnitSyntax assemblyInfo = CreateCompilationUnit(root)
                .WithAttributeLists(root.AttributeLists);
            WriteFile(
                Path.Combine(assemblyDirectory, "AssemblyInfo.cs"),
                assemblyInfo);
            fileCount++;
        }

        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MemberDeclarationSyntax member in root.Members)
        {
            switch (member)
            {
                case NamespaceDeclarationSyntax namespaceDeclaration:
                    foreach (MemberDeclarationSyntax typeDeclaration in
                        namespaceDeclaration.Members)
                    {
                        string namespaceName =
                            namespaceDeclaration.Name.ToString();
                        string directory = GetNamespaceDirectory(
                            assemblyDirectory,
                            namespaceName);
                        string typePath = GetUniqueTypePath(
                            directory,
                            typeDeclaration,
                            usedPaths);
                        NamespaceDeclarationSyntax singleTypeNamespace =
                            namespaceDeclaration.WithMembers(
                                SyntaxFactory.SingletonList(typeDeclaration));
                        WriteFile(
                            typePath,
                            CreateCompilationUnit(root).AddMembers(
                                singleTypeNamespace));
                        fileCount++;
                    }

                    break;

                default:
                    string globalTypePath = GetUniqueTypePath(
                        assemblyDirectory,
                        member,
                        usedPaths);
                    WriteFile(
                        globalTypePath,
                        CreateCompilationUnit(root).AddMembers(member));
                    fileCount++;
                    break;
            }
        }

        return fileCount;
    }

    private static CompilationUnitSyntax CreateCompilationUnit(
        CompilationUnitSyntax source) =>
        SyntaxFactory.CompilationUnit()
            .WithExterns(source.Externs)
            .WithUsings(source.Usings);

    private static string GetNamespaceDirectory(
        string assemblyDirectory,
        string namespaceName)
    {
        string directory = assemblyDirectory;
        foreach (string segment in namespaceName.Split('.'))
        {
            directory = Path.Combine(
                directory,
                GetSafePathSegment(segment));
        }

        return directory;
    }

    private static string GetUniqueTypePath(
        string directory,
        MemberDeclarationSyntax declaration,
        HashSet<string> usedPaths)
    {
        (string name, int arity) = declaration switch
        {
            BaseTypeDeclarationSyntax type =>
                (type.Identifier.ValueText, GetArity(type)),
            DelegateDeclarationSyntax @delegate =>
                (@delegate.Identifier.ValueText,
                    @delegate.TypeParameterList?.Parameters.Count ?? 0),
            _ => throw new InvalidOperationException(
                $"Unexpected generated declaration '{declaration.Kind()}'."),
        };

        string safeName = GetSafePathSegment(name);
        string aritySuffix = arity == 0 ? string.Empty : $".{arity}";
        string path = Path.Combine(
            directory,
            $"{safeName}{aritySuffix}.cs");
        int collision = 1;
        while (!usedPaths.Add(path))
        {
            path = Path.Combine(
                directory,
                $"{safeName}{aritySuffix}.{collision++}.cs");
        }

        return path;
    }

    private static int GetArity(BaseTypeDeclarationSyntax type) =>
        type switch
        {
            TypeDeclarationSyntax declaration =>
                declaration.TypeParameterList?.Parameters.Count ?? 0,
            _ => 0,
        };

    private static string GetSafePathSegment(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string safeValue = string.Concat(
            value.Select(
                character => invalidCharacters.Contains(character)
                    ? '_'
                    : character));

        return safeValue is "." or ".." ? $"_{safeValue}" : safeValue;
    }

    private static void WriteFile(
        string path,
        CompilationUnitSyntax compilationUnit)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        SyntaxNode normalized = compilationUnit
            .WithoutLeadingTrivia()
            .NormalizeWhitespace(eol: Environment.NewLine);
        File.WriteAllText(
            path,
            CSharpFileBuilder.DefaultFileHeader
                + normalized.ToFullString()
                + Environment.NewLine);
    }
}
