// src/Ecr.Application/Permits/Validity/PermitTargets.cs
using Ecr.Application.Permits.Groups;
using Ecr.Application.Ports;

namespace Ecr.Application.Permits.Validity;

/// <summary>
/// Rule (h): which Lookup targets are «permit type» — a published permit type header (<c>PRM_&lt;TYPE&gt;</c>) or a permit
/// group (hidden <c>PRMG_*</c> registry, whose entries are permits of member types). One answer for sheet columns,
/// header fields, the Issues check and the nightly scan.
/// </summary>
public static class PermitTargets
{
    /// <summary>Loads the test: one query for the headers; the group snapshot is refreshed when groups are wired.</summary>
    /// <param name="types">Permit type store.</param>
    /// <param name="groups">Group resolver; <c>null</c> — no groups.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<Func<int, bool>> LoadAsync(IPermitTypeStore types, IPermitGroupResolver? groups, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(types);

        var headers = await types.ListPublishedHeaderRegistryIdsAsync(ct).ConfigureAwait(false);
        if (groups is not null)
        {
            await groups.EnsureFreshAsync(ct).ConfigureAwait(false);
        }

        return target => headers.Contains(target) || (groups?.IsGroup(target) ?? false);
    }
}
