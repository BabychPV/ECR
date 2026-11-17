using System.Globalization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>
/// ✎ LAND-YEAR L3: немісячна колонка вихідної версії (копія на місяць) → місячна колонка цільової (рік, місяці в
/// колонках) для місяця <see cref="MonthNumber"/>: значення періоду з місяцем <c>m</c> переходить у колонку місяця <c>m</c>.
/// </summary>
/// <param name="SourceColumnDefId">Немісячна колонка вихідної версії.</param>
/// <param name="MonthNumber">Місяць 1..12 (= <c>PeriodKey % 100</c> місячного проєкту).</param>
/// <param name="TargetColumnDefId">Місячна колонка цільової версії (<c>MonthNumber</c> = <paramref name="MonthNumber"/>).</param>
/// <param name="TargetTableDefId">Її таблиця.</param>
public sealed record VersionMigrationMonthColumn(int SourceColumnDefId, int MonthNumber, int TargetColumnDefId, int TargetTableDefId);

/// <summary>План переносу — частина LAND-YEAR L3 (місячні колонки).</summary>
public sealed partial record VersionMigrationPlan
{
    /// <summary>
    /// ✎ LAND-YEAR L3: мапа «немісячна колонка × місяць → місячна колонка». Порожньо — перенос не міняє розкладку
    /// (усі інші переноси): сховище виконує лише мапу 1:1, відбиток плану незмінний.
    /// </summary>
    /// <remarks>
    /// ⛔ Значення періоду <c>P</c> у колонці <c>C</c> лежить в екземплярі періоду <c>P</c>, а власник колонки <c>C_Mmm</c>
    /// річної версії — екземпляр періоду <c>(рік, m)</c> (<c>IMonthColumnRouter.OwnerPeriodAsync</c>). Для
    /// <c>m = P % 100</c> це той самий екземпляр, тож перенос — зміна колонки на місці, без переїзду між екземплярами.
    /// </remarks>
    public IReadOnlyList<VersionMigrationMonthColumn> MonthColumns { get; init; } = [];

    /// <summary>✎ LAND-YEAR L3: скільки значень із <see cref="TransferredValues"/> переїде в місячні колонки.</summary>
    public long MonthTransferredValues { get; init; }

    /// <summary>
    /// ✎ LAND-YEAR L3: скільки значень обчислюваних колонок, яких цільова версія не має (селектор «поточного місяця»,
    /// чиє ім'я лишилось псевдонімом аргументу методологій), не переїде, а буде перераховано. Не втрата: значення —
    /// похідні, їх відтворює перерахунок після переносу.
    /// </summary>
    public long RecomputedValues { get; init; }

    /// <summary>
    /// ✎ LAND-YEAR L3: шляхи (<c>SHEET.TABLE.COLUMN</c>) немісячних колонок зі значеннями, для яких цільова річна версія
    /// не має повного набору місячних колонок (<c>C_M01..C_M12</c> або псевдонімів) — причина відмови <c>dataLoss</c>.
    /// </summary>
    public IReadOnlyList<string> MonthColumnsUnmapped { get; init; } = [];

    /// <summary>
    /// ✎ LAND-YEAR L3 (B-static): немісячні ВВЕДЕНІ колонки цільової річної версії (не <c>IsMonthColumn</c>, не
    /// обчислювані), власник яких — якір року (<c>IMonthColumnRouter</c>: найменший ключ календаря проєкту в році).
    /// Непорожньо лише для переходу «копія на місяць» → «рік»: сховище в транзакції перемикання версії дописує в якір
    /// значення, яких там немає, з НАЙПІЗНІШОГО місяця року (див. <see cref="NonMonthCopiedToAnchor"/>). Порожньо — крок
    /// не виконується (усі інші переноси).
    /// </summary>
    public IReadOnlyList<int> AnchorColumns { get; init; } = [];

    /// <summary>
    /// ✎ LAND-YEAR L3 (B-static): скільки комірок немісячних колонок дістане якір року з найпізнішого місяця (якір порожній,
    /// місяці згодні). Сховище при застосуванні звіряє фактичну кількість із цією (розбіжність — відкат T2, ECR-CELL-0409).
    /// </summary>
    public long NonMonthCopiedToAnchor { get; init; }

    /// <summary>
    /// ✎ LAND-YEAR L3 (B-static): скільки рядків створить (або відновить знятими) крок якоря в якорі року — рядки
    /// пізніших місяців, яких якорю бракує для копії (Land doc 96: T8_00 у 202601 не має ні рядків, ні екземпляра).
    /// </summary>
    public long NonMonthAnchorRowsCreated { get; init; }

    /// <summary>✎ LAND-YEAR L3 (B-static): скільки екземплярів таблиць створить крок якоря в якорі року.</summary>
    public long NonMonthAnchorInstancesCreated { get; init; }
}

/// <summary>
/// ✎ LAND-YEAR L3 (B-static): що зробить (зробив би) крок «немісячне значення → якір року» переходу на рік.
/// </summary>
/// <param name="Copies">Скільки комірок якір дістане з найпізнішого місяця (порожній якір, місяці згодні).</param>
/// <param name="ConflictCells">Скільки комірок мають у місяцях РІЗНІ значення при порожньому якорі (причина <c>nonMonthConflict</c>).</param>
/// <param name="Conflicts">Ці комірки зі значеннями по місяцях — не більше стелі запиту.</param>
/// <param name="RowsCreated">Скільки рядків крок створить (відновить) у якорі.</param>
/// <param name="InstancesCreated">Скільки екземплярів таблиць крок створить у якорі.</param>
/// <param name="Targets">Куди крок пише: документ × таблиця цільової версії × період якоря (перевірка поданих аркушів).</param>
public sealed record VersionMigrationAnchorFill(
    long Copies, long ConflictCells, IReadOnlyList<VersionMigrationAnchorConflict> Conflicts,
    long RowsCreated = 0, long InstancesCreated = 0, IReadOnlyList<VersionMigrationAnchorTarget>? Targets = null)
{
    /// <summary>Кроку немає (не перехід на рік).</summary>
    public static VersionMigrationAnchorFill None { get; } = new(0, 0, []);
}

/// <summary>Пара «документ × таблиця цільової версії × період якоря», куди пише крок якоря.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableDefId">Таблиця цільової версії.</param>
/// <param name="PeriodKey">Період якоря.</param>
public sealed record VersionMigrationAnchorTarget(long DocumentId, int TableDefId, int PeriodKey);

/// <summary>Комірка немісячної колонки, у якої місяці року не згодні, а якір порожній (ідентифікатори ЦІЛЬОВОЇ версії).</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableDefId">Таблиця цільової версії.</param>
/// <param name="RowKey">Бізнес-ключ рядка (ним рядки зіставляються між екземплярами періодів).</param>
/// <param name="ColumnDefId">Колонка цільової версії.</param>
/// <param name="Values">Значення по місяцях (період, текст значення) за зростанням періоду.</param>
public sealed record VersionMigrationAnchorConflict(
    long DocumentId, int TableDefId, string RowKey, int ColumnDefId, IReadOnlyList<NonMonthConflictValue> Values);

/// <summary>Планувальник — частина LAND-YEAR L3 (місячні колонки).</summary>
public static partial class VersionMigrationPlanner
{
    /// <summary>Вид пункту звіту: немісячна колонка переїжджає в місячні колонки цільової версії.</summary>
    public const string MonthColumnsKind = "MonthColumns";

    /// <summary>Вид пункту звіту: обчислювана колонка зникає, її значення відтворить перерахунок.</summary>
    public const string RecomputedKind = "Recomputed";

    /// <summary>Накопичувач місячних відповідностей під час розбору таблиць.</summary>
    private sealed class MonthOutcome(TemplateVersionSnapshot target)
    {
        public TemplateVersionSnapshot Target { get; } = target;

        public List<VersionMigrationMonthColumn> Map { get; } = [];

        public HashSet<int> Sources { get; } = [];

        public HashSet<int> Recomputed { get; } = [];

        public List<string> Unmapped { get; } = [];

        public HashSet<int> ClaimedTargets { get; } = [];
    }

    /// <summary>
    /// Колонка, якої немає в цільовій таблиці за кодом: місячні цілі (код <c>C_Mmm</c> або псевдонім <c>C</c> місяця
    /// <c>m</c>) — 12 відповідностей і пункт <see cref="MonthColumnsKind"/>; обчислювана колонка, ім'я якої лишилось лише
    /// псевдонімом на НАЯВНІ колонки (селектор), — пункт <see cref="RecomputedKind"/>. Інакше <c>false</c> (звичайне видалення).
    /// </summary>
    /// <remarks>
    /// ⚠ Ціль мусить бути НОВОЮ колонкою (її коду немає у вихідній таблиці): інакше значення лягли б поверх уже
    /// перенесених 1:1. Одна ціль — одне джерело; неповний набір (менше 12 місяців) — не мапа (значення не мали б
    /// місця в частині місяців).
    /// </remarks>
    private static bool TryMapMonthColumns(
        MonthOutcome? month, List<VersionMigrationItem> items, string path, TableDef to,
        ColumnDef column, long values, HashSet<string> sourceColumnCodes, HashSet<int> renamedTargets)
    {
        if (month is null || column.IsMonthColumn || to.LayoutKind != TableLayoutKind.MonthsInColumns)
        {
            return false;
        }

        var targetColumns = Live(to.Columns).ToList();
        var byCode = targetColumns.ToDictionary(c => c.Code, StringComparer.Ordinal);
        var byId = targetColumns.ToDictionary(c => c.Id);

        bool Usable(ColumnDef? candidate, int m)
            => candidate is not null
               && candidate.IsMonthColumn
               && candidate.MonthNumber == m
               && !sourceColumnCodes.Contains(candidate.Code)
               && !renamedTargets.Contains(candidate.Id)
               && !month.ClaimedTargets.Contains(candidate.Id);

        var found = new List<ColumnDef>(12);
        var aliasNamesColumn = false;
        for (var m = 1; m <= 12; m++)
        {
            var code = string.Create(CultureInfo.InvariantCulture, $"{column.Code}_M{m:00}");
            if (byCode.TryGetValue(code, out var byCodeTarget) && Usable(byCodeTarget, m))
            {
                found.Add(byCodeTarget);
                continue;
            }

            ColumnDef? aliasTarget = null;
            foreach (var alias in month.Target.MonthInputAliasesFor(to.Id, m))
            {
                if (!string.Equals(alias.Alias, column.Code, StringComparison.Ordinal))
                {
                    continue;
                }

                aliasNamesColumn = true;
                if (byId.TryGetValue(alias.ColumnDefId, out var candidate) && Usable(candidate, m))
                {
                    aliasTarget = candidate;
                    break;
                }
            }

            if (aliasTarget is null)
            {
                break;
            }

            found.Add(aliasTarget);
        }

        if (found.Count == 12)
        {
            for (var i = 0; i < 12; i++)
            {
                month.Map.Add(new VersionMigrationMonthColumn(column.Id, i + 1, found[i].Id, to.Id));
                month.ClaimedTargets.Add(found[i].Id);
            }

            month.Sources.Add(column.Id);
            items.Add(new(path, MonthColumnsKind, ChangeClass.Safe, values, "Code", column.Code,
                $"{found[0].Code}..{found[11].Code}"));
            return true;
        }

        // Селектор «поточного місяця»: його ім'я — псевдонім аргументу на наявні колонки, значення — похідне.
        aliasNamesColumn |= Enumerable.Range(1, 12)
            .Any(m => month.Target.MonthInputAliasesFor(to.Id, m).Any(a => string.Equals(a.Alias, column.Code, StringComparison.Ordinal)));
        if (column.IsComputed && aliasNamesColumn)
        {
            month.Recomputed.Add(column.Id);
            items.Add(new(path, RecomputedKind, ChangeClass.Guarded, values));
            return true;
        }

        if (values > 0)
        {
            month.Unmapped.Add(path);
        }

        return false;
    }
}
