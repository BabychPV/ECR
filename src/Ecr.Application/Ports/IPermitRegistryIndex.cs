// src/Ecr.Application/Ports/IPermitRegistryIndex.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Rule (h): which registries are permit-type Lookup targets — headers of PUBLISHED permit types (<c>PRM_&lt;TYPE&gt;</c>)
/// and permit groups (hidden <c>PRMG_*</c>). Read on the hot write path (every PATCH of a table with a Lookup column),
/// so the answer comes from memory.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Zero round-trips on a warm request: the implementation keeps the set in a process-wide cache refreshed at most
/// once per <see cref="RefreshInterval"/> (ratchet <c>PatchCellsQueryCountTests</c>: a Land PATCH of Lookup cells must not
/// pay for the permit rule). Not the group resolver either — a Land target never asks it (<c>EnsureFreshCalls == 0</c>).
/// </para>
/// <para>
/// ⚠ Staleness is bounded and harmless: a permit type published a moment ago becomes «permit-type» within the interval;
/// a column can target it only after a template version is published with it, which takes longer.
/// </para>
/// </remarks>
public interface IPermitRegistryIndex
{
    /// <summary>How old the cached set may get.</summary>
    public static TimeSpan RefreshInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Registry ids of permit type headers (published) and permit groups.</summary>
    /// <param name="ct">Cancellation.</param>
    public ValueTask<IReadOnlySet<int>> PermitTargetIdsAsync(CancellationToken ct);

    /// <summary>
    /// Drops the cached set: the next read reloads it. Called after a permit type is published and after a permit group
    /// is created — the moments the set changes.
    /// </summary>
    /// <remarks>
    /// ⛔ e2e 42630598595 (full run): a type published and used within <see cref="RefreshInterval"/> of an earlier read was
    /// not «permit-type» yet, so a months-in-columns permit column fell back to the period-end check and a legit permit was
    /// refused (ECR-CELL-4223 entryNotValidOnDate). «Harmless staleness» held only when a human was slower than 30 s.
    /// </remarks>
    public void Invalidate()
    {
    }
}
