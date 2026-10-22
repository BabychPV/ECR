// src/Ecr.Application/Permits/Groups/LookupTargetEntries.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Permits.Groups;

/// <summary>Запис цілі Lookup разом із типом дозволу (лише для цілі-групи).</summary>
/// <param name="Entry">Запис довідника.</param>
/// <param name="TypeCode">Код типу дозволу (реєстр шапки <c>PRM_&lt;TYPE&gt;</c> без префікса); <c>null</c> - ціль не група.</param>
public sealed record LookupTargetEntry(RegistryEntry Entry, string? TypeCode);

/// <summary>
/// ЄДИНЕ читання записів цілі Lookup за <c>registryId</c> для імпорту/експорту/слайсів/синхронізації (Permits, PRMG):
/// звичайна ціль - її записи; ціль-ГРУПА (<c>PRMG_*</c> без власних записів) - обʼєднання записів реєстрів-членів.
/// </summary>
/// <remarks>
/// <para>
/// Номер дозволу унікальний лише в межах типу, тому в групі код може повторюватись у кількох типах: підпис для експорту -
/// <see cref="Label"/> («номер — тип»); імпорт за голим номером, що збігається в кількох типах, ВІДМОВЛЯЄ з переліком
/// кандидатів, без пріоритету типу (рішення координатора, 02-contracts).
/// </para>
/// <para>
/// Розгортання - лише коли <c>IsGroup(target)</c>; для не-групи рівно один <c>ListEntriesAsync(target)</c>, як і раніше
/// (Land без змін).
/// </para>
/// </remarks>
public static class LookupTargetEntries
{
    /// <summary>Роздільник підпису «номер — тип».</summary>
    public const string LabelSeparator = " — ";

    /// <summary>Підпис запису: для групи «номер — тип», інакше голий код.</summary>
    /// <param name="code">Код (номер) запису.</param>
    /// <param name="typeCode">Код типу; <c>null</c> - не група.</param>
    public static string Label(string code, string? typeCode)
        => typeCode is null ? code : code + LabelSeparator + typeCode;

    /// <summary>Записи цілі (для групи - склад).</summary>
    /// <param name="registries">Сховище довідників.</param>
    /// <param name="groups">Резолвер груп; <c>null</c> - груп немає.</param>
    /// <param name="targetRegistryId">Ціль колонки/поля.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<LookupTargetEntry>> ListAsync(
        IRegistryStore registries, IPermitGroupResolver? groups, int targetRegistryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registries);

        if (groups is not null)
        {
            await groups.EnsureFreshAsync(ct).ConfigureAwait(false);
        }

        if (groups is null || !groups.IsGroup(targetRegistryId))
        {
            var own = await registries.ListEntriesAsync(targetRegistryId, ct).ConfigureAwait(false);
            return [.. own.Select(e => new LookupTargetEntry(e, null))];
        }

        var result = new List<LookupTargetEntry>();
        foreach (var memberId in groups.ExpandRegistryIds(targetRegistryId))
        {
            var definition = await registries.FindDefinitionByIdAsync(memberId, ct).ConfigureAwait(false);
            var typeCode = definition is null
                ? memberId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : definition.Code.StartsWith("PRM_", StringComparison.Ordinal) ? definition.Code[4..] : definition.Code;
            var entries = await registries.ListEntriesAsync(memberId, ct).ConfigureAwait(false);
            result.AddRange(entries.Select(e => new LookupTargetEntry(e, typeCode)));
        }

        return result;
    }

    /// <summary>
    /// Розбір коду для імпорту: підпис «номер — тип» і унікальний голий номер дають Id; номер, що збігається в кількох типах,
    /// - у <paramref name="ambiguous"/> (кандидати «номер — тип; …»).
    /// </summary>
    /// <param name="entries">Записи цілі.</param>
    /// <param name="codes">Код/підпис → Id (регістр не враховується).</param>
    /// <param name="ambiguous">Голий номер → кандидати; порожній, коли неоднозначностей немає.</param>
    public static void BuildImportCodes(
        IReadOnlyList<LookupTargetEntry> entries,
        out Dictionary<string, long> codes,
        out Dictionary<string, string> ambiguous)
    {
        ArgumentNullException.ThrowIfNull(entries);

        codes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        ambiguous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var byCode in entries.GroupBy(e => e.Entry.Code, StringComparer.OrdinalIgnoreCase))
        {
            var distinct = byCode.GroupBy(e => e.Entry.Id).Select(g => g.First()).ToList();
            if (distinct.Count == 1)
            {
                codes[byCode.Key] = distinct[0].Entry.Id;
            }
            else
            {
                ambiguous[byCode.Key] = string.Join("; ", distinct.Select(e => Label(e.Entry.Code, e.TypeCode)).Order(StringComparer.Ordinal));
            }

            foreach (var e in distinct.Where(e => e.TypeCode is not null))
            {
                codes.TryAdd(Label(e.Entry.Code, e.TypeCode), e.Entry.Id);
            }
        }
    }
}
