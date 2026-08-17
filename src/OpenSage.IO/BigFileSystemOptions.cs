namespace OpenSage.IO;

/// <summary>
/// Ordering used when enumerating the <c>.big</c> archives of a directory tree before registering
/// them.
/// </summary>
public enum BigArchiveLoadOrder
{
    /// <summary>
    /// The SAGE engine order: within a directory the archive names are sorted ascending by their
    /// <c>tolower</c>-folded bytes, all archives of a directory are registered before the archives
    /// of its subdirectories.
    /// <para>
    /// The retail engine walks an ordered <c>std::set&lt;AsciiString, less_than_nocase&gt;</c> whose
    /// comparator bottoms out in <c>_memicmp</c>, i.e. a <em>lowercase</em> fold. That places
    /// <c>'!'</c> (0x21), <c>'#'</c> (0x23), digits and <c>'_'</c> (0x5F) before letters
    /// (<c>'a'</c> = 0x61), which is exactly why the community's <c>!</c>/<c>#</c>-prefixed patch
    /// archives are registered first and — combined with first-wins registration — win every
    /// conflict.
    /// </para>
    /// <para>
    /// Note that .NET's <see cref="StringComparer.OrdinalIgnoreCase"/> folds to <em>uppercase</em>
    /// instead, which disagrees with the engine on <c>'_'</c> (0x5F sits below <c>'a'</c> but above
    /// <c>'Z'</c>) — so it must not be used for this.
    /// </para>
    /// </summary>
    Sage,

    /// <summary>
    /// The heuristics OpenSAGE uses for Generals / Zero Hour, where Generals and Zero Hour archives
    /// may share a directory and the Steam release added <c>PatchNNN.big</c> archives whose intended
    /// ordering is not documented. Only meaningful together with
    /// <see cref="BigArchiveConflictResolution.LastWins"/>, which is how those heuristics were
    /// tuned.
    /// </summary>
    ZeroHour,
}

/// <summary>
/// What happens when an archive registers a path that another archive in the same layer already
/// registered.
/// </summary>
public enum BigArchiveConflictResolution
{
    /// <summary>
    /// The first archive to claim a path keeps it; later archives are silently dropped for that
    /// path. This is the retail engine's behaviour for every base-game archive layer — the engine
    /// registers entries with <c>overwrite = FALSE</c> and its insertion is
    /// <c>if (find(name) == end() || overwrite) store</c>.
    /// </summary>
    FirstWins,

    /// <summary>
    /// Later archives overwrite earlier ones. The engine uses this — <c>overwrite = TRUE</c> — only
    /// for the <c>-mod</c> archive layers (and for <c>lang\English*.big</c>), which is why a mod's
    /// archives override the base game even though the base game registered first.
    /// </summary>
    LastWins,
}

/// <summary>
/// Configuration for a single <see cref="BigFileSystem"/> archive layer.
/// </summary>
public sealed record BigFileSystemOptions
{
    /// <summary>
    /// A base-game archive layer, as the engine registers ROTWK's, its <c>apt\</c> subtree's and the
    /// BFME2 base install's archives.
    /// </summary>
    public static readonly BigFileSystemOptions BaseGame = new()
    {
        LoadOrder = BigArchiveLoadOrder.Sage,
        ConflictResolution = BigArchiveConflictResolution.FirstWins,
    };

    /// <summary>
    /// A <c>-mod</c> archive layer. The engine registers these with <c>overwrite = TRUE</c>, so
    /// within the layer the semantics invert to last-wins.
    /// </summary>
    public static readonly BigFileSystemOptions ModOverlay = new()
    {
        LoadOrder = BigArchiveLoadOrder.Sage,
        ConflictResolution = BigArchiveConflictResolution.LastWins,
    };

    /// <summary>
    /// The Generals / Zero Hour layer, preserving OpenSAGE's existing (empirically tuned) ordering
    /// heuristics and last-wins registration for those two games.
    /// </summary>
    public static readonly BigFileSystemOptions GeneralsZeroHour = new()
    {
        LoadOrder = BigArchiveLoadOrder.ZeroHour,
        ConflictResolution = BigArchiveConflictResolution.LastWins,
    };

    public BigArchiveLoadOrder LoadOrder { get; init; } = BigArchiveLoadOrder.Sage;

    public BigArchiveConflictResolution ConflictResolution { get; init; } = BigArchiveConflictResolution.FirstWins;
}
