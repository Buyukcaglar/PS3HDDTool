namespace PS3HddTool.Core.Migration;

public static class MigrationPolicy
{
    public static void RejectPhysicalAccess() => throw new NotSupportedException(
        "Physical drive access is disabled in this experimental image-only migration fork.");
}
