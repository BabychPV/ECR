// src/Ecr.Infrastructure/Persistence/MethodologyArgumentsBackfill.cs
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Крок даних «похідні аргументи» (2026-11-02): переносить маркери <c>Name?</c> зі старих, введених руками
/// <c>calc.MethodologyFormula.ArgumentsCsv</c> у вирази і робить список похідним від виразу.
/// </summary>
/// <remarks>
/// ⛔ Чому C#, а не SQL у міграції: розбір виразу — це лексер діалекту (рядкові літерали, дотові імена, межі
/// імен), і повторити його в T-SQL означало б мати ДРУГИЙ розбір, який розійдеться з першим. Крок виконує
/// застосунок на старті (після сіду), як і решту DML (<c>02-contracts.md</c> §14).
/// <para>
/// ⚠ Ідемпотентний і самообмежений: береться лише версія, у якій хоча б одна формула має список, що НЕ збігається
/// з похідним (старий формат). Після першого проходу таких немає — повторний старт нічого не пише й не
/// журналить. Формули, записані через API, похідні з моменту запису.
/// </para>
/// <para>
/// ⚠ Числа опублікованих версій не змінюються: рантайм (<c>OptionalArguments</c>) об'єднує маркери по версії, і
/// маркер ставиться на кожне вживання імені в кожній формулі версії (<see cref="MethodologyDerivedArguments"/>).
/// Звіт — у журнал старту (Warning), рядок на версію: скільки маркерів перенесено, які маркери зникли (ім'я не
/// вживає жодна формула версії), які невжиті елементи списку прибрано.
/// </para>
/// </remarks>
/// <param name="db">Контекст бази.</param>
/// <param name="logger">Журнал старту; <c>null</c> — звіт лише в поверненому значенні.</param>
public sealed partial class MethodologyArgumentsBackfill(EcrDbContext db, ILogger? logger = null)
{
    /// <summary>Підсумок проходу.</summary>
    /// <param name="Versions">Скільки версій вирівняно.</param>
    /// <param name="Formulas">Скільки формул переписано.</param>
    /// <param name="MarkersAdded">Скільки маркерів <c>?</c> поставлено у вирази.</param>
    /// <param name="DroppedOptional">Маркери, що зникли: «код/версія: ім'я».</param>
    /// <param name="DroppedDeclared">Невжиті елементи старих списків: «код/версія/формула: елемент».</param>
    /// <param name="Skipped">Версії, пропущені через задовгий вираз після маркерів.</param>
    public sealed record Report(
        int Versions,
        int Formulas,
        int MarkersAdded,
        IReadOnlyList<string> DroppedOptional,
        IReadOnlyList<string> DroppedDeclared,
        IReadOnlyList<string> Skipped);

    /// <summary>Стеля читання формул (Land — 566; уся база — тисячі).</summary>
    private const int MaxFormulas = 200_000;

    /// <summary>Вирівнювання однієї версії.</summary>
    private sealed record VersionPlan(string Label, MethodologyDerivedArguments.VersionResult Normalized)
    {
        public IEnumerable<MethodologyDerivedArguments.Output> Changed
            => Normalized.Formulas.Where(f => f.ExpressionChanged || f.ArgumentsChanged);
    }

    /// <summary>Виконує крок.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Підсумок.</returns>
    public async Task<Report> RunAsync(CancellationToken ct)
    {
        var rows = await db.MethodologyFormulas
            .AsNoTracking()
            .OrderBy(f => f.MethodologyVersionId)
            .ThenBy(f => f.Id)
            .Select(f => new { f.Id, f.MethodologyVersionId, f.Code, f.Expression, f.ArgumentsCsv })
            .Take(MaxFormulas)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var legacy = rows
            .GroupBy(r => r.MethodologyVersionId)
            .Where(g => g.Any(r => !MethodologyDerivedArguments.IsDerived(r.Expression, r.ArgumentsCsv)))
            .ToList();

        if (legacy.Count == 0)
        {
            return new Report(0, 0, 0, [], [], []);
        }

        var versionIds = legacy.Select(g => g.Key).ToList();
        var labels = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => versionIds.Contains(v.Id))
            .Join(db.Methodologies, v => v.MethodologyId, m => m.Id, (v, m) => new { v.Id, m.Code, v.Version })
            .ToDictionaryAsync(x => x.Id, x => $"{x.Code}/{x.Version}", ct)
            .ConfigureAwait(false);

        var plans = new List<VersionPlan>();
        var skipped = new List<string>();

        foreach (var group in legacy)
        {
            var label = labels.GetValueOrDefault(
                group.Key, group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var result = MethodologyDerivedArguments.Normalize(
                [.. group.Select(r => new MethodologyDerivedArguments.Input(r.Id, r.Code, r.Expression, r.ArgumentsCsv))]);

            // ⛔ Версія переписується ЦІЛКОМ або ніяк: формула без маркера, якого вимагає версія, змінила б
            // множину необов'язкових і, отже, числа.
            var tooLong = result.Formulas.FirstOrDefault(f => f.Expression.Length > MethodologyFormula.MaxExpressionLength);
            if (tooLong is not null)
            {
                skipped.Add($"{label}/{tooLong.Code}");
                if (logger is not null)
                {
                    LogSkipped(logger, label, tooLong.Code, MethodologyFormula.MaxExpressionLength);
                }

                continue;
            }

            plans.Add(new VersionPlan(label, result));
        }

        // ⚠ Стратегія повторів EF (`EnableRetryOnFailure`) не дозволяє власної транзакції поза нею: увесь запис —
        // одним замиканням; повтор переписує ті самі значення.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            foreach (var formula in plans.SelectMany(p => p.Changed))
            {
                var expression = formula.Expression;
                var csv = formula.ArgumentsCsv;
                await db.MethodologyFormulas
                    .Where(f => f.Id == formula.Id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(f => f.Expression, expression).SetProperty(f => f.ArgumentsCsv, csv),
                        ct)
                    .ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var droppedOptional = new List<string>();
        var droppedDeclared = new List<string>();

        foreach (var plan in plans)
        {
            var changed = plan.Changed.ToList();
            droppedOptional.AddRange(plan.Normalized.DroppedOptional.Select(name => $"{plan.Label}: {name}"));
            droppedDeclared.AddRange(changed.SelectMany(f => f.DroppedDeclared.Select(item => $"{plan.Label}/{f.Code}: {item}")));

            if (logger is not null)
            {
                LogVersion(
                    logger,
                    plan.Label,
                    changed.Count,
                    changed.Sum(f => f.MarkersAdded),
                    changed.Count(f => f.ExpressionChanged),
                    string.Join(", ", plan.Normalized.DroppedOptional),
                    string.Join("; ", changed
                        .Where(f => f.DroppedDeclared.Count > 0)
                        .Select(f => $"{f.Code}: {string.Join(", ", f.DroppedDeclared)}")));
            }
        }

        var report = new Report(
            plans.Count,
            plans.Sum(p => p.Changed.Count()),
            plans.Sum(p => p.Changed.Sum(f => f.MarkersAdded)),
            droppedOptional,
            droppedDeclared,
            skipped);

        if (logger is not null)
        {
            LogTotal(
                logger, report.Versions, report.Formulas, report.MarkersAdded,
                report.DroppedOptional.Count, report.DroppedDeclared.Count);
        }

        return report;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Похідні аргументи: {Version} — переписано формул {Formulas}, маркерів «?» перенесено у вирази {Markers} "
            + "(у {MarkedFormulas} формулах); маркер зник (ім'я не вживає жодна формула версії): [{DroppedOptional}]; "
            + "прибрано невжиті елементи списку: [{DroppedDeclared}].")]
    private static partial void LogVersion(
        ILogger logger, string version, int formulas, int markers, int markedFormulas, string droppedOptional, string droppedDeclared);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Похідні аргументи: {Version} ПРОПУЩЕНО — формула {Formula} з маркерами довша за {Max} символів; "
            + "список лишається старим, рантайм його читає як і раніше. Скоротіть вираз і перезапустіть.")]
    private static partial void LogSkipped(ILogger logger, string version, string formula, int max);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Похідні аргументи: вирівняно версій {Versions}, формул {Formulas}, маркерів перенесено {Markers}, "
            + "маркерів зникло {DroppedOptional}, невжитих елементів прибрано {DroppedDeclared}.")]
    private static partial void LogTotal(
        ILogger logger, int versions, int formulas, int markers, int droppedOptional, int droppedDeclared);
}
