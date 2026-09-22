// src/Ecr.Application/Calculations/CompareMethodologyVersionsHandler.cs
using System.Globalization;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Calculations;

/// <summary>
/// Різниця двох версій методології: формули, константи, тести (<c>BE-25</c>).
/// </summary>
/// <remarks>
/// Лише читання. Не плутати з <c>MethodologyPublicationDiff</c> — той порівнює
/// РЕЗУЛЬТАТИ розрахунку при публікації, цей — ВМІСТ версій до неї.
/// </remarks>
public sealed class CompareMethodologyVersionsHandler(
    IMethodologyDraftStore drafts,
    IMethodologyStore methodologies,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Порівнює версію з базовою.</summary>
    /// <param name="methodologyId">Методологія з адреси.</param>
    /// <param name="methodologyVersionId">Версія «стало».</param>
    /// <param name="baseVersionId">Версія «було».</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Відмінності.</returns>
    /// <exception cref="NotFoundException"><c>ECR-CALC-0404</c> — хоч одна з версій чужа або відсутня.</exception>
    public async Task<MethodologyVersionDiffDto> HandleAsync(
        int methodologyId, int methodologyVersionId, int baseVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        await MethodologyVersionGuard
            .RequireOwnAsync(drafts, methodologyId, [methodologyVersionId, baseVersionId], ct)
            .ConfigureAwait(false);

        var before = await ReadAsync(baseVersionId, ct).ConfigureAwait(false);
        var after = await ReadAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return new MethodologyVersionDiffDto(baseVersionId, methodologyVersionId, Compare(before, after));
    }

    /// <summary>Порівнює вміст двох версій.</summary>
    /// <param name="before">Базова версія.</param>
    /// <param name="after">Нова версія.</param>
    /// <returns>Відмінності: формули, далі константи, далі тести; всередині — за кодом.</returns>
    /// <remarks>
    /// Коди порівнюються без регістру — так їх зіставляє СУБД (<c>EcrCode</c>). Варіант
    /// константи — це код + категорія + речовина + дата початку (<c>ФВ-16.5</c>):
    /// два значення з різними датами — два варіанти, а не «змінене» одне.
    /// </remarks>
    public static IReadOnlyList<MethodologyDiffItemDto> Compare(VersionContent before, VersionContent after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var items = new List<MethodologyDiffItemDto>();

        Diff(items, MethodologyDiffItemKind.Formula, before.Formulas, after.Formulas,
            f => f.Code.ToUpperInvariant(), f => (f.Code, null, null, null), f => f.Expression,
            (a, b) => Changed(
                (MethodologyDiffFields.Expression, a.Expression != b.Expression),
                (MethodologyDiffFields.ResultType, a.ResultType != b.ResultType),
                (MethodologyDiffFields.OutputUnitId, a.OutputUnitId != b.OutputUnitId),
                (MethodologyDiffFields.ArgumentsCsv, a.ArgumentsCsv != b.ArgumentsCsv)));

        Diff(items, MethodologyDiffItemKind.Constant, before.Constants, after.Constants,
            c => string.Create(
                CultureInfo.InvariantCulture,
                $"{c.Code.ToUpperInvariant()}|{c.Category}|{c.SubstanceEntryId}|{c.ValidFrom:yyyy-MM-dd}"),
            c => (c.Code, c.Category, c.SubstanceEntryId, c.ValidFrom),
            c => c.Value?.ToString(CultureInfo.InvariantCulture) ?? c.TextValue,
            (a, b) => Changed(
                (MethodologyDiffFields.Kind, a.Kind != b.Kind),
                (MethodologyDiffFields.Value, a.Value != b.Value),
                (MethodologyDiffFields.TextValue, a.TextValue != b.TextValue),
                (MethodologyDiffFields.UnitId, a.UnitId != b.UnitId),
                (MethodologyDiffFields.ValidTo, a.ValidTo != b.ValidTo),
                (MethodologyDiffFields.Source, a.Source != b.Source)));

        Diff(items, MethodologyDiffItemKind.TestCase, before.TestCases, after.TestCases,
            t => t.Code.ToUpperInvariant(), t => (t.Code, null, null, null), t => t.ExpectedJson,
            (a, b) => Changed(
                (MethodologyDiffFields.InputJson, a.InputJson != b.InputJson),
                (MethodologyDiffFields.ExpectedJson, a.ExpectedJson != b.ExpectedJson),
                (MethodologyDiffFields.Tolerance, a.Tolerance != b.Tolerance)));

        return items;
    }

    private async Task<VersionContent> ReadAsync(int versionId, CancellationToken ct)
        => new(
            await methodologies.GetFormulasAsync(versionId, ct).ConfigureAwait(false),
            await methodologies.GetConstantsAsync(versionId, ct).ConfigureAwait(false),
            await drafts.GetTestCaseEntitiesAsync(versionId, ct).ConfigureAwait(false));

    private static void Diff<T>(
        List<MethodologyDiffItemDto> items,
        MethodologyDiffItemKind kind,
        IReadOnlyList<T> before,
        IReadOnlyList<T> after,
        Func<T, string> key,
        Func<T, (string Code, string? Category, long? Substance, DateOnly? ValidFrom)> identity,
        Func<T, string?> shown,
        Func<T, T, IReadOnlyList<string>> changed)
    {
        // ToLookup, а не ToDictionary: зіпсовані дані з дублем ключа не мають валити читання.
        var was = before.ToLookup(key, StringComparer.Ordinal);
        var now = after.ToLookup(key, StringComparer.Ordinal);

        foreach (var k in was.Select(g => g.Key).Union(now.Select(g => g.Key)).Order(StringComparer.Ordinal))
        {
            var a = was[k].FirstOrDefault();
            var b = now[k].FirstOrDefault();
            var id = identity(b ?? a!);

            if (a is null || b is null)
            {
                items.Add(new MethodologyDiffItemDto(
                    kind, id.Code, id.Category, id.Substance, id.ValidFrom,
                    a is null ? MethodologyDiffChange.Added : MethodologyDiffChange.Removed,
                    [], a is null ? null : shown(a), b is null ? null : shown(b)));
                continue;
            }

            var fields = changed(a, b);
            if (fields.Count > 0)
            {
                items.Add(new MethodologyDiffItemDto(
                    kind, id.Code, id.Category, id.Substance, id.ValidFrom,
                    MethodologyDiffChange.Changed, fields, shown(a), shown(b)));
            }
        }
    }

    private static string[] Changed(params (string Field, bool Differs)[] fields)
        => [.. fields.Where(f => f.Differs).Select(f => f.Field)];
}

/// <summary>Вміст версії, який порівнює <see cref="CompareMethodologyVersionsHandler"/>.</summary>
/// <param name="Formulas">Формули.</param>
/// <param name="Constants">Константи з усіма варіантами.</param>
/// <param name="TestCases">Тести золотого набору.</param>
public sealed record VersionContent(
    IReadOnlyList<MethodologyFormula> Formulas,
    IReadOnlyList<MethodologyConstant> Constants,
    IReadOnlyList<MethodologyTestCaseEntity> TestCases);
