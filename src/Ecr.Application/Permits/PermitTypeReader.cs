// src/Ecr.Application/Permits/PermitTypeReader.cs
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits;

/// <summary>
/// Опублікований склад типу, прочитаний з реєстрів і надбудови полів (Permits, PM-03).
/// </summary>
/// <remarks>
/// ⚠ Істина про опублікований тип — самі реєстри, а не знімок: приховані реєстри можна змінити кодом, і
/// знімок збрехав би. Шапка не читається: її поля фіксовані (<see cref="PermitHeaderFields"/>).
/// </remarks>
internal sealed class PermitTypeReader(IRegistryStore registries, IPermitTypeStore permits)
{
    /// <summary>Читає опублікований склад типу.</summary>
    /// <param name="type">Тип.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<PermitTypeContent> ReadPublishedAsync(PermitTypeDef type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);

        var tables = await permits.ListTablesAsync(type.Id, ct).ConfigureAwait(false);
        var metas = (await permits.ListFieldMetasAsync(type.Id, ct).ConfigureAwait(false))
            .ToDictionary(m => m.RegistryFieldDefId);
        var codeById = (await registries.ListDefinitionsAsync(ct).ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => d.Code);

        var result = new List<PermitTypeTableDto>();
        foreach (var table in tables)
        {
            var definition = await registries.FindDefinitionByIdAsync(table.RegistryDefId, ct).ConfigureAwait(false);
            var columns = new List<PermitTypeColumnDto>();

            if (definition is not null)
            {
                foreach (var field in definition.Fields
                             .Where(f => f.RelationKind != RegistryRelationKind.Composition)
                             .OrderBy(f => f.Ordinal))
                {
                    metas.TryGetValue(field.Id, out var meta);
                    columns.Add(await ColumnAsync(field, meta, codeById, ct).ConfigureAwait(false));
                }
            }

            result.Add(new PermitTypeTableDto(
                table.Code,
                Copy(table.NameL10n.Values),
                table.Ordinal,
                columns,
                await RulesAsync(table.RegistryDefId, ct).ConfigureAwait(false)));
        }

        return new PermitTypeContent(
            Copy(type.NameL10n.Values),
            PermitTypeCodec.CategoryName(type.Category),
            result,
            await RulesAsync(type.HeaderRegistryDefId, ct).ConfigureAwait(false));
    }

    internal static Dictionary<string, string> Copy(IReadOnlyDictionary<string, string> values)
        => new(values, StringComparer.OrdinalIgnoreCase);

    private async Task<PermitTypeColumnDto> ColumnAsync(
        RegistryFieldDef field, PermitFieldMeta? meta, Dictionary<int, string> codeById, CancellationToken ct)
    {
        IReadOnlyList<string>? choices = null;
        string? listCode = null;

        if (meta?.ListRegistryDefId is { } listId)
        {
            codeById.TryGetValue(listId, out listCode);

            if (meta.ListScope == PermitListScope.TypeOnly)
            {
                choices =
                [
                    .. (await registries.ListEntriesAsync(listId, ct).ConfigureAwait(false))
                        .Where(e => e.IsActive && !e.IsDeleted)
                        .OrderBy(e => e.Ordinal).ThenBy(e => e.Id)
                        .Select(e => e.DisplayL10n.Get("en") ?? e.Code),
                ];
            }
        }

        return new PermitTypeColumnDto(
            field.Code,
            Copy(field.NameL10n.Values),
            field.DataType.ToString(),
            field.UnitId,
            field.IsRequired,
            field.IsKey,
            meta?.GroupCode,
            meta?.Precision,
            meta?.Scale,
            meta?.ListScope?.ToString(),
            listCode,
            meta?.ListSiblingTableCode,
            choices,
            field.Ordinal);
    }

    private async Task<IReadOnlyList<PermitTypeRuleDto>> RulesAsync(int registryDefId, CancellationToken ct)
        => [.. (await registries.ListRulesAsync(registryDefId, ct).ConfigureAwait(false))
            .Where(r => r.IsActive)
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .Select(r => new PermitTypeRuleDto(
                r.Code,
                Copy(r.MessageL10n.Values),
                r.Expression,
                r.Severity == ValidationSeverity.Error ? "Block" : "Warn"))];
}
