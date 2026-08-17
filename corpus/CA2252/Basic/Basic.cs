// CA2252: This API requires opting into preview features. The caller is
// not itself marked [RequiresPreviewFeatures] and the compilation does not
// set EnablePreviewFeatures.
using System.Runtime.Versioning;

public static class PreviewApiConsumer
{
    [RequiresPreviewFeatures]
    public static void PreviewOnly()
    {
    }

    public static void Call() => PreviewOnly();
}
