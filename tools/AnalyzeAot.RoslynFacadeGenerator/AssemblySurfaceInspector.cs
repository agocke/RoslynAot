using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AnalyzeAot.RoslynFacadeGenerator;

internal sealed record AssemblySurface(
    string Path,
    string Identity,
    int TypeCount,
    int ConstructorCount,
    int MethodCount,
    int PropertyCount,
    int EventCount,
    int FieldCount);

internal static class AssemblySurfaceInspector
{
    public static AssemblySurface Inspect(string assemblyPath)
    {
        string fullPath = Path.GetFullPath(assemblyPath);
        AssemblyName assemblyName = AssemblyName.GetAssemblyName(fullPath);

        using FileStream stream = File.OpenRead(fullPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException(
                "The file does not contain managed metadata.",
                fullPath);
        }

        MetadataReader reader = peReader.GetMetadataReader();
        var visibleTypes = new Dictionary<TypeDefinitionHandle, bool>();
        int typeCount = 0;
        int constructorCount = 0;
        int methodCount = 0;
        int propertyCount = 0;
        int eventCount = 0;
        int fieldCount = 0;

        foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
        {
            if (!IsExternallyVisible(reader, typeHandle, visibleTypes))
            {
                continue;
            }

            typeCount++;
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            var accessorMethods = new HashSet<MethodDefinitionHandle>();

            foreach (PropertyDefinitionHandle propertyHandle in
                type.GetProperties())
            {
                PropertyAccessors accessors =
                    reader.GetPropertyDefinition(propertyHandle).GetAccessors();
                Add(accessorMethods, accessors.Getter);
                Add(accessorMethods, accessors.Setter);
                foreach (MethodDefinitionHandle other in accessors.Others)
                {
                    Add(accessorMethods, other);
                }

                if (IsExternallyVisible(reader, accessors.Getter)
                    || IsExternallyVisible(reader, accessors.Setter)
                    || accessors.Others.Any(
                        handle => IsExternallyVisible(reader, handle)))
                {
                    propertyCount++;
                }
            }

            foreach (EventDefinitionHandle eventHandle in type.GetEvents())
            {
                EventAccessors accessors =
                    reader.GetEventDefinition(eventHandle).GetAccessors();
                Add(accessorMethods, accessors.Adder);
                Add(accessorMethods, accessors.Remover);
                Add(accessorMethods, accessors.Raiser);
                foreach (MethodDefinitionHandle other in accessors.Others)
                {
                    Add(accessorMethods, other);
                }

                if (IsExternallyVisible(reader, accessors.Adder)
                    || IsExternallyVisible(reader, accessors.Remover)
                    || IsExternallyVisible(reader, accessors.Raiser)
                    || accessors.Others.Any(
                        handle => IsExternallyVisible(reader, handle)))
                {
                    eventCount++;
                }
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                if (accessorMethods.Contains(methodHandle)
                    || !IsExternallyVisible(reader, methodHandle))
                {
                    continue;
                }

                MethodDefinition method =
                    reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) is ".ctor" or ".cctor")
                {
                    constructorCount++;
                }
                else
                {
                    methodCount++;
                }
            }

            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                if (IsExternallyVisible(field.Attributes))
                {
                    fieldCount++;
                }
            }
        }

        return new AssemblySurface(
            fullPath,
            assemblyName.FullName,
            typeCount,
            constructorCount,
            methodCount,
            propertyCount,
            eventCount,
            fieldCount);
    }

    private static bool IsExternallyVisible(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        Dictionary<TypeDefinitionHandle, bool> cache)
    {
        if (cache.TryGetValue(handle, out bool visible))
        {
            return visible;
        }

        TypeDefinition type = reader.GetTypeDefinition(handle);
        TypeAttributes visibility =
            type.Attributes & TypeAttributes.VisibilityMask;
        visible = visibility switch
        {
            TypeAttributes.Public => true,
            TypeAttributes.NestedPublic
                or TypeAttributes.NestedFamily
                or TypeAttributes.NestedFamORAssem =>
                    IsExternallyVisible(
                        reader,
                        type.GetDeclaringType(),
                        cache),
            _ => false,
        };

        cache.Add(handle, visible);
        return visible;
    }

    private static bool IsExternallyVisible(
        MetadataReader reader,
        MethodDefinitionHandle handle) =>
        !handle.IsNil
        && IsExternallyVisible(reader.GetMethodDefinition(handle).Attributes);

    private static bool IsExternallyVisible(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask) is
            MethodAttributes.Public
            or MethodAttributes.Family
            or MethodAttributes.FamORAssem;

    private static bool IsExternallyVisible(FieldAttributes attributes) =>
        (attributes & FieldAttributes.FieldAccessMask) is
            FieldAttributes.Public
            or FieldAttributes.Family
            or FieldAttributes.FamORAssem;

    private static void Add(
        HashSet<MethodDefinitionHandle> handles,
        MethodDefinitionHandle handle)
    {
        if (!handle.IsNil)
        {
            handles.Add(handle);
        }
    }
}
