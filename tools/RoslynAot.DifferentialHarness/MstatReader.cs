using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RoslynAot.DifferentialHarness;

internal sealed record ModuleMetricsReport(
    int RetainedTypeCount,
    int RetainedMethodCount);

/// <summary>
/// Counts the rows ILC records in a <c>.mstat</c> file, produced by
/// <c>IlcGenerateMstatFile=true</c>.
/// </summary>
/// <remarks>
/// An mstat is a managed assembly whose <c>&lt;Module&gt;</c> type carries one
/// method per table (<c>Types</c>, <c>Methods</c>, ...). Each row is encoded in
/// the method's IL as a <c>ldtoken</c> for the entity followed by a
/// variable-length run of integer pushes for its sizes, so counting rows means
/// walking the instruction stream rather than scanning bytes: a 0xD0 byte
/// inside a 4-byte <c>ldc.i4</c> operand would otherwise count as a row.
/// </remarks>
internal static class MstatReader
{
    public static ModuleMetricsReport Read(string mstatPath)
    {
        using FileStream stream = File.OpenRead(mstatPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        var rowCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            byte[]? il = peReader
                .GetMethodBody(method.RelativeVirtualAddress)
                .GetILBytes();
            if (il is null)
            {
                continue;
            }

            rowCounts[metadata.GetString(method.Name)] = CountLoadTokens(il);
        }

        rowCounts.TryGetValue("Types", out int types);
        rowCounts.TryGetValue("Methods", out int methods);
        if (types == 0)
        {
            throw new InvalidDataException(
                $"'{mstatPath}' has no Types rows. Either the module was not " +
                "built with IlcGenerateMstatFile, or the mstat encoding " +
                "changed.");
        }

        return new ModuleMetricsReport(types, methods);
    }

    private static int CountLoadTokens(ReadOnlySpan<byte> il)
    {
        int count = 0;
        int offset = 0;
        while (offset < il.Length)
        {
            byte opcode = il[offset++];
            switch (opcode)
            {
                case 0xD0: // ldtoken <token>
                    count++;
                    offset += 4;
                    break;
                case 0x20: // ldc.i4 <int32>
                    offset += 4;
                    break;
                case 0x21: // ldc.i8 <int64>
                    offset += 8;
                    break;
                case 0x1F: // ldc.i4.s <int8>
                    offset += 1;
                    break;
                case 0x72: // ldstr <token>
                    offset += 4;
                    break;
                case >= 0x15 and <= 0x1E: // ldc.i4.m1 .. ldc.i4.8
                case 0x00: // nop
                case 0x26: // pop
                case 0x2A: // ret
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unexpected opcode 0x{opcode:x2} at IL offset " +
                        $"{offset - 1} in the mstat table. The mstat encoding " +
                        "changed; the row count cannot be trusted.");
            }
        }

        return count;
    }
}
