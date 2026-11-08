// src/Ecr.Application/Permits/Validity/PermitTargets.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Permits.Validity;

/// <summary>
/// Rule (h): which Lookup targets are «permit type» — a published permit type header (<c>PRM_&lt;TYPE&gt;</c>) or a permit
/// group (hidden <c>PRMG_*</c> registry, whose entries are permits of member types). One answer for sheet columns,
/// header fields and the Issues check.
/// </summary>
/// <remarks>
/// ⛔ From the in-memory <see cref="IPermitRegistryIndex"/>, not from a store query or the group resolver: the write path of
/// every Lookup PATCH asks this, and a Land PATCH must not pay a round-trip for it (<c>PatchCellsQueryCountTests</c>).
/// </remarks>
public static class PermitTargets
{
    /// <summary>Loads the test from the index (no round-trip on a warm request).</summary>
    /// <param name="index">Permit registry index.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<Func<int, bool>> LoadAsync(IPermitRegistryIndex index, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(index);

        var targets = await index.PermitTargetIdsAsync(ct).ConfigureAwait(false);
        return targets.Contains;
    }
}
