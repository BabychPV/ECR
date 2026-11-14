using System.Diagnostics;
using System.Text.Json;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// B70/B87: виявлення прямої правки архіву табличних даних (<c>arc.CellValue</c>, <c>arc.TableRow</c>,
/// <c>arc.TableInstance</c>, <c>arc.CellPermitPart</c>, <c>arc.CellStyle</c>, <c>arc.CellFormulaOverride</c>) — звірка поточного відбитка кожного заархівованого періоду з еталоном, який записала
/// <c>arc.usp_ArchiveYear</c> у момент архівації.
/// </summary>
/// <remarks>
/// ⛔ Слово людини: «заархівовані дані не мають змінюватися; від адміна SQL ніщо не захищає». Застосунок архів не пише
/// (B69), а заборонити запис адміністраторові SQL неможливо — тож лишається ВИЯВИТИ: правка значення, вставка чи
/// видалення рядка архіву дає знахідку <c>ARCHIVE_CHECKSUM</c> із сутністю <c>arc.Period</c> (розширення наявного
/// правила, а не нове). Це виявлення, а не захист: адміністратор, який знає механізм, перепише й еталон у
/// <c>itg.ArchiveRun</c> (див. D-NEW про зовнішню фіксацію еталона).
/// <para>
/// ⚠ Відбиток — <c>arc.ArchiveRowHash</c> (<c>12-archive-tables.sql</c>): кількість рядків і сума 64-бітних SHA2-хешів
/// рядків окремо для комірок, рядків таблиць і екземплярів. Той самий вираз рахує й процедура, тож розбіжність формул
/// неможлива за побудовою.
/// </para>
/// <para>
/// ⚠ «Період заархівовано» — так само, як вирішує читач (<c>ArchiveAwareCellReader</c>): останній ЗАВЕРШЕНИЙ прогін,
/// що покриває період, — <c>ToArchive</c>. Після розархівації (<c>FromArchive</c>) архів періоду порожній законно, і
/// еталон старого прогону вже нічого не означає. Період, який зараз покриває незавершений прогін (<c>Running</c>),
/// пропускається: розархівація посеред кроку 2 вже стерла <c>arc.*</c>, але ще не записала <c>Completed</c> — інакше
/// найважча знахідка була б хибною.
/// </para>
/// <para>
/// ⚠ Еталона немає (архів зроблено до B70) — перевірка ЗАПИСУЄ його з поточного стану (<c>origin = baseline</c>) і
/// каже про це в підсумку прогону (<c>archiveBaselinesRecorded</c>). Зміна між архівацією і першим проходом після
/// оновлення так не виявиться — це названа ціна шляху оновлення; альтернатива (вічна знахідка «немає еталона» на
/// кожен старий рік) перетворила б журнал на шум, у якому справжня знахідка тоне (ФВ-7.7).
/// </para>
/// <para>
/// B87 (борг B70): відбиток покриває й <c>arc.CellPermitPart</c>, <c>arc.CellStyle</c>, <c>arc.CellFormulaOverride</c>
/// (види 4–6 в'юхи). ⛔ Еталон, записаний ДО B87, цих видів не має — його звіряють лише за комірками, рядками й
/// екземплярами (<see cref="PeriodPrint.SameData"/>), інакше оновлення дало б хибну знахідку на кожен рік зі стилями чи
/// частинами. Якщо старий еталон збігся, прохід ДОПОВНЮЄ його новими видами з поточного стану (ціна та сама, що в
/// базового еталона вище) і каже про це в підсумку (<c>archiveReferencesExtended</c>); наступна правка нових видів —
/// уже знахідка.
/// </para>
/// <para>
/// ⚠ Бюджет часу на прохід (<see cref="Budget"/>) і ротація початку за днем: рік прод-обсягу (~108 млн комірок)
/// перераховується хвилинами, і на кількох роках прохід не має права з'їсти ніч. Перевіряється щонайменше один період
/// за прохід; що не вклалося — наступної ночі починається з іншого місця, тож кожен період перевіряється принаймні
/// раз на N ночей (N — кількість заархівованих періодів). Неповний прохід не видаляє старих знахідок правила.
/// </para>
/// </remarks>
internal sealed class ArchiveTamperCheck(EcrDbContext db)
{
    /// <summary>Сутність знахідки (EntityId = PeriodKey).</summary>
    public const string EntityType = "arc.Period";

    /// <summary>Ключ каталогу тексту знахідки (після префікса <c>consistency.finding.</c>).</summary>
    public const string FindingKey = "ARCHIVE_TAMPER";

    /// <summary>Бюджет часу одного проходу на перерахунок відбитків.</summary>
    /// <remarks>
    /// Виміряно (B70, локальний SQL Server, tempdb): 1,76 млн рядків columnstore — 8,4 с, тобто ~5 мкс на рядок;
    /// рік Land (~176 тис. комірок) — до 2 с, рік прод-обсягу (~108 млн) — ~9 хв. Двадцять хвилин — два прод-роки
    /// за ніч; ключа конфігурації немає з тієї ж причини, що й у <see cref="ConsistencyCheckJob.CommandTimeoutSeconds"/>.
    /// </remarks>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(20);

    /// <summary>Стеля прогонів журналу, які читає прохід (сам журнал — сотні рядків).</summary>
    private const int MaxRuns = 10_000;

    /// <summary>Ширина діапазону прогону, понад яку він вважається пошкодженим і не розгортається в періоди.</summary>
    private const int MaxRangeWidth = 1_000;

    private const string StatusCompleted = "Completed";
    private const string StatusRunning = "Running";
    private const string OriginBaseline = "baseline";

    // ⚠ Поля B87 відсутні в записах еталона до B87 — і такими лишаються, коли прохід переписує документ прогону.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Перевіряє заархівовані періоди в межах бюджету.</summary>
    /// <param name="utcNow">Момент проходу (ротація початку за днем).</param>
    /// <param name="budget">Бюджет часу.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Розбіжності й підсумок проходу.</returns>
    public async Task<ArchiveTamperResult> RunAsync(DateTime utcNow, TimeSpan budget, CancellationToken ct)
    {
        var runs = await db.ArchiveRuns
            .AsNoTracking()
            .Where(r => r.Status == StatusCompleted || r.Status == StatusRunning)
            .OrderByDescending(r => r.StartedAt)
            .ThenByDescending(r => r.Id)
            .Take(MaxRuns)
            .Select(r => new RunRow(
                r.Id, r.Direction, r.Status, r.FromPeriodKey, r.ToPeriodKey, r.ChecksumSourceJson, r.ChecksumTargetJson))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var complete = runs.Count < MaxRuns;
        var plan = Plan(runs, ref complete);

        var tampers = new List<ArchiveTamper>();
        // Що дописати в еталон прогону: базові відбитки (еталона немає) і доповнення B87 (еталон до B87 збігся).
        var writes = new Dictionary<long, PendingWrite>();
        var verified = 0;
        var noReference = 0;

        var stopwatch = Stopwatch.StartNew();
        // Ротація: щодня обхід починається з наступного періоду, тож неповні проходи покривають усі періоди по черзі.
        var day = (utcNow.Date - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerDay;
        var start = plan.Count == 0 ? 0 : (int)(((day % plan.Count) + plan.Count) % plan.Count);

        for (var n = 0; n < plan.Count; n++)
        {
            // ⚠ Перший період — завжди: інакше прохід із вичерпаним бюджетом не просувався б жодної ночі.
            if (n > 0 && stopwatch.Elapsed > budget)
            {
                complete = false;
                break;
            }

            var period = plan[(start + n) % plan.Count];
            var current = await CurrentAsync(period.PeriodKey, ct).ConfigureAwait(false);
            verified++;

            if (period.Reference is { } reference)
            {
                if (!reference.Entry.SameData(current))
                {
                    tampers.Add(new ArchiveTamper(period.PeriodKey, reference.Run.Id, reference.Entry, current));
                }
                else if (!reference.Entry.CoversAllKinds)
                {
                    // B87: еталон до B87 (лише комірки, рядки, екземпляри) збігся — доповнюємо його частинами місяця,
                    // стилями й перевизначеннями з поточного стану, як базовий еталон B70; далі їхня правка — знахідка.
                    Pending(reference.Run).Extensions.Add(current);
                }
            }
            else if (period.BaselineRun is { } target)
            {
                Pending(target).Baselines.Add(current);
            }
            else
            {
                noReference++;
            }
        }

        var recordedBaselines = 0;
        var recordedExtensions = 0;
        foreach (var pending in writes.Values)
        {
            if (await RecordAsync(pending.Run, [.. pending.Baselines, .. pending.Extensions], ct).ConfigureAwait(false))
            {
                recordedBaselines += pending.Baselines.Count;
                recordedExtensions += pending.Extensions.Count;
            }
        }

        return new ArchiveTamperResult(tampers, complete, verified, recordedBaselines, noReference, recordedExtensions);

        PendingWrite Pending(RunRow run)
        {
            if (!writes.TryGetValue(run.Id, out var pending))
            {
                pending = new PendingWrite(run, [], []);
                writes[run.Id] = pending;
            }

            return pending;
        }
    }

    /// <summary>Заархівовані періоди з їхнім еталоном (або прогоном, куди записати базовий), за зростанням ключа.</summary>
    private static List<PlannedPeriod> Plan(List<RunRow> runs, ref bool complete)
    {
        var byPeriod = new SortedDictionary<int, List<RunRow>>();
        foreach (var run in runs)
        {
            if (run.ToPeriodKey < run.FromPeriodKey || run.ToPeriodKey - run.FromPeriodKey > MaxRangeWidth)
            {
                continue;
            }

            for (var key = run.FromPeriodKey; key <= run.ToPeriodKey; key++)
            {
                if (!byPeriod.TryGetValue(key, out var list))
                {
                    list = [];
                    byPeriod[key] = list;
                }

                // `runs` уже від найновішого — список кожного періоду теж.
                list.Add(run);
            }
        }

        var plan = new List<PlannedPeriod>();
        foreach (var (key, covering) in byPeriod)
        {
            if (covering.Exists(r => r.Status == StatusRunning))
            {
                complete = false;
                continue;
            }

            if (covering[0].Direction != ArchiveRun.ToArchive)
            {
                continue;
            }

            // Еталон — найновіший прогін ПІСЛЯ останньої розархівації (усі до першого FromArchive), що має запис
            // про цей період; затерте одне з двох полів — не еталон (це вже знахідка правила itg.ArchiveRun).
            Reference? reference = null;
            RunRow? baselineRun = null;
            foreach (var run in covering.TakeWhile(r => r.Direction == ArchiveRun.ToArchive))
            {
                var doc = Parse(run);
                if (doc is null)
                {
                    if (run.SourceJson is null && run.TargetJson is null)
                    {
                        baselineRun ??= run;
                    }

                    continue;
                }

                var entry = doc.Periods?.Find(p => p.P == key);
                if (entry is not null)
                {
                    reference = new Reference(run, entry);
                    break;
                }

                if (string.Equals(doc.Origin, OriginBaseline, StringComparison.Ordinal))
                {
                    baselineRun ??= run;
                }
            }

            plan.Add(new PlannedPeriod(key, reference, reference is null ? baselineRun : null));
        }

        return plan;
    }

    /// <summary>Еталон прогону, якщо обидва поля однакові й мають формат B70; інакше <c>null</c>.</summary>
    private static FingerprintDoc? Parse(RunRow run)
    {
        if (run.TargetJson is null || !string.Equals(run.SourceJson, run.TargetJson, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<FingerprintDoc>(run.TargetJson, Json);
            return doc is { V: 1, Periods: not null } ? doc : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Поточний відбиток періоду з <c>arc.ArchiveRowHash</c>.</summary>
    private async Task<PeriodPrint> CurrentAsync(int periodKey, CancellationToken ct)
    {
        // ⚠ Той самий агрегат, що й у `usp_ArchiveYear`: CONVERT(varchar(40), SUM(decimal(38,0))) — рядок, а не
        // decimal .NET (29 цифр), і порівняння рядків з еталоном точне.
        var parts = await db.Database
            .SqlQueryRaw<KindPrint>(
                """
                SELECT h.Kind AS Kind, COUNT_BIG(*) AS Total,
                       CONVERT(varchar(40), SUM(CAST(h.RowHash AS decimal(38,0)))) AS Digest
                  FROM arc.ArchiveRowHash AS h
                 WHERE h.PeriodKey = @periodKey
                 GROUP BY h.Kind
                """,
                new SqlParameter("@periodKey", periodKey))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        KindPrint Part(byte kind) => parts.Find(p => p.Kind == kind) ?? new KindPrint(kind, 0, "0");

        var cells = Part(1);
        var rows = Part(2);
        var instances = Part(3);
        var permitParts = Part(4);
        var styles = Part(5);
        var overrides = Part(6);

        return new PeriodPrint(periodKey, cells.Total, cells.Digest, rows.Total, rows.Digest, instances.Total, instances.Digest)
        {
            Pp = permitParts.Total,
            Pph = permitParts.Digest,
            S = styles.Total,
            Sh = styles.Digest,
            O = overrides.Total,
            Oh = overrides.Digest,
        };
    }

    /// <summary>
    /// Дописує відбитки періодів у еталон прогону — базові (еталона не було) і доповнення B87 (лише якщо поля не
    /// змінилися з моменту читання).
    /// </summary>
    /// <remarks>
    /// ⚠ Походження еталона зберігається: доповнений еталон процедури лишається <c>archive</c>, новий — <c>baseline</c>.
    /// Записи інших періодів переносяться як є; поля B87, яких у них немає, не дописуються (<c>null</c> не пишеться).
    /// </remarks>
    private async Task<bool> RecordAsync(RunRow run, List<PeriodPrint> entries, CancellationToken ct)
    {
        var existing = Parse(run);
        var periods = existing?.Periods?.ToList() ?? [];
        periods.RemoveAll(p => entries.Exists(e => e.P == p.P));
        periods.AddRange(entries);
        periods.Sort((a, b) => a.P.CompareTo(b.P));

        var json = JsonSerializer.Serialize(new FingerprintDoc(1, existing?.Origin ?? OriginBaseline, periods), Json);

        // ⚠ Оптимістична умова: паралельний прохід (нічний і ручний) або нова архівація могли записати своє — тоді
        // цей запис не перетирає чужого, а наступний прохід прочитає свіжий стан.
        var updated = await db.Database
            .ExecuteSqlRawAsync(
                """
                UPDATE itg.ArchiveRun
                   SET ChecksumSourceJson = @json, ChecksumTargetJson = @json
                 WHERE Id = @id
                   AND ((@old IS NULL AND ChecksumSourceJson IS NULL AND ChecksumTargetJson IS NULL)
                        OR (ChecksumSourceJson = @old AND ChecksumTargetJson = @old));
                """,
                [
                    new SqlParameter("@json", System.Data.SqlDbType.NVarChar, -1) { Value = json },
                    new SqlParameter("@id", run.Id),
                    new SqlParameter("@old", System.Data.SqlDbType.NVarChar, -1)
                    {
                        Value = (object?)run.TargetJson ?? DBNull.Value,
                    },
                ],
                ct)
            .ConfigureAwait(false);

        return updated == 1;
    }

    private sealed record RunRow(
        long Id, string Direction, string Status, int FromPeriodKey, int ToPeriodKey, string? SourceJson, string? TargetJson);

    private sealed record Reference(RunRow Run, PeriodPrint Entry);

    private sealed record PendingWrite(RunRow Run, List<PeriodPrint> Baselines, List<PeriodPrint> Extensions);

    private sealed record PlannedPeriod(int PeriodKey, Reference? Reference, RunRow? BaselineRun);

    private sealed record FingerprintDoc(int V, string? Origin, List<PeriodPrint>? Periods);

    private sealed record KindPrint(byte Kind, long Total, string Digest);
}

/// <summary>Відбиток одного періоду архіву (формат запису <c>periods[]</c> у <c>itg.ArchiveRun</c>).</summary>
/// <param name="P">Період.</param>
/// <param name="C">Кількість комірок.</param>
/// <param name="Ch">Сума хешів комірок.</param>
/// <param name="R">Кількість рядків таблиць.</param>
/// <param name="Rh">Сума хешів рядків.</param>
/// <param name="I">Кількість екземплярів таблиць.</param>
/// <param name="Ih">Сума хешів екземплярів.</param>
/// <remarks>
/// B87: частини місяця між дозволами (<see cref="Pp"/>), стилі комірок (<see cref="S"/>) і ручні перевизначення формул
/// (<see cref="O"/>) — необов'язкові поля. ⛔ Еталон архіву до B87 їх не має, і це не «змінено»: такий запис звіряється
/// лише за комірками, рядками й екземплярами — інакше після оновлення КОЖЕН рік, де є стилі чи частини, дав би хибну
/// знахідку найвищої ваги. Поточний відбиток (<c>ArchiveTamperCheck.CurrentAsync</c>) і еталон процедури після B87
/// мають їх завжди (порожні — 0).
/// </remarks>
internal sealed record PeriodPrint(int P, long C, string Ch, long R, string Rh, long I, string Ih)
{
    /// <summary>Кількість частин місяця (<c>arc.CellPermitPart</c>); <c>null</c> — еталон до B87.</summary>
    public long? Pp { get; init; }

    /// <summary>Сума хешів частин місяця.</summary>
    public string? Pph { get; init; }

    /// <summary>Кількість стилів комірок (<c>arc.CellStyle</c>); <c>null</c> — еталон до B87.</summary>
    public long? S { get; init; }

    /// <summary>Сума хешів стилів.</summary>
    public string? Sh { get; init; }

    /// <summary>Кількість ручних перевизначень формул (<c>arc.CellFormulaOverride</c>); <c>null</c> — еталон до B87.</summary>
    public long? O { get; init; }

    /// <summary>Сума хешів перевизначень.</summary>
    public string? Oh { get; init; }

    /// <summary>Чи несе відбиток усі види B87 (інакше це еталон до B87, який варто доповнити).</summary>
    public bool CoversAllKinds => Covers(Pp, Pph) && Covers(S, Sh) && Covers(O, Oh);

    /// <summary>
    /// Ті самі дані (період не порівнюється — його задає ключ пошуку). Викликається на ЕТАЛОНІ: вид B87, якого еталон не
    /// має, не порівнюється.
    /// </summary>
    /// <param name="other">Поточний відбиток.</param>
    /// <returns><c>true</c>, якщо кількості й суми хешів збігаються.</returns>
    public bool SameData(PeriodPrint other)
        => C == other.C && R == other.R && I == other.I
           && string.Equals(Ch, other.Ch, StringComparison.Ordinal)
           && string.Equals(Rh, other.Rh, StringComparison.Ordinal)
           && string.Equals(Ih, other.Ih, StringComparison.Ordinal)
           && SameKind(Pp, Pph, other.Pp, other.Pph)
           && SameKind(S, Sh, other.S, other.Sh)
           && SameKind(O, Oh, other.O, other.Oh);

    private static bool Covers(long? count, string? digest) => count is not null || digest is not null;

    // ⚠ Вид є в еталоні, якщо записано хоч одне з двох полів; тоді відсутнє друге — розбіжність, а не пропуск.
    private static bool SameKind(long? count, string? digest, long? otherCount, string? otherDigest)
        => !Covers(count, digest)
           || ((count ?? -1) == (otherCount ?? 0)
               && string.Equals(digest ?? string.Empty, otherDigest ?? "0", StringComparison.Ordinal));
}

/// <summary>Розбіжність архіву періоду з еталоном.</summary>
/// <param name="PeriodKey">Період.</param>
/// <param name="RunId">Прогін, що несе еталон.</param>
/// <param name="Recorded">Еталон.</param>
/// <param name="Current">Поточний відбиток.</param>
internal sealed record ArchiveTamper(int PeriodKey, long RunId, PeriodPrint Recorded, PeriodPrint Current);

/// <summary>Підсумок проходу <see cref="ArchiveTamperCheck"/>.</summary>
/// <param name="Tampers">Розбіжності.</param>
/// <param name="Complete">Чи перевірено всі заархівовані періоди (тоді зниклі знахідки можна видаляти).</param>
/// <param name="Verified">Скільки періодів перераховано.</param>
/// <param name="BaselinesRecorded">Скільки періодів отримали базовий еталон у цьому проході.</param>
/// <param name="NoReference">Скільки періодів лишилися без еталона (нема куди записати).</param>
/// <param name="ReferencesExtended">
/// B87: скільки еталонів до B87 доповнено частинами місяця, стилями й перевизначеннями в цьому проході.
/// </param>
internal sealed record ArchiveTamperResult(
    IReadOnlyList<ArchiveTamper> Tampers,
    bool Complete,
    int Verified,
    int BaselinesRecorded,
    int NoReference,
    int ReferencesExtended);
