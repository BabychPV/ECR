using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Reporting;

/// <summary>
/// Будує незмінні зрізи <c>rpt.*</c> для регламентної звітності.
/// </summary>
/// <remarks>
/// Межа з SSRS проходить саме тут: звіти лишаються в SSRS (<c>D-52</c>), а ми
/// віддаємо стабільний контракт даних. <c>rpt.*</c> — зріз **без логіки**:
/// агрегації робить цей сервіс, вʼюха лише проєктує (ФВ-0.3).
/// <para>
/// ✎ <c>D-52a</c>: поруч із SSRS зрізи читає сам застосунок, а колонки зрізу
/// задає опис версії. Для <c>rpt.*</c> це адитивно (<c>D-53</c>): опис із тими
/// самими п'ятьма колонками дає той самий вміст і ту саму суму.
/// </para>
/// </remarks>
public sealed class ReportSnapshotBuilder(EcrDbContext db, IClock clock) : IReportSnapshotBuilder
{
    /// <summary>Стеля рядків одного зрізу.</summary>
    /// <remarks>
    /// Річний звіт великого проєкту — десятки тисяч рядків. Межа існує не
    /// тому, що більше не буває, а тому, що без неї помилка в правилах відбору
    /// виглядала б як повільність, а не як помилка.
    /// </remarks>
    private const int MaxRows = 200_000;

    /// <inheritdoc />
    public async Task<long> BuildAsync(
        int reportVersionId,
        int projectId,
        PeriodKey? periodKey,
        string? parametersJson,
        CancellationToken ct)
    {
        var version = await db.ReportVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == reportVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Версії звіту {reportVersionId} не існує.");

        // ⛔ D-52a: колонки зрізу задає ОПИС. Розбирається ДО створення зрізу:
        // відмова після нього лишила б у `rpt.ReportSnapshot` порожній рядок.
        var layout = LayoutOf(version);

        // ⛔ R5: правила застосовуються до рядка джерела ДО запису й до суми — і ДО
        // створення зрізу: помилка правила на рядку не лишає порожнього зрізу.
        var rowRules = ReportRowRules.Parse(version.RulesJson, [.. layout.Select(c => c.Code)]);

        // ⛔ R6: значення параметрів зводяться з оголошеннями ВДРУГЕ. Перший раз
        // це зробив обробник запиту (щоб відмовити 422 одразу), але задача може
        // прийти й не звідти — з розкладу або з черги, пережившої переїзд.
        var parameters = ReportParameters.Bind(rowRules.Parameters, parametersJson);

        var cells = await AggregateAsync(layout, rowRules, parameters.Values, projectId, periodKey, ct)
            .ConfigureAwait(false);

        // ⚠ Статус УСПАДКОВУЄТЬСЯ від даних (D-65). Окреме поле «статус звіту»
        // стало б другим джерелом істини і рано чи пізно показало б регулятору
        // Approved на чернетці.
        var status = await StatusOfDataAsync(projectId, periodKey, ct).ConfigureAwait(false);

        var snapshot = new ReportSnapshot(
            reportVersionId, projectId, periodKey?.Value, status, clock.UtcNow, builtByUserId: null);

        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Агрегації виконуються ТУТ, одним набором запитів. Вʼюха rpt.v_*
        // нічого не рахує (ФВ-0.3): індексована вʼюха з обчисленнями не
        // перебудовується інкрементно і зупиняє запис у джерело.
        var rows = cells.ConvertAll(c => Cell(snapshot.Id, c.RowNo, c.Code, c.Text, c.Number));

        db.ReportRows.AddRange(rows);

        snapshot.Complete(
            rows.Count,
            ComputeHash(rows),

            // Прогін, з якого взято числа: без нього неможливо сказати, на
            // чому стоїть значення у звіті.
            await CurrentRunAsync(projectId, periodKey, ct).ConfigureAwait(false),

            // ⚠ Записуються ВИКОРИСТАНІ значення, а не надіслані: замовчування
            // вже підставлені. Інакше зріз, побудований без жодного параметра,
            // не давав би відповіді на питання «з чим його рахували».
            parameters.Json ?? parametersJson);

        // Сума щойно порахована `ComputeHash`, тобто поточним форматом: формат
        // зберігається одразу, а не визначається потім перерахунком.
        snapshot.RecordHashFormat(VerifyReportSnapshotHandler.FormatCurrent);

        await SwitchCurrentAsync(snapshot, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return snapshot.Id;
    }

    /// <inheritdoc />
    public async Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct)
    {
        var snapshot = await db.ReportSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Зрізу {snapshotId} не існує.");

        // ⛔ Після подання зріз ІММУТАБЕЛЬНИЙ. Повторна побудова створює НОВИЙ
        // зріз, а не переписує цей: інакше звіт, роздрукований учора, і той
        // самий звіт сьогодні дали б різні числа без жодного сліду.
        snapshot.MarkSubmitted(userId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct)
    {
        var snapshot = await db.ReportSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Зрізу {snapshotId} не існує.");

        // Поданий зріз статусу не міняє — це відхиляє сама сутність.
        if (snapshot.Status == SnapshotStatus.Submitted)
        {
            return snapshot.Status;
        }

        var status = await StatusOfDataAsync(
            snapshot.ProjectId,
            snapshot.PeriodKey is { } key ? new PeriodKey(key) : null,
            ct).ConfigureAwait(false);

        snapshot.RefreshStatus(status);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return status;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Найновіші першими і зі стелею. Зрізів за рік накопичуються тисячі:
    /// перелік «усіх» довелося б гортати саме тоді, коли потрібен останній.
    /// </remarks>
    public async Task<IReadOnlyList<ReportSnapshotSummary>> ListAsync(
        int? projectId, int? periodKey, IReadOnlyCollection<int>? visibleProjectIds, CancellationToken ct)
    {
        // ⛔ Порожній перелік видимих проєктів — це «жодного», а не «усі»
        // (Q-239). Різниця тут і є вся різниця між фільтром і його
        // відсутністю: користувач без жодного гранта на проєкт мусить бачити
        // порожньо, а не всю базу.
        if (visibleProjectIds is { Count: 0 })
        {
            return [];
        }

        var query = db.ReportSnapshots
            .AsNoTracking()
            .Where(s => projectId == null || s.ProjectId == projectId)
            .Where(s => periodKey == null || s.PeriodKey == periodKey);

        if (visibleProjectIds is not null)
        {
            // ⚠ Матеріалізований масив, а не сам інтерфейс: EF перекладає
            // `Contains` по параметру-колекції, і форма з `null`-перевіркою
            // всередині виразу («visible == null || visible.Contains(…)») не
            // транслювалася б — умова будується поза виразом.
            var visible = visibleProjectIds as int[] ?? [.. visibleProjectIds];
            query = query.Where(s => visible.Contains(s.ProjectId));
        }

        return await query
            .OrderByDescending(s => s.BuiltAt)
            .Take(MaxSnapshots)
            .Select(s => new ReportSnapshotSummary(
                s.Id,
                s.ReportVersionId,
                s.ProjectId,
                s.PeriodKey,
                s.Status.ToString(),
                s.IsCurrent,
                s.RowCount,

                // ⚠ Сума віддається рядком. Байти в JSON перетворюються на
                // base64, який неможливо звірити очима з тим, що показує
                // SSRS, — а звіряють їх саме очима.
                s.ContentHash == null ? null : Convert.ToHexString(s.ContentHash),
                s.BuiltAt)
            {
                HashFormat = s.HashFormat ?? VerifyReportSnapshotHandler.FormatUnknown,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int?> FindProjectIdAsync(long snapshotId, CancellationToken ct)
        => await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => (int?)s.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<SnapshotHashes?> VerifyAsync(long snapshotId, CancellationToken ct)
    {
        var stored = await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => s.Id == snapshotId)
            .Select(s => new StoredHash(s.Id, s.ContentHash))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        // ⚠ Порядок — той самий, у якому рядки хешувалися при побудові:
        // `RowNo` зростає, а комірки рядка йдуть у порядку колонок ОПИСУ.
        // Первинний ключ (SnapshotId, RowNo, ColumnCode) дав би АЛФАВІТНИЙ
        // порядок колонок, тому комірки одного рядка впорядковує опис, а не база.
        var described = await DescribedColumnsAsync(snapshotId, ct).ConfigureAwait(false) ?? [];

        var rows = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .OrderBy(r => r.RowNo)
            .Take(MaxRows * Math.Max(described.Count, LegacyColumns.Length))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var order = StoredLayout(described, rows).Select(c => c.Code).ToList();

        var ordered = rows
            .OrderBy(r => r.RowNo)
            .ThenBy(r => ColumnOrder(order, r.ColumnCode))
            .ThenBy(r => r.ColumnCode, StringComparer.Ordinal)
            .ToList();

        var storedHex = stored.Hash is null ? string.Empty : Convert.ToHexString(stored.Hash);
        var actualHex = Convert.ToHexString(ComputeHash(ordered));

        // Стара сума рахується лише тоді, коли нова не збіглася: для зрізів,
        // побудованих після BE-17, вона не потрібна взагалі.
        var legacyHex = string.Equals(storedHex, actualHex, StringComparison.Ordinal)
            ? null
            : Convert.ToHexString(ComputeLegacyHash(ordered));

        return new SnapshotHashes(storedHex, actualHex, legacyHex);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Умова <c>HashFormat IS NULL</c> стоїть у самому UPDATE, а не в перевірці
    /// перед ним: звірка й нічна задача можуть писати той самий зріз одночасно.
    /// Один рядок, автокоміт — жодної довгої транзакції.
    /// </remarks>
    public async Task<bool> RecordHashFormatAsync(long snapshotId, string format, CancellationToken ct)
        => await db.ReportSnapshots
            .Where(s => s.Id == snapshotId && s.HashFormat == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.HashFormat, format), ct)
            .ConfigureAwait(false) > 0;

    /// <inheritdoc />
    public async Task<SnapshotRowsPage?> RowsAsync(
        long snapshotId, int afterRowNo, int limit, string language, CancellationToken ct)
    {
        var version = await VersionOfAsync(snapshotId, ct).ConfigureAwait(false);

        if (version is null)
        {
            return null;
        }

        var described = ReportColumnSpec.Parse(version.ColumnsJson);

        // ⛔ R8: макет застосовується ТУТ, на видачі, а не при побудові — у
        // `rpt.ReportRow` і в `ContentHash` його немає (D-53: зміна адитивна).
        var layout = ReportLayout.Of(
            version.RulesJson, [.. described.Select(c => new ReportColumnCommand(c.Code, c.Kind))]);

        if (!layout.IsEmpty)
        {
            return await LaidOutRowsAsync(snapshotId, described, layout, afterRowNo, limit, language, ct)
                .ConfigureAwait(false);
        }

        // Номери рядків окремим запитом: сторінка рахується в РЯДКАХ звіту, а
        // `rpt.ReportRow` зберігає комірки, і їх у рядку стільки, скільки колонок.
        var rowNos = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && r.RowNo > afterRowNo)
            .Select(r => r.RowNo)
            .Distinct()
            .OrderBy(n => n)
            .Take(limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var last = rowNos.Take(limit).LastOrDefault();

        var cells = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId && r.RowNo > afterRowNo && r.RowNo <= last)
            .OrderBy(r => r.RowNo)
            .Take(limit * MaxColumnsPerRow)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stored = StoredLayout(described, cells);

        return new SnapshotRowsPage(
            Titled(stored, language),
            WideRows(cells, stored),
            rowNos.Count > limit ? last : null);
    }

    /// <summary>
    /// Сторінка зрізу, розкладеного макетом (<c>R8</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Читається ВЕСЬ зріз, а сторінка нарізається вже з упорядкованого:
    /// група й підсумок — властивості ЗРІЗУ, і порахувати їх по сторінці
    /// означало б «суму», яка на кожній сторінці інша. Ціна названа — повне
    /// читання на КОЖНУ сторінку, межа та сама <see cref="MaxRows"/>, що й у
    /// перевірки суми; дешевший порядок у SQL вимагав би рахувати підсумки
    /// другим кодом, а саме цього <c>R8</c> і не робить.
    /// <para>
    /// ⚠ Курсор тут означає, СКІЛЬКИ рядків уже віддано, а не останній
    /// <c>RowNo</c>: у порядку груп <c>RowNo</c> не зростає. Без макета обидва
    /// числа збігаються, тож клієнт випадків не розрізняє.
    /// </para>
    /// </remarks>
    private async Task<SnapshotRowsPage> LaidOutRowsAsync(
        long snapshotId, IReadOnlyList<ReportColumnSpec> described, ReportLayout layout,
        int delivered, int limit, string language, CancellationToken ct)
    {
        var cells = await db.ReportRows
            .AsNoTracking()
            .Where(r => r.SnapshotId == snapshotId)
            .OrderBy(r => r.RowNo)
            .Take(MaxRows * Math.Max(described.Count, LegacyColumns.Length))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var stored = StoredLayout(described, cells);
        var view = layout.Apply(WideRows(cells, stored));
        var page = view.Rows.Skip(delivered).Take(limit).ToList();

        return new SnapshotRowsPage(
            Titled(stored, language),
            page,
            delivered + page.Count < view.Rows.Count ? delivered + page.Count : null,
            view.Groups,
            view.Totals,
            layout.ShowGroupHeader);
    }

    /// <summary>Колонки зрізу, підписані мовою запиту (<c>R9</c>).</summary>
    /// <remarks>
    /// ⚠ Фолбек лежить в <see cref="ReportColumnNames"/>, а не тут: книга бере
    /// вже підписані колонки з цієї самої сторінки, і друга копія ланцюга
    /// розійшлася б із першою мовчки.
    /// </remarks>
    private static IReadOnlyList<SnapshotColumn> Titled(
        IReadOnlyList<ReportColumnSpec> columns, string language)
        => [.. columns.Select(c => new SnapshotColumn(
            c.Code, c.Kind, ReportColumnNames.Of(c.Code, c.NameL10n, language)))];

    /// <summary>Комірки зрізу, зведені в рядки: значення за кодом колонки в порядку опису.</summary>
    private static List<SnapshotRow> WideRows(
        IReadOnlyList<ReportRow> cells, IReadOnlyList<ReportColumnSpec> columns)
        => [.. cells
            .GroupBy(c => c.RowNo)
            .OrderBy(g => g.Key)
            .Select(g => new SnapshotRow(
                g.Key,
                columns.ToDictionary(
                    c => c.Code,
                    c => ValueOf(g.FirstOrDefault(x => x.ColumnCode == c.Code), c.Kind),
                    StringComparer.Ordinal)))];

    /// <summary>Стеля комірок одного рядка у сторінці рядків.</summary>
    private const int MaxColumnsPerRow = 64;

    /// <summary>Значення комірки для відповіді: число без хвостових нулів масштабу бази.</summary>
    private static object? ValueOf(ReportRow? cell, string kind)
        => kind switch
        {
            _ when cell is null => null,
            ReportSourceColumns.Number => cell.ValueNumeric is { } number
                ? decimal.Parse(Canonical(number), CultureInfo.InvariantCulture)
                : null,
            "date" => cell.ValueDate?.ToString("O", CultureInfo.InvariantCulture),
            _ => cell.ValueString,
        };

    /// <summary>
    /// Колонки, які зрізи мали ДО <c>D-52a</c>: будівник писав їх завжди, хоч би що стояло в описі.
    /// </summary>
    private static readonly ReportColumnSpec[] LegacyColumns =
    [
        new("DocumentId", ReportSourceColumns.Number),
        new("RowKey", ReportSourceColumns.Text),
        new("OutputCode", ReportSourceColumns.Text),
        new("Value", ReportSourceColumns.Number),
        new("SubstanceEntryId", ReportSourceColumns.Number),
    ];

    /// <summary>Колонки опису версії, за якою побудовано зріз; <c>null</c> — зрізу немає.</summary>
    private async Task<IReadOnlyList<ReportColumnSpec>?> DescribedColumnsAsync(long snapshotId, CancellationToken ct)
        => await VersionOfAsync(snapshotId, ct).ConfigureAwait(false) is { } version
            ? ReportColumnSpec.Parse(version.ColumnsJson)
            : null;

    /// <summary>Опис версії, за якою побудовано зріз; <c>null</c> — зрізу немає.</summary>
    /// <remarks>
    /// ⚠ Колонки й правила беруться ОДНИМ запитом: читати їх окремо означало б
    /// два звернення на кожну сторінку рядків, бо макет (<c>R8</c>) лежить у
    /// <c>RulesJson</c>, а колонки — в <c>ColumnsJson</c>.
    /// </remarks>
    private Task<StoredVersion?> VersionOfAsync(long snapshotId, CancellationToken ct)
        => (from snapshot in db.ReportSnapshots.AsNoTracking()
            join version in db.ReportVersions.AsNoTracking() on snapshot.ReportVersionId equals version.Id
            where snapshot.Id == snapshotId
            select new StoredVersion(version.ColumnsJson, version.RulesJson))
            .FirstOrDefaultAsync(ct);

    /// <summary>Опис версії зрізу так, як він збережений.</summary>
    private sealed record StoredVersion(string ColumnsJson, string RulesJson);

    /// <summary>Колонки ЗБЕРЕЖЕНОГО зрізу в порядку, у якому їх складала побудова.</summary>
    /// <remarks>
    /// ⛔ Зріз, побудований до <c>D-52a</c>, має п'ять колонок незалежно від
    /// опису. Ознака — у рядках є код, якого опис не знає; тоді порядок старий,
    /// інакше сума такого зрізу перестала б збігатися (BE-17).
    /// </remarks>
    private static IReadOnlyList<ReportColumnSpec> StoredLayout(
        IReadOnlyList<ReportColumnSpec> described, IReadOnlyList<ReportRow> cells)
    {
        var known = described.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

        return known.Count > 0 && cells.All(c => known.Contains(c.ColumnCode)) ? described : LegacyColumns;
    }

    private static int ColumnOrder(List<string> order, string columnCode)
    {
        var index = order.IndexOf(columnCode);
        return index < 0 ? order.Count : index;
    }

    /// <summary>Збережена сума зрізу.</summary>
    private sealed record StoredHash(long Id, byte[]? Hash);

    /// <summary>Стеля переліку зрізів.</summary>
    private const int MaxSnapshots = 500;

    /// <summary>
    /// Статус, виведений зі стану аркушів проєкту.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Approved</c> лише тоді, коли затверджені **всі** аркуші. Один
    /// незатверджений аркуш робить увесь зріз чернетковим — і це правильно:
    /// звіт, у якому половина даних погоджена, а половина ні, не є погодженим
    /// звітом (D-65).
    /// </remarks>
    private async Task<SnapshotStatus> StatusOfDataAsync(
        int projectId, PeriodKey? periodKey, CancellationToken ct)
    {
        var query =
            from state in db.ApprovalStates.AsNoTracking()
            join document in db.Documents.AsNoTracking()
                on state.DocumentId equals document.Id
            where document.ProjectId == projectId
                  && (periodKey == null || state.PeriodKey == periodKey.Value.Value)
            select state.Status;

        var statuses = await query.Take(MaxRows).ToListAsync(ct).ConfigureAwait(false);

        // Аркушів немає — зріз чернетковий. «Нічого не подано» і «все
        // затверджено» не можна плутати: перше означає порожній звіт.
        if (statuses.Count == 0)
        {
            return SnapshotStatus.Draft;
        }

        if (statuses.TrueForAll(s => s == DocumentStatus.Approved))
        {
            return SnapshotStatus.Approved;
        }

        return statuses.Exists(s => s is DocumentStatus.Submitted or DocumentStatus.Approved)
               && !statuses.Exists(s => s is DocumentStatus.Draft or DocumentStatus.Rejected)
            ? SnapshotStatus.Submitted
            : SnapshotStatus.Draft;
    }

    /// <summary>Агрегує результати розрахунку в рядки зрізу.</summary>
    private async Task<List<CellValue>> AggregateAsync(
        IReadOnlyList<ReportColumnSpec> layout, ReportRowRules rowRules,
        IReadOnlyDictionary<string, object?> parameters, int projectId, PeriodKey? periodKey,
        CancellationToken ct)
    {
        var query =
            from result in db.CalculationResults.AsNoTracking()
            join document in db.Documents.AsNoTracking()
                on result.DocumentId equals document.Id
            join run in db.CalculationRuns.AsNoTracking()
                on result.CalculationRunId equals run.Id
            join unit in db.Units.AsNoTracking()
                on result.UnitId equals unit.Id
            join project in db.Projects.AsNoTracking()
                on document.ProjectId equals project.Id
            where document.ProjectId == projectId
                  && (periodKey == null || result.PeriodKey == periodKey.Value.Value)

                  // ⚠ Лише АКТУАЛЬНИЙ прогін. Без цієї умови зріз склав би
                  // результати всіх прогонів разом — числа виросли б кратно
                  // кількості перерахунків і лишилися б правдоподібними.
                  && run.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus

            // ⛔ Сортування стоїть ДО проєкції, і це не косметика. Поки
            // `OrderBy` висів на вже спроєктованому `ResultRow`, EF не міг
            // перекласти запит узагалі: `ResultRow` — тип застосунку, і
            // впорядкувати за його властивістю в SQL нема як. Побудова зрізу
            // від цього не «була повільною» — вона падала
            // `InvalidOperationException` («could not be translated») на
            // КОЖНОМУ виклику, тобто не завершилася успіхом жодного разу за
            // весь час існування `rpt.*`. Не бачив цього ніхто: `ReportDef`
            // не створювало ніщо, тож до цього рядка виконання не доходило —
            // побудова відмовляла раніше, `ECR-RPT-0404` (директива №09
            // `W7`, сценарій `S-27`).
            orderby result.DocumentId, result.SourceRowKey, result.OutputCode
            select new ResultRow(
                result.DocumentId, result.SourceRowKey, result.OutputCode,
                result.Value, result.SubstanceEntryId,
                result.PeriodKey, result.UnitId, unit.Code, result.MethodologyVersionId, project.Code);

        var results = await query
            .Take(MaxRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rows = new List<CellValue>(results.Count * layout.Count);
        var rowNo = 0;

        foreach (var result in results)
        {
            // Без правил (схема 1) рядок читається прямо з джерела, як до R5.
            var ruled = rowRules.IsEmpty
                ? null
                : Readers.ToDictionary(r => r.Key, r => r.Value(result), StringComparer.Ordinal);

            if (ruled is not null && !rowRules.Apply(ruled, parameters))
            {
                // Прихований рядок номера не займає: `RowNo` лишається суцільним.
                continue;
            }

            rowNo++;

            // ⛔ Лише описані колонки і в порядку опису (D-52a): за цим порядком
            // рахується сума, і за ним її перераховує `VerifyAsync`.
            foreach (var column in layout)
            {
                var value = ruled is null ? Readers[column.Code](result) : ruled[column.Code];

                rows.Add(column.Kind == ReportSourceColumns.Number
                    ? new CellValue(rowNo, column.Code, null, (decimal?)value)
                    : new CellValue(rowNo, column.Code, (string?)value, null));
            }
        }

        return rows;
    }

    /// <summary>Значення комірки до появи зрізу: ідентифікатор зрізу додається після правил.</summary>
    private readonly record struct CellValue(int RowNo, string Code, string? Text, decimal? Number);

    /// <summary>Як прочитати кожне поле джерела <c>CalculationResults</c>.</summary>
    /// <remarks>
    /// Перелік кодів і їхні типи — у <see cref="ReportSourceColumns"/>; рівність
    /// двох таблиць стереже тест. Ідентифікатори йдуть як <c>decimal</c> з
    /// масштабом 0 — рівно так їх писав код до <c>D-52a</c> (формат <c>legacy</c>).
    /// </remarks>
    private static readonly Dictionary<string, Func<ResultRow, object?>> Readers = new(StringComparer.Ordinal)
    {
        ["DocumentId"] = r => (decimal)r.DocumentId,
        ["RowKey"] = r => r.SourceRowKey,
        ["OutputCode"] = r => r.OutputCode,
        ["Value"] = r => r.Value,
        ["SubstanceEntryId"] = r => (decimal?)r.SubstanceEntryId,
        ["PeriodKey"] = r => (decimal)r.PeriodKey,
        ["UnitId"] = r => (decimal)r.UnitId,
        ["UnitCode"] = r => r.UnitCode,
        ["MethodologyVersionId"] = r => (decimal)r.MethodologyVersionId,
        ["ProjectCode"] = r => r.ProjectCode,
    };

    /// <summary>Коди колонок, які будівник уміє прочитати з джерела.</summary>
    public static IReadOnlyCollection<string> ReadableColumns => Readers.Keys;

    /// <summary>Колонки зрізу за описом версії; відмовляє, якщо побудувати за ним не можна.</summary>
    /// <remarks>
    /// Створення версії таке відсіює (<see cref="ReportSourceColumns.Require"/>);
    /// сюди доходить лише опис, заведений до <c>D-52a</c> або повз застосунок.
    /// Мовчки пропустити колонку означало б зріз, у якому менше, ніж обіцяє опис.
    /// </remarks>
    private static IReadOnlyList<ReportColumnSpec> LayoutOf(ReportVersion version)
    {
        var rules = ReportRules.Parse(version.RulesJson);
        var columns = ReportColumnSpec.Parse(version.ColumnsJson);

        var broken = columns.FirstOrDefault(c =>
            !Readers.ContainsKey(c.Code)
            || !string.Equals(ReportSourceColumns.KindOf(rules.RowSource, c.Code), c.Kind, StringComparison.Ordinal));

        if (columns.Count == 0 || !ReportRowRules.IsSupported(rules.Schema) || broken is not null)
        {
            throw new InvalidOperationException(
                $"Версія звіту {version.Id}: за описом зріз не будується "
                + $"(схема {rules.Schema}, колонок {columns.Count}, непридатна колонка «{broken?.Code}»).");
        }

        return columns;
    }

    /// <summary>Актуальний прогін проєкту й періоду.</summary>
    private Task<long?> CurrentRunAsync(int projectId, PeriodKey? periodKey, CancellationToken ct)
        => db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && (periodKey == null || r.PeriodKey == periodKey.Value.Value)
                        && r.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Робить зріз поточним, знімаючи поточність із попереднього.
    /// </summary>
    /// <remarks>
    /// ⚠ Обидві половини — в одному наборі змін, який коміт застосує разом.
    /// Між ними існує стан із двома поточними зрізами, і регуляторна вʼюха в
    /// цю мить повернула б подвоєні рядки — не помилку, а просто вдвічі більше
    /// число. Фільтрований унікальний індекс не дав би це зберегти, але вже
    /// після того, як транзакція впала б посеред побудови.
    /// </remarks>
    private async Task SwitchCurrentAsync(ReportSnapshot snapshot, CancellationToken ct)
    {
        var previous = await db.ReportSnapshots
            .Where(s => s.ReportVersionId == snapshot.ReportVersionId
                        && s.ProjectId == snapshot.ProjectId
                        && s.PeriodKey == snapshot.PeriodKey
                        && s.Id != snapshot.Id
                        && s.IsCurrent)
            .Take(MaxCurrentSnapshots)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var stale in previous)
        {
            stale.Supersede();
        }

        snapshot.MakeCurrent();
    }

    /// <summary>Стеля на кількість зрізів, з яких знімається поточність.</summary>
    /// <remarks>
    /// Поточний зріз мусить бути рівно один — це тримає фільтрований
    /// унікальний індекс. Більший список означає зіпсовані дані, і межа не дає
    /// такій зіпсованості перетворитися на довгу транзакцію.
    /// </remarks>
    private const int MaxCurrentSnapshots = 100;

    /// <summary>Комірка зрізу.</summary>
    private static ReportRow Cell(
        long snapshotId, int rowNo, string columnCode, string? text, decimal? number)
    {
        var row = new ReportRow(snapshotId, rowNo, columnCode);
        row.SetValue(text, number, null);
        return row;
    }

    /// <summary>
    /// Контрольна сума вмісту зрізу.
    /// </summary>
    /// <remarks>
    /// Рахується за ДАНИМИ у стабільному порядку, а не за часом побудови: два
    /// зрізи з однаковими числами мусять мати однакову суму, інакше нею
    /// неможливо довести, що звіт не змінився.
    /// </remarks>
    /// <param name="rows">Рядки зрізу в порядку побудови.</param>
    public static byte[] ComputeHash(IReadOnlyList<ReportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return HashOf(rows, r => Canonical(r.ValueNumeric));
    }

    /// <summary>
    /// Контрольна сума за форматом ДО BE-17, відтворена зі збережених рядків.
    /// </summary>
    /// <remarks>
    /// ⚠ ТИМЧАСОВО прийнятний формат: приймається для зрізів, побудованих до
    /// BE-17; прибрати, коли таких не лишиться (жоден <c>rpt.ReportSnapshot</c>
    /// не збігається за ним — або всі старі перебудовано).
    /// <para>
    /// Стара сума писала число як <c>decimal.ToString()</c>, тобто з масштабом,
    /// який число мало В МИТЬ ПОБУДОВИ. Масштаб у базі втрачено (усе має
    /// масштаб стовпця: до <c>D-148</c> — 10, тепер — 16), але для рядків, які
    /// складає <c>AggregateAsync</c>, він відновлюється з коду колонки
    /// однозначно: <c>DocumentId</c> і <c>SubstanceEntryId</c> — це
    /// <c>long</c> (масштаб 0, «4217»), а <c>Value</c> читалось із
    /// <c>calc.CalculationResult.Value</c>, ТОДІ колонки <c>decimal(28,10)</c>,
    /// і SqlClient віддавав його з масштабом 10 («12.5000000000»).
    ///
    /// ⚠ Саме тому <see cref="LegacyNumber"/> друкує <c>F10</c> ЛІТЕРАЛОМ і
    /// переходу на 16 знаків не помічає: формат відтворює те, як число
    /// виглядало ДО BE-17, а не те, як воно лежить у стовпці сьогодні.
    /// Порядок комірок той самий, що й тепер.
    /// </para>
    /// <para>
    /// ⛔ Межа: зріз, рядки якого складено НЕ побудовою (інші колонки, число
    /// з іншим масштабом), за старим форматом не відтворюється — і тоді
    /// відповідь лишається «не збігається», бо довести протилежне нема чим.
    /// </para>
    /// </remarks>
    private static byte[] ComputeLegacyHash(IReadOnlyList<ReportRow> rows)
        => HashOf(rows, LegacyNumber);

    /// <summary>Число так, як його друкував код до BE-17 у мить побудови.</summary>
    private static string LegacyNumber(ReportRow row)
    {
        if (row.ValueNumeric is not { } number)
        {
            return string.Empty;
        }

        // ⛔ Ціла форма — лише для справді цілого ідентифікатора. Відкинути
        // дріб беззастережно означало б сховати підміну `4217` → `4217.5`.
        var isId = row.ColumnCode is "DocumentId" or "SubstanceEntryId";

        return isId && number == decimal.Truncate(number)
            ? decimal.Truncate(number).ToString(CultureInfo.InvariantCulture)
            : number.ToString("F10", CultureInfo.InvariantCulture);
    }

    private static byte[] HashOf(IReadOnlyList<ReportRow> rows, Func<ReportRow, string> number)
    {
        var text = string.Join(
            '\n',
            rows.Select(r => string.Create(
                CultureInfo.InvariantCulture,
                $"{r.RowNo}|{r.ColumnCode}|{r.ValueString}|{number(r)}")));

        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Число без хвостових нулів, із точністю колонки значень.</summary>
    /// <remarks>
    /// ⛔ BE-17. <c>decimal</c> у .NET несе МАСШТАБ: <c>5m</c> друкується «5», а
    /// те саме число, прочитане з <c>decimal(34,16)</c>, — «5.0000000000000000». Поки
    /// суму рахували лише при побудові, цього не було видно; перерахунок за
    /// збереженими рядками давав би іншу суму на КОЖНОМУ зрізі з числами, тобто
    /// перевірка завжди казала б «вміст змінено».
    /// <para>
    /// ⚠ Знаків **16**, а не 10. Формат розширено ОКРЕМИМ комітом ПЕРЕД
    /// переходом <c>rpt.ReportRow.ValueNumeric</c> на <c>decimal(34,16)</c>
    /// (міграція <c>D148ReportingAndSourceScale16</c>), і порядок тут — не
    /// смак. Формат
    /// обрізає хвостові нулі, тому для значень із ≤ 10 знаками рядок
    /// ПОБАЙТНО той самий, що й до розширення, і вже збережені суми лишаються
    /// чинними. Розширити ПІСЛЯ колонки означало б вікно, у якому два різні
    /// числа (різниця на 11–16 знаку) дають одну суму — тобто «вміст не
    /// змінювався» там, де він змінився.
    /// </para>
    /// </remarks>
    private static string Canonical(decimal? value)
        => value is { } number
            ? number.ToString("0.################", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>Результат розрахунку для агрегації.</summary>
    /// <remarks>
    /// Названий тип, а не анонімний: анонімний розриває вираз фігурною дужкою,
    /// і архітектурне правило «<c>ToListAsync</c> без <c>Take</c>» бачить
    /// половину інструкції без межі (`D1-08`).
    /// </remarks>
    private sealed record ResultRow(
        long DocumentId, string? SourceRowKey, string OutputCode, decimal Value, long? SubstanceEntryId,
        int PeriodKey, int UnitId, string UnitCode, int MethodologyVersionId, string ProjectCode);
}

/// <summary>Опис колонок звіту, що зберігається у <c>ReportVersion.ColumnsJson</c>.</summary>
/// <param name="Code">Код колонки — він же ключ у рядку зрізу.</param>
/// <param name="Kind">Тип значення: <c>text</c>, <c>number</c>, <c>date</c>.</param>
/// <param name="NameL10n">
/// Підписи колонки мовами каталогу (<c>R9</c>); <c>null</c> — опис назв не має,
/// і колонка підписується КОДОМ, як до <c>R9</c>. Читається тим самим іменем
/// поля, яким його пише <see cref="ReportColumnCommand"/>.
/// </param>
public sealed record ReportColumnSpec(
    string Code, string Kind, IReadOnlyDictionary<string, string>? NameL10n = null)
{
    /// <summary>Налаштування розбору; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Читає опис колонок із JSON версії звіту.</summary>
    /// <param name="columnsJson">Вміст <c>ColumnsJson</c>.</param>
    /// <returns>Колонки; порожній перелік, якщо опис зламаний.</returns>
    /// <remarks>
    /// Сам розбір не кидає; що робити з порожнім переліком, вирішує споживач.
    /// Побудова (<c>LayoutOf</c>) за ним відмовляє, перевірка й перегляд рядків
    /// читають зріз як побудований до <c>D-52a</c>.
    /// </remarks>
    public static IReadOnlyList<ReportColumnSpec> Parse(string columnsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ReportColumnSpec>>(columnsJson, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
