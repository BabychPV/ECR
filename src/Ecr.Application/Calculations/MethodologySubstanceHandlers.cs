// src/Ecr.Application/Calculations/MethodologySubstanceHandlers.cs
using System.Globalization;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Calculations;

/// <summary>
/// Склад речовин версії методології (<c>calc.MethodologySubstance</c>, ФВ-8.8): читання.
/// </summary>
/// <remarks>
/// ⚠ Порожній склад — законний стан (версія без речовин; форма A константи), тому 200 з порожнім
/// переліком, а 404 лишається для версії, якої немає.
/// </remarks>
public sealed class ListMethodologySubstancesHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає склад речовин версії.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Речовини в порядку <c>ordinal</c>.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<MethodologySubstancesDto> HandleAsync(int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        _ = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw SaveMethodologySubstancesHandler.VersionNotFound(methodologyVersionId);

        var rows = await drafts.ListSubstanceRowsAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return SaveMethodologySubstancesHandler.Map(rows);
    }
}

/// <summary>
/// Замінює склад речовин версії-**чернетки** переданим переліком (набір, а не дельта).
/// </summary>
/// <remarks>
/// ⛔ До цього речовини потрапляли у версію рівно одним шляхом — клонуванням попередньої, тож
/// методологія, створена з нуля (або імпортована пакетом), не мала речовин, і ключ форми B
/// (<c>{ключ}_{код речовини}</c>) був недосяжний.
///
/// ⚠ <c>PUT</c> набору, а не <c>POST</c> одного елемента: набір ідемпотентний (повторний запит із
/// тим самим тілом нічого не змінює), порядок у переліку — це <c>Ordinal</c>, і «прибрати речовину»
/// не потребує окремої дії. Порожній перелік очищує склад.
///
/// ⛔ Прибрати речовину, на яку звужена константа цієї ж версії, не можна (<c>ECR-CALC-0409</c>,
/// <c>substanceInUse</c>): константа звужена до речовини, якої версія більше не рахує, — мертва
/// конфігурація, що виглядає чинною.
///
/// ⚠ Право те саме, що на склад виходів і формул (<c>Calculation.EditFormula</c>): склад речовин
/// — частина структури методики, не коефіцієнт.
/// </remarks>
public sealed class SaveMethodologySubstancesHandler(
    IMethodologyDraftStore drafts,
    IMethodologyStore methodologies,
    IRegistryStore registries,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditFormula";

    /// <summary>Стеля складу: більше — це помилка даних, а не методика.</summary>
    public const int MaxSubstances = 1000;

    /// <summary>Записує склад речовин версії.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="substanceEntryIds">Записи довідника речовин у порядку переліку.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Склад після запису.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0422</c> — перелік не подано, задовгий, містить дубль або запис, якого немає;
    /// <c>ECR-CALC-0409</c> — версія опублікована або речовину використовує константа.
    /// </exception>
    public async Task<MethodologySubstancesDto> HandleAsync(
        int methodologyVersionId, IReadOnlyList<long>? substanceEntryIds, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw VersionNotFound(methodologyVersionId);

        var wanted = Validate(substanceEntryIds);

        await RequireExistingAsync(wanted, ct).ConfigureAwait(false);

        var existing = await drafts.GetSubstancesTrackedAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var wantedSet = wanted.ToHashSet();
        var removed = existing.Where(s => !wantedSet.Contains(s.SubstanceEntryId)).ToList();

        await RequireNotUsedAsync(version, removed, ct).ConfigureAwait(false);

        var byEntry = existing.ToDictionary(s => s.SubstanceEntryId);

        foreach (var substance in removed)
        {
            version.RemoveSubstance(substance);
            drafts.Remove(substance);
        }

        for (var ordinal = 0; ordinal < wanted.Count; ordinal++)
        {
            var entryId = wanted[ordinal];

            if (byEntry.TryGetValue(entryId, out var kept))
            {
                if (kept.Ordinal != ordinal)
                {
                    version.SetSubstanceOrdinal(kept, ordinal);
                }
            }
            else
            {
                drafts.Add(version.AddSubstance(entryId, ordinal));
            }
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        var rows = await drafts.ListSubstanceRowsAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return Map(rows);
    }

    /// <summary>Склад як DTO.</summary>
    /// <param name="rows">Рядки складу.</param>
    /// <returns>DTO для конфігуратора.</returns>
    internal static MethodologySubstancesDto Map(IReadOnlyList<MethodologySubstanceRow> rows)
        => new([.. rows.Select(r => new MethodologySubstanceDto(
            r.SubstanceEntryId, r.Code, r.RegistryDefId, r.Ordinal, r.IsDeleted))]);

    /// <summary>Відмова «версії немає».</summary>
    /// <param name="methodologyVersionId">Версія з маршруту.</param>
    /// <returns>Виняток 404 <c>ECR-CALC-0404</c>.</returns>
    internal static NotFoundException VersionNotFound(int methodologyVersionId)
        => SaveMethodologyCategoryRuleHandler.VersionNotFound(methodologyVersionId);

    private static List<long> Validate(IReadOnlyList<long>? ids)
    {
        if (ids is null)
        {
            throw Rule(
                "Перелік речовин не подано: порожній склад записується порожнім переліком, а не відсутнім.",
                "err.ECR-CALC-0422.substancesRequired",
                "ECR-CALC-0422");
        }

        if (ids.Count > MaxSubstances)
        {
            throw Rule(
                $"У версії не може бути більше {MaxSubstances} речовин.",
                "err.ECR-CALC-0422.substancesTooMany",
                "ECR-CALC-0422",
                ("max", MaxSubstances.ToString(CultureInfo.InvariantCulture)),
                ("count", ids.Count.ToString(CultureInfo.InvariantCulture)));
        }

        var seen = new HashSet<long>();
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw Rule(
                    $"Речовина {id} вказана двічі.",
                    "err.ECR-CALC-0422.substanceDuplicate",
                    "ECR-CALC-0422",
                    ("substanceEntryId", id.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return [.. ids];
    }

    private async Task RequireExistingAsync(List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var standings = await registries.FindEntryStandingsAsync(ids, ct).ConfigureAwait(false);
        var alive = standings.Where(s => !s.IsDeleted).Select(s => s.Id).ToHashSet();

        foreach (var id in ids)
        {
            if (!alive.Contains(id))
            {
                throw Rule(
                    $"Речовини (запису довідника) {id} не існує.",
                    "err.ECR-CALC-0422.substanceNotFound",
                    "ECR-CALC-0422",
                    ("substanceEntryId", id.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private async Task RequireNotUsedAsync(
        MethodologyVersion version, List<MethodologySubstance> removed, CancellationToken ct)
    {
        if (removed.Count == 0)
        {
            return;
        }

        var removedIds = removed.Select(s => s.SubstanceEntryId).ToHashSet();
        var constants = await methodologies.GetConstantsAsync(version.Id, ct).ConfigureAwait(false);

        var blocking = constants
            .Where(c => c.SubstanceEntryId is { } entry && removedIds.Contains(entry))
            .Select(c => c.Code)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (blocking.Count == 0)
        {
            return;
        }

        throw Rule(
            $"Речовину не можна прибрати зі складу: на неї звужені константи {string.Join(", ", blocking.Take(5))}.",
            "err.ECR-CALC-0409.substanceInUse",
            "ECR-CALC-0409",
            ("constantCodes", string.Join(", ", blocking.Take(5))),
            ("count", blocking.Count.ToString(CultureInfo.InvariantCulture)));
    }

    private static BusinessRuleException Rule(
        string message, string messageKey, string code, params (string Name, string Value)[] args)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal) { ["messageKey"] = messageKey };
        foreach (var (name, value) in args)
        {
            details[name] = value;
        }

        return new BusinessRuleException(code, message, details);
    }
}
