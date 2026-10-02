namespace CodeDiffer.Mcp;

/// <summary>
/// Placeholder for the agent-facing MCP server. The paged, bounded tool surface is wired on the
/// first-party .NET MCP SDK in a later step; it is deliberately small so it costs the agent almost no
/// standing tokens (the CodeCompass lesson). See docs/OUTPUT.md §4.
/// </summary>
public static class McpServerInfo
{
    public const string ServerName = "codediffer";

    /// <summary>The planned tool surface — small by design; diffs page by reference, never dump.</summary>
    public static readonly IReadOnlyList<string> PlannedTools =
    [
        "start_compare",    // -> compare_id (streams progress, cancellable)
        "get_summary",      // constant-size counts by status/reason
        "list_files",       // paged/filtered; hides identical by default
        "get_file_diff",    // one file, bounded, "summary only" fallback
        "get_stats",        // totals / byte mass / top movers
        "export_changeset", // A->B changeset (patch-applicable)
        "apply_changeset",  // dry-run onto C: per-hunk applied|fuzzy|conflict
    ];
}
