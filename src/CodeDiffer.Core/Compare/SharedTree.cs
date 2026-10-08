using System.Collections.Concurrent;
using CodeDiffer.Core.Ledger;
using CodeDiffer.Core.Walk;

namespace CodeDiffer.Core.Compare;

/// <summary>
/// The left tree as one compare saw it, handed to the next compare with the same left root (compare3's base):
/// its listing and the content id proven for each of its files this run. The second compare then neither
/// re-lists the base nor re-checks it (a cache hit costs a live stat per file, which over SMB is most of a
/// warm compare's time), and both compares judge against the same snapshot of the base.
/// </summary>
public sealed class SharedTree
{
    internal string? Root { get; private set; }
    internal WalkResult? Walk { get; private set; }
    internal ConcurrentDictionary<string, ContentId> Ids { get; } = new(StringComparer.Ordinal);
    /// <summary>When the run sharing this tree began: a rehash keeps a ledger entry hashed since then (by an earlier
    /// compare of the same run), never one from before.</summary>
    internal long StartedUtcTicks { get; } = DateTime.UtcNow.Ticks;

    /// <summary>The walk to reuse for <paramref name="rootFull"/>, or null (nothing shared yet, or another root).</summary>
    internal WalkResult? For(string rootFull) => Walk is not null && string.Equals(Root, rootFull, StringComparison.OrdinalIgnoreCase) ? Walk : null;

    internal void Begin(string rootFull, WalkResult walk)
    {
        Root = rootFull;
        Walk = walk;
        Ids.Clear();
    }
}
