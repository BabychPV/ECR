// src/Ecr.Application/Permits/Bindings/PermitPlanBuilder.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Permits.Bindings;

/// <summary>План прив'язки для документа: який дозвіл обрано й які рядки він породжує.</summary>
/// <param name="Resolution">Результат вибору дозволу.</param>
/// <param name="Rows">Рядки аркуша з дозволу (порожні, якщо дозволу немає, режим - формули чи таблиця недоступна).</param>
/// <param name="Warnings">Попередження вибору й побудови (без дублів).</param>
public sealed record PermitBindingPlan(
    PermitResolution Resolution, PermitRowPlan Rows, IReadOnlyList<PermitBindingIssue> Warnings);

/// <summary>
/// Спільна для перегляду (PM-09) і рантайму (PM-10) побудова плану: вибір дозволу + рядки планувальником над знімком.
/// </summary>
/// <remarks>
/// ⚠ ЄДИНИЙ шлях: якби перегляд і створення рядків документа будували план кожен по-своєму, перегляд показував би не те,
/// що створить документ. Кеш знімків (за набором довідників і датою) передає викликач, що будує план для кількох
/// прив'язок за один виклик, - знімок будується раз.
/// </remarks>
public static class PermitPlanBuilder
{
    /// <summary>Будує план.</summary>
    /// <param name="view">Прив'язка.</param>
    /// <param name="type">Тип дозволу прив'язки.</param>
    /// <param name="table">Таблиця типу прив'язки.</param>
    /// <param name="source">Звідки дані: кінець періоду, об'єкт, дозвіл із шапки.</param>
    /// <param name="resolver">Вибір дозволу.</param>
    /// <param name="snapshots">Завантажувач знімків довідників.</param>
    /// <param name="cache">Кеш знімків викликача; <c>null</c> - без кешу.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<PermitBindingPlan> BuildAsync(
        PermitBindingView view,
        PermitTypeDef type,
        PermitTypeTable table,
        PermitPlanSource source,
        IPermitResolver resolver,
        IRegistrySnapshotLoader snapshots,
        IDictionary<string, IRegistrySnapshot>? cache,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(snapshots);

        var binding = view.Binding;
        var warnings = new List<PermitBindingIssue>();

        var resolution = await resolver.ResolveAsync(
            new PermitResolveRequest(
                type.HeaderRegistryDefId, binding.SelectionRule, source.PeriodEnd, source.FacilityNames, source.HeaderEntryId),
            ct).ConfigureAwait(false);
        warnings.AddRange(resolution.Warnings);

        var plan = new PermitRowPlan([], []);
        if (binding.Mode == PermitBindingMode.Rows && resolution.EntryId is { } permitId)
        {
            var ids = new[] { type.HeaderRegistryDefId, table.RegistryDefId }.Order().ToArray();
            var key = string.Join(',', ids) + "|" + source.PeriodEnd.ToString("O", CultureInfo.InvariantCulture);

            IRegistrySnapshot snapshot;
            if (cache is not null && cache.TryGetValue(key, out var cached))
            {
                snapshot = cached;
            }
            else
            {
                snapshot = await snapshots.LoadAsync(ids, source.PeriodEnd, null, ct).ConfigureAwait(false);
                if (cache is not null)
                {
                    cache[key] = snapshot;
                }
            }

            plan = PermitRowPlanner.Plan(
                snapshot,
                PermitHeaderFields.TableRegistryCode(type.Code, table.Code),
                permitId,
                [.. view.Columns.Select(c => new PlannerColumn(ColumnCode(view, c.SheetColumnDefId), c.PermitFieldCode, c.Role))]);
            warnings.AddRange(plan.Warnings);
        }

        return new PermitBindingPlan(resolution, plan, warnings);
    }

    private static string ColumnCode(PermitBindingView view, int columnId)
        => view.SheetColumnCodes.GetValueOrDefault(columnId, columnId.ToString(CultureInfo.InvariantCulture));
}

/// <summary>Звідки береться дозвіл для плану.</summary>
/// <param name="PeriodEnd">Останній день періоду документа.</param>
/// <param name="FacilityNames">Назви об'єкта документа (для <c>ByFacilityPeriodEnd</c>).</param>
/// <param name="HeaderEntryId">Запис шапки дозволу з поля шапки (для <c>FromHeaderLookup</c>).</param>
public sealed record PermitPlanSource(DateOnly PeriodEnd, IReadOnlyList<string> FacilityNames, long? HeaderEntryId);

/// <summary>Читання об'єкта й дозволу з шапки документа - спільне для перегляду й рантайму.</summary>
public static class PermitDocumentSource
{
    /// <summary>Читає з шапки документа об'єкт або дозвіл, залежно від правила вибору.</summary>
    /// <param name="headers">Сховище шапок.</param>
    /// <param name="registries">Сховище довідників (запис довідника-об'єкта).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="rule">Правило вибору прив'язки.</param>
    /// <param name="headerFieldCode">Код поля шапки прив'язки (для <c>FromHeaderLookup</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<(IReadOnlyList<string> FacilityNames, long? HeaderEntryId)> ReadAsync(
        IDocumentHeaderStore headers,
        IRegistryStore registries,
        long documentId,
        PermitSelectionRule rule,
        string? headerFieldCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(registries);

        var header = await headers.GetExpressionValuesAsync(documentId, ct).ConfigureAwait(false);
        if (rule == PermitSelectionRule.FromHeaderLookup)
        {
            return ([], LookupId(header, headerFieldCode));
        }

        return (await FacilityNamesAsync(registries, header, ct).ConfigureAwait(false), null);
    }

    private static long? LookupId(IReadOnlyDictionary<string, ExpressionValue> header, string? fieldCode)
    {
        if (fieldCode is null)
        {
            return null;
        }

        var value = header.FirstOrDefault(p => string.Equals(p.Key, fieldCode, StringComparison.OrdinalIgnoreCase)).Value;
        return value.AsNumber() is { } n && n == decimal.Truncate(n) ? (long)n : null;
    }

    private static async Task<IReadOnlyList<string>> FacilityNamesAsync(
        IRegistryStore registries, IReadOnlyDictionary<string, ExpressionValue> header, CancellationToken ct)
    {
        var value = header.FirstOrDefault(
            p => string.Equals(p.Key, PermitBindingFacility.HeaderFieldCode, StringComparison.OrdinalIgnoreCase)).Value;

        if (value.Type == ExpressionValueType.Text)
        {
            return [(string)value.Value!];
        }

        // Lookup: у документі лежить id запису довідника - порівнюється його код і назви.
        if (LookupId(header, PermitBindingFacility.HeaderFieldCode) is { } entryId
            && await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false) is { } entry)
        {
            return [entry.Code, .. entry.DisplayL10n.Values.Values];
        }

        return [];
    }
}
