namespace CodeDiffer.Core.Compare;

/// <summary>
/// Strict-in guards for a compare. Honors the honesty contract: a missing / mistyped root is a hard
/// error naming the offending path — NEVER silently treated as an empty tree (the prototype's "silent
/// success on a missing path, everything reported added/deleted" bug).
/// </summary>
public static class InputValidation
{
    public static void ValidateTrees(string left, string right)
    {
        RequireExistingDirectory(left, "left");
        RequireExistingDirectory(right, "right");
    }

    private static void RequireExistingDirectory(string path, string which)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"compare root ({which}) is empty");
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"compare root ({which}) does not exist or is not a directory: {path}");
    }
}
