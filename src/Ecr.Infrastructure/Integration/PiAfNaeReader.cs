using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Reads the AF equipment tree for the one-time NAE load through the same adapter calls as the registry sync:
/// <c>DiscoverElementsAsync</c> for the elements, <c>ReadCurrentAsync</c> for their attributes. Read-only (D-44).
/// </summary>
/// <remarks>
/// Not covered by an automated test against a live AF (none is available to CI); the load logic is tested with a fake
/// <see cref="INaeAfReader"/> and this class only maps adapter results onto <see cref="NaeAfElement"/>.
/// </remarks>
public sealed class PiAfNaeReader(EcrDbContext db, IEnumerable<IExternalDataSource> sources) : INaeAfReader
{
    private const char AttributeSeparator = '|';
    private const int Chunk = 400;
    private static readonly string[] Attributes = ["Name_En", "Name_Ru", "Name_Kz", "Region", "On_Offshore"];

    /// <inheritdoc />
    public async Task<NaeAfSnapshot> ReadAsync(int dataSourceId, string rootPath, CancellationToken ct)
    {
        var dataSource = await db.DataSources.AsNoTracking()
                             .FirstOrDefaultAsync(s => s.Id == dataSourceId && s.IsActive, ct).ConfigureAwait(false)
                         ?? throw Unavailable(
                             $"Data source {dataSourceId} does not exist or is switched off.",
                             "err.ECR-INT-0503.sourceMissing",
                             ("dataSourceId", dataSourceId.ToString(CultureInfo.InvariantCulture)));

        var adapter = sources.FirstOrDefault(s => s.Transport == dataSource.Transport)
                      ?? throw Unavailable(
                          $"Transport {dataSource.Transport} is not registered.",
                          "err.ECR-INT-0503.transportNotRegistered",
                          ("transport", dataSource.Transport.ToString()));

        var listed = await adapter.DiscoverElementsAsync(dataSourceId, rootPath, ct).ConfigureAwait(false);
        var withAddress = listed.Elements.Where(e => !string.IsNullOrWhiteSpace(e.ReadAddress)).ToList();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var paths = withAddress.SelectMany(e => Attributes.Select(a => $"{e.ReadAddress}{AttributeSeparator}{a}")).Distinct().ToList();
        for (var i = 0; i < paths.Count; i += Chunk)
        {
            // Failures (an attribute a group element does not have) are expected and are not a reason to doubt the listing.
            var read = await adapter.ReadCurrentAsync(dataSourceId, paths.Skip(i).Take(Chunk).ToList(), ct).ConfigureAwait(false);
            foreach (var point in read.Values)
            {
                var text = point.ValueString ?? point.ValueNumeric?.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values[point.SourcePath] = text;
                }
            }
        }

        string? Get(SourceEntityRef e, string attribute)
            => values.TryGetValue($"{e.Address}{AttributeSeparator}{attribute}", out var v) ? v : null;

        var elements = listed.Elements
            .Select(e =>
            {
                var r = new SourceEntityRef(e.ReadAddress);
                return new NaeAfElement(
                    e.Path ?? e.Name, e.Name, Get(r, "Name_En"), Get(r, "Name_Ru"), Get(r, "Name_Kz"), Get(r, "Region"), Get(r, "On_Offshore"));
            })
            .ToList();

        return new NaeAfSnapshot(elements, listed.IsComplete);
    }

    private static BusinessRuleException Unavailable(string message, string messageKey, params (string Name, string Value)[] details)
    {
        var dictionary = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (name, value) in details)
        {
            dictionary[name] = value;
        }

        return new BusinessRuleException("ECR-INT-0503", message, dictionary);
    }

    private readonly record struct SourceEntityRef(string Address);
}

/// <summary>
/// "Where used" reader (NAE plan N2). The permit tables that point at registry entries arrive with plan N3 (the AIR permit
/// type): until then nothing refers to an NAE node, and the honest answer is an empty list, not an invented query.
/// </summary>
/// <remarks>TODO(N3): query the permit source tables (AIR_SOURCES / AIR_SOURCE_SUBSTANCES) by the referencing lookup value.</remarks>
public sealed class PermitReferenceReader : IRegistryEntryReferenceReader
{
    /// <inheritdoc />
    public Task<IReadOnlyList<RegistryEntryReferenceDto>> ListAsync(int registryDefId, long entryId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RegistryEntryReferenceDto>>([]);
}
