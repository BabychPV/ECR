// src/Ecr.Application/Permits/Bindings/PermitResolver.cs
using Ecr.Application.Ports;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Bindings;

/// <summary>
/// Вибір дозволу за правилом прив'язки над реєстром шапок типу (Permits, PM-09).
/// </summary>
/// <remarks>
/// <para>
/// <c>ByFacilityPeriodEnd</c>: дозвіл, чине розташування (<c>Location</c>) якого збігається з об'єктом документа і який
/// активний, не видалений та чинний на кінець періоду - вибір у <see cref="PermitSelection"/> (чиста функція).
/// <c>FromHeaderLookup</c>: запис, обраний у полі шапки; нечинний на кінець періоду віддається теж, але з
/// попередженням - <c>REGSUM</c> над ним мовчки дасть нуль, бо знімок його не бачить.
/// </para>
/// <para>
/// ⚠ Шапки читаються з реєстру напряму (<see cref="IRegistryStore"/>), а не зі знімка: знімок не віддає ні
/// <c>ValidFrom</c>, ні активності, а вони потрібні і для вибору, і для пояснення «чому дозволу немає».
/// </para>
/// </remarks>
public sealed class PermitResolver(IRegistryStore registries) : IPermitResolver
{
    /// <inheritdoc />
    public async Task<PermitResolution> ResolveAsync(PermitResolveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = await registries.FindDefinitionByIdAsync(request.HeaderRegistryDefId, ct).ConfigureAwait(false);
        if (header is null)
        {
            return None("noPermit");
        }

        var numberField = header.Fields.FirstOrDefault(f => f.Code == "Permit_Number")?.Id;
        var locationField = header.Fields.FirstOrDefault(f => f.Code == PermitBindingFacility.PermitLocationFieldCode)?.Id;

        return request.Rule == PermitSelectionRule.FromHeaderLookup
            ? await FromHeaderAsync(request, numberField, ct).ConfigureAwait(false)
            : await ByFacilityAsync(request, numberField, locationField, ct).ConfigureAwait(false);
    }

    private async Task<PermitResolution> ByFacilityAsync(
        PermitResolveRequest request, int? numberField, int? locationField, CancellationToken ct)
    {
        var names = request.FacilityNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count == 0)
        {
            return None("noFacility");
        }

        var entries = await registries.ListEntriesAsync(request.HeaderRegistryDefId, ct).ConfigureAwait(false);
        var values = await registries
            .ListValuesForEntriesAsync([.. entries.Where(e => !e.IsDeleted).Select(e => e.Id)], ct)
            .ConfigureAwait(false);

        string? Text(long entryId, int? fieldId)
            => fieldId is null
                ? null
                : values.FirstOrDefault(v => v.RegistryEntryId == entryId && v.RegistryFieldDefId == fieldId)?.ValueString;

        var candidates = entries.Select(e => new PermitCandidate(
            e.Id, Text(e.Id, numberField), Text(e.Id, locationField), e.ValidFrom, e.ValidTo, e.IsActive, e.IsDeleted));
        var chosen = PermitSelection.ChooseByFacility(candidates, names, request.PeriodEnd);

        return chosen is null
            ? None("noPermit")
            : new PermitResolution(chosen.EntryId, chosen.Number, chosen.ValidFrom, chosen.ValidTo, null, []);
    }

    private async Task<PermitResolution> FromHeaderAsync(PermitResolveRequest request, int? numberField, CancellationToken ct)
    {
        if (request.HeaderEntryId is not { } entryId)
        {
            return None("headerEmpty");
        }

        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != request.HeaderRegistryDefId)
        {
            return None("headerUnknown");
        }

        var number = numberField is { } field
            ? (await registries.ListValuesAsync(entry.Id, ct).ConfigureAwait(false))
                .FirstOrDefault(v => v.RegistryFieldDefId == field)?.ValueString
            : null;

        var warnings = new List<PermitBindingIssue>();
        if (!entry.IsActive || !entry.IsValidOn(request.PeriodEnd))
        {
            // ⚠ Знімок довідників такий запис не бачить: рядки й REGSUM над ним мовчки порожні.
            warnings.Add(new PermitBindingIssue(
                "binding.warning.permitNotValidAtPeriodEnd",
                new Dictionary<string, string>
                {
                    ["entryId"] = entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["periodEnd"] = request.PeriodEnd.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                },
                $"Дозвіл {number ?? entry.Code} нечинний на {request.PeriodEnd:yyyy-MM-dd} (неактивний або поза вікном дії): " +
                "рядки з нього не з'являться, а REGSUM дасть 0."));
        }

        return new PermitResolution(entry.Id, number, entry.ValidFrom, entry.ValidTo, null, warnings);
    }

    private static PermitResolution None(string reason) => new(null, null, null, null, reason, []);
}
