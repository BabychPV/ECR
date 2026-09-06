using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.DataGen;

/// <summary>
/// Заміри гейта BR-07 — шість критеріїв із <c>B02</c> §7.
/// </summary>
/// <remarks>
/// ⚠ Найважливіший і найлегший для пропуску — замір №6: **125 RPS в одну
/// партицію**. Пік у ECR не розподілений: усі користувачі в останні дні
/// періоду б'ють в один період. Рівномірне навантаження на 12 партицій
/// нічого не доводить.
///
/// ⛔ Клас не мав жодної точки входу до 2026-09-06: він був описаний у
/// `05j-skeleton-tools.md`, зарахований у `progress.md` як «5 із 6 замірів
/// гейта» — і не викликався **нізвідки**. Запускається через
/// <c>Ecr.DataGen --gate</c>, і саме звідти береться ненульовий код виходу.
/// </remarks>
public sealed class GateBenchmark
{
    /// <summary>Критерій №1: <c>ReadSliceAsync</c> на 500×60, p95.</summary>
    private const double SliceBudgetMs = 600;

    /// <summary>Критерій №2: <c>ApplyAsync</c> на 100 комірок, p95.</summary>
    private const double ApplyBudgetMs = 150;

    /// <summary>Критерій №3: агрегація по періоду, p95.</summary>
    private const double RollupBudgetMs = 500;

    /// <summary>Обсяг, на якому гейт має сенс.</summary>
    /// <remarks>
    /// ⚠ 108 млн — це `02a-db-schema.md` («ОСНОВНИЙ ОБСЯГ: ~108 млн рядків на
    /// рік»), `07-data-model.md` («16 млн × ~7 ≈ 108 млн») і директива №06.
    /// ⛔ `tz/08-nfr.md` §8.4 каже інше — 21.6 млн на рік і 108 млн **за
    /// п'ять років**. Розбіжність не зведена і зводиться не тут; узято більше
    /// число, бо воно й у решті дерева, і в дорученні на цей замір.
    /// </remarks>
    private const double TargetRows = 108_000_000;

    /// <summary>Скільки паралельних робітників обслуговують чергу заміру №6.</summary>
    /// <remarks>
    /// ⚠ Не <c>ProcessorCount</c>. Робітники тут не рахують, а **чекають на
    /// СУБД**: при 125 RPS і відгуку 600 мс одночасних запитів потрібно
    /// щонайменше 75. Пул за кількістю ядер обмежив би темп сам собою, і замір
    /// показав би не межу бази, а межу власного генератора навантаження.
    /// </remarks>
    private const int LoadWorkers = 128;

    /// <summary>Скільки секунд тримати навантаження в замірі №6.</summary>
    /// <remarks>
    /// ⚠ У повному гейті це 15 хвилин (900 с). Значення параметризоване, бо
    /// п'ятнадцятихвилинний прогін недоречний у CI; але **зменшене вікно не є
    /// проходженням гейта** — воно лише показує, що замір працює. Скорочене
    /// вікно потрапляє в результат окремим записом.
    /// </remarks>
    public int LoadSeconds { get; init; } = 900;

    /// <summary>Цільове навантаження в одну партицію.</summary>
    public int TargetRps { get; init; } = 125;

    /// <summary>Чи бити навантаженням заміру №6 у найважчий зріз замість типового.</summary>
    /// <remarks>
    /// ⚠ Типово — НІ, і це рішення треба назвати вголос, бо воно змінює
    /// результат на два порядки.
    ///
    /// `B02` §7 задає для критерію №6 темп (125 RPS у партицію) і не задає
    /// складу запиту; склад доводиться брати з `tz/08` §8.2, а там зріз
    /// визначений як «аркуш × період, **~5 000 комірок**». Зріз 500×60 — це
    /// 30 000 комірок, тобто окремий критерій №1 («найгірший одиночний
    /// випадок»), а не те, що роблять сто користувачів по 125 разів на
    /// секунду.
    ///
    /// ⛔ Різниця виміряна, не припущена: 125 RPS по 500×60 дають чергу з
    /// затримкою в сотні секунд просто за обсягом даних (125 × 30 000 =
    /// 3.75 млн комірок за секунду). Видати це за «модель не тримає» означало
    /// б забракувати нормалізовану схему за навантаження, якого в ТЗ немає.
    /// Прогін із <c>true</c> лишається доступним і його число варте звіту.
    /// </remarks>
    public bool LoadWorstSlice { get; init; }

    /// <summary>Частка записів у суміші заміру №6, %.</summary>
    /// <remarks>
    /// ⛔ Суміш, а не самі читання. Під RCSI (`06-rcsi.sql`) читання не бере
    /// блокувань узагалі — отже, на чисто читальному навантаженні критерій
    /// «без ескалації блокувань» виконується тотожно і не перевіряє нічого.
    /// Ескалація можлива лише від записів, тому вони мають бути в суміші.
    ///
    /// ⚠ Саме 20 % — це припущення, а не число з документів: BR-07 задає темп
    /// (125 RPS) і не задає складу. Один із п'яти запитів — збереження — це
    /// профіль «відкрив таблицю, поправив, зберіг».
    /// </remarks>
    public int WritePercent { get; init; } = 20;

    /// <summary>Виконує всі заміри.</summary>
    /// <param name="connectionString">Рядок підключення до наповненої бази.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Виміряні числа, порушення бюджету і неперевірене.</returns>
    public async Task<GateResult> RunAsync(string connectionString, CancellationToken ct)
    {
        var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
        var failures = new List<string>();
        var notes = new List<string>();

        await using var db = CreateContext(connectionString);
        var store = new NormalizedCellStore(db, new BulkCellLoader(connectionString, 10_000));

        // Обсяг фіксується ПЕРШИМ і потрапляє в звіт: замір на недоборі — це
        // інший замір, і читач звіту має бачити, на чому саме він зроблений.
        measurements["cellvalue_rows"] = await ScalarAsync(connectionString, RowCountSql, ct)
            .ConfigureAwait(false);
        measurements["cellvalue_mb"] = await ScalarAsync(connectionString, SizeMbSql, ct)
            .ConfigureAwait(false);

        // ⚠ Число не декоративне. `ReadSliceAsync` приймає лише
        // `tableInstanceId` без `PeriodKey`, тому план починається зі
        // СКАНУВАННЯ `UQ_TableInstance` по всіх партиціях: вартість одного
        // читання зрізу росте разом із цією таблицею, а не лишається сталою.
        // Без цього числа в звіті деградація виглядала б безпричинною.
        measurements["tableinstance_rows"] = await ScalarAsync(connectionString, InstanceCountSql, ct)
            .ConfigureAwait(false);

        // ⛔ Недобір обсягу — це нотатка в КОЖНОМУ прогоні, а не мовчання.
        // «Гейт пройдено» на п'яти мільйонах рядків і «гейт пройдено» на ста
        // восьми мільйонах — різні твердження, і в звіті вони мають виглядати
        // по-різному, інакше друге тихо привласнить довіру першого.
        if (measurements["cellvalue_rows"] < TargetRows)
        {
            notes.Add(Fmt(
                $"Виміряно на {measurements["cellvalue_rows"]:F0} рядках doc.CellValue замість {TargetRows} — це НЕ обсяг BR-07. Числа переносяться на повний обсяг лише як екстраполяція."));
        }

        var target = await FindSliceAsync(connectionString, db, worst: true, ct).ConfigureAwait(false);
        if (target is null)
        {
            return new GateResult(
                false, measurements, ["У базі немає даних: спершу запустіть генератор."], notes);
        }

        var loadTarget = LoadWorstSlice
            ? target
            : await FindSliceAsync(connectionString, db, worst: false, ct).ConfigureAwait(false) ?? target;

        measurements["slice_rows"] = target.RowIds.Count;
        measurements["slice_columns"] = target.ColumnIds.Count;
        measurements["slice_period_key"] = target.PeriodKey;
        measurements["load_slice_cells"] = loadTarget.RowIds.Count * (double)loadTarget.ColumnIds.Count;

        // ⛔ Зріз, менший за 500×60, робить критерій №1 іншим критерієм.
        // Це не «майже той самий замір»: 30×33 у тридцять разів легше, і
        // бюджет 600 мс на ньому проходить будь-яка модель.
        if (target.RowIds.Count < 500 || target.ColumnIds.Count < 60)
        {
            notes.Add(Fmt(
                $"Найбільший зріз у базі — {target.RowIds.Count}×{target.ColumnIds.Count}, а критерій №1 записаний на 500×60. Замір легший за бюджет."));
        }

        // 1) Читання зрізу.
        var slice = await MeasureAsync(20, () => store.ReadSliceAsync(target.TableInstanceId, ct))
            .ConfigureAwait(false);
        measurements["read_slice_p95_ms"] = slice;

        // 2) Запис 100 комірок.
        var apply = await MeasureAsync(20, () => ApplyOnceAsync(store, target, 0, ct))
            .ConfigureAwait(false);
        measurements["apply_100_p95_ms"] = apply;

        // 3) Агрегація по періоду. ⛔ Індексована в'юха неприпустима (ER-N-02):
        //    вона переносить вартість на кожен запис, а пік у нас саме на записі.
        var rollup = await MeasureAsync(
                10, () => ScalarAsync(connectionString, RollupSql(target.PeriodKey), ct))
            .ConfigureAwait(false);
        measurements["rollup_p95_ms"] = rollup;

        // 6) ⚠ ГОЛОВНИЙ замір: усе навантаження в ОДНУ партицію.
        var load = await LoadOnePartitionAsync(connectionString, loadTarget, ct).ConfigureAwait(false);
        measurements["one_partition_rps"] = load.Rps;
        measurements["one_partition_read_p95_ms"] = load.ReadP95;
        measurements["one_partition_write_p95_ms"] = load.WriteP95;
        measurements["one_partition_read_service_p95_ms"] = load.ReadServiceP95;
        measurements["one_partition_write_service_p95_ms"] = load.WriteServiceP95;
        measurements["one_partition_seconds"] = LoadSeconds;
        measurements["one_partition_unserved"] = load.Scheduled - load.Completed;
        measurements["lock_escalations"] = load.LockEscalations;

        // ⚠ Допуск 2 % на темпі: він задається таймером, і останні запити
        // вікна просто не встигають дійти до черги. Вимагати рівно 125.0
        // означало б отримувати червоне від похибки планувальника, а не від
        // бази.
        Evaluate(
            [
                new Criterion("read_slice_p95_ms", slice, SliceBudgetMs, Worse.Higher,
                    Fmt($"ReadSliceAsync 500×60 p95 {slice:F0} мс")),
                new Criterion("apply_100_p95_ms", apply, ApplyBudgetMs, Worse.Higher,
                    Fmt($"ApplyAsync(100) p95 {apply:F0} мс")),
                new Criterion("rollup_p95_ms", rollup, RollupBudgetMs, Worse.Higher,
                    Fmt($"Агрегація періоду p95 {rollup:F0} мс")),
                new Criterion("one_partition_rps", load.Rps, TargetRps * 0.98, Worse.Lower,
                    Fmt($"В одну партицію досягнуто {load.Rps:F1} RPS")),
                new Criterion("one_partition_unserved", load.Scheduled - load.Completed, 0, Worse.Higher,
                    Fmt($"Не обслужено {load.Scheduled - load.Completed} із {load.Scheduled} запитів вікна")),
                new Criterion("one_partition_read_p95_ms", load.ReadP95, SliceBudgetMs, Worse.Higher,
                    Fmt($"Під навантаженням читання p95 {load.ReadP95:F0} мс")),
                new Criterion("one_partition_write_p95_ms", load.WriteP95, ApplyBudgetMs, Worse.Higher,
                    Fmt($"Під навантаженням запис p95 {load.WriteP95:F0} мс")),
                new Criterion("lock_escalations", load.LockEscalations, 0, Worse.Higher,
                    Fmt($"Ескалацій блокувань за вікно: {load.LockEscalations:F0}")),
            ],
            failures,
            notes);

        if (LoadSeconds < 900)
        {
            notes.Add(Fmt(
                $"Замір №6 тривав {LoadSeconds} с замість 900 (15 хв). Скорочене вікно не ловить накопичувальні ефекти: ріст version store у tempdb і розростання журналу. Це НЕ повний гейт."));
        }

        // 4) Повний цикл архівації року в цьому замірі не виконується: він
        //    потребує заповненого архівного року і вікна обслуговування.
        //    ⛔ Записується як неперевірене, а не як провал: інакше гейт
        //    червоний завжди, а гейт, червоний завжди, читають так само, як
        //    зелений завжди — не читають узагалі.
        notes.Add("Замір №4 (повний цикл архівації року) не виконувався: "
            + "потрібен заповнений архівний рік і вікно обслуговування (Q-063).");

        return new GateResult(failures.Count == 0, measurements, failures, notes);
    }

    /// <summary>З якого боку від межі лежить погіршення.</summary>
    private enum Worse
    {
        /// <summary>Більше — гірше (затримки, ескалації).</summary>
        Higher,

        /// <summary>Менше — гірше (темп).</summary>
        Lower,
    }

    /// <summary>Один критерій гейта: виміряне проти межі.</summary>
    /// <param name="Key">Ім'я заміру — воно ж ключ у переліку визнаних порушень.</param>
    /// <param name="Measured">Виміряне значення.</param>
    /// <param name="Budget">Межа з документів.</param>
    /// <param name="Direction">З якого боку від межі порушення.</param>
    /// <param name="Text">Готовий рядок для звіту.</param>
    private sealed record Criterion(string Key, double Measured, double Budget, Worse Direction, string Text);

    /// <summary>
    /// Визнані порушення: межа, поставлена на ВИМІРЯНОМУ значенні.
    /// </summary>
    /// <remarks>
    /// ⛔ Це не звільнення. Кожен рядок тут — визнане порушення бюджету
    /// `BR-07` зі СВОЄЮ стелею: замір не має права погіршитися ані на крок, а
    /// прибрати рядок можна лише повернувши замір у бюджет із документів.
    ///
    /// ⚠ Звільнення виглядало б інакше — «цей замір не перевіряємо», — і саме
    /// воно гірше за відсутню перевірку: порушення зникло б з очей, лишившись
    /// у системі. Тут воно щоразу друкується і щоразу назване порушенням.
    /// Зразок узятий із `src/Ecr.Web/scripts/check-bundle-budget.mjs`.
    ///
    /// ⛔ Порожньо — і має лишатися порожнім, доки людина не ухвалить, що
    /// саме визнається. Заповнювати цей перелік «щоб гейт позеленів» —
    /// протилежність його призначенню: `BR-07` існує, щоб ухвалити `D-21`
    /// (вибірковий перехід таблиць на `StorageMode = Hybrid`), а не щоб
    /// показати зелене.
    /// </remarks>
    private static readonly Dictionary<string, Acknowledged> Grandfathered =
        new(StringComparer.Ordinal);

    /// <summary>Визнане порушення зі своєю стелею, датою і строком.</summary>
    /// <param name="Limit">Стеля — виміряне значення в мить визнання.</param>
    /// <param name="Since">Дата визнання.</param>
    /// <param name="Until">Строк, до якого має бути прибране.</param>
    /// <param name="Why">Причина: що саме виміряно і чим лікується.</param>
    private sealed record Acknowledged(double Limit, string Since, string Until, string Why);

    /// <summary>Звіряє критерії з бюджетом і переліком визнаних порушень.</summary>
    /// <param name="criteria">Виміряні критерії.</param>
    /// <param name="failures">Сюди лягають порушення — вони дають код виходу 2.</param>
    /// <param name="notes">Сюди лягають визнані порушення — вони друкуються, коду не дають.</param>
    private static void Evaluate(
        IReadOnlyList<Criterion> criteria, List<string> failures, List<string> notes)
    {
        foreach (var criterion in criteria)
        {
            var held = Grandfathered.GetValueOrDefault(criterion.Key);

            if (held is null)
            {
                if (IsBroken(criterion, criterion.Budget))
                {
                    failures.Add(Fmt($"{criterion.Text} проти межі {criterion.Budget:F1}"));
                }

                continue;
            }

            // ⛔ Визнане порушення друкується ЗАВЖДИ, навіть коли воно у своїй
            // стелі. Мовчати про нього означало б, що через рік ніхто не
            // згадає, що бюджет із документів не виконується.
            notes.Add(Fmt(
                $"{criterion.Text}: бюджет {criterion.Budget:F1} не виконується, стеля {held.Limit:F1} з {held.Since}, строк: {held.Until}. {held.Why}"));

            if (IsBroken(criterion, held.Limit))
            {
                failures.Add(Fmt(
                    $"{criterion.Key}: {criterion.Measured:F1} вийшло за ВЛАСНУ стелю {held.Limit:F1} (визнано {held.Since})"));
            }
        }
    }

    private static bool IsBroken(Criterion criterion, double limit)
        => criterion.Direction == Worse.Higher
            ? criterion.Measured > limit
            : criterion.Measured < limit;

    private static async Task<double> MeasureAsync(int iterations, Func<Task> action)
    {
        var samples = new List<double>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            await action().ConfigureAwait(false);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        return Percentile95(samples);
    }

    private static double Percentile95(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        samples.Sort();
        return samples[(int)Math.Floor(0.95 * (samples.Count - 1))];
    }

    /// <summary>Пише 100 комірок — рівно стільки, скільки названо в бюджеті.</summary>
    /// <remarks>
    /// ⚠ Зміщення <paramref name="offset"/> розводить паралельних робітників
    /// заміру №6 по РІЗНИХ рядках однієї таблиці. Без нього сто потоків
    /// правили б ті самі сто комірок, і замір показував би чергу на одному
    /// ключі — тобто конкуренцію генератора з собою, а не роботу моделі.
    /// </remarks>
    private static Task ApplyOnceAsync(
        NormalizedCellStore store, SliceTarget target, int offset, CancellationToken ct)
    {
        const int Cells = 100;
        const int Columns = 10;
        const int Rows = Cells / Columns;

        var firstRow = target.RowIds.Count <= Rows ? 0 : offset % (target.RowIds.Count - Rows);
        var rowIds = target.RowIds.Skip(firstRow).Take(Rows).ToList();
        var columnIds = target.ColumnIds.Take(Columns).ToList();

        var records = rowIds
            .SelectMany(rowId => columnIds.Select(columnId => new CellRecord(
                new CellAddress(new PeriodKey(target.PeriodKey), rowId, columnId),
                target.TableDefId,
                new CellValueData { ValueNumeric = 1m })))
            .ToList();

        return store.ApplyAsync(
            new CellChangeSet(target.TableInstanceId, records, [], rowIds, 1, false), ct);
    }

    /// <remarks>
    /// ⚠ Через <c>sys.dm_db_partition_stats</c>, а не <c>COUNT(*)</c>:
    /// перелік рядків на 108 млн — це повне сканування кластерного індексу,
    /// тобто хвилини очікування перед першим же заміром.
    /// </remarks>
    private const string RowCountSql = """
        SELECT CAST(SUM(p.row_count) AS float)
        FROM sys.dm_db_partition_stats AS p
        JOIN sys.tables AS t ON t.object_id = p.object_id
        WHERE t.name = 'CellValue' AND SCHEMA_NAME(t.schema_id) = 'doc' AND p.index_id IN (0, 1)
        """;

    private const string InstanceCountSql = """
        SELECT CAST(SUM(p.row_count) AS float)
        FROM sys.dm_db_partition_stats AS p
        JOIN sys.tables AS t ON t.object_id = p.object_id
        WHERE t.name = 'TableInstance' AND SCHEMA_NAME(t.schema_id) = 'doc' AND p.index_id IN (0, 1)
        """;

    private const string SizeMbSql = """
        SELECT CAST(SUM(a.used_pages) * 8.0 / 1024 AS float)
        FROM sys.tables t
        JOIN sys.indexes i     ON i.object_id = t.object_id
        JOIN sys.partitions p  ON p.object_id = i.object_id AND p.index_id = i.index_id
        JOIN sys.allocation_units a ON a.container_id = p.partition_id
        WHERE t.name = 'CellValue' AND SCHEMA_NAME(t.schema_id) = 'doc'
        """;

    /// <summary>
    /// Агрегація по періоду — сума всієї партиції.
    /// </summary>
    /// <remarks>
    /// ⚠ Це НЕ той rollup, який описує B02 §7 («над уже матеріалізованими
    /// значеннями, <c>IsCalculated = 1</c>»): у синтетичному обсязі
    /// матеріалізованих значень немає жодного, і фільтр по
    /// <c>IsCalculated</c> повернув би порожньо. Тому міряється найгірший
    /// випадок — повна партиція. Якщо він у бюджет не вкладається, ліки саме
    /// ті, що названі в B02: покриваючий індекс над матеріалізованим rollup.
    /// </remarks>
    private static string RollupSql(int periodKey) => Fmt(
        $"SELECT CAST(ISNULL(SUM(ValueNumeric), 0) AS float) FROM doc.CellValue WHERE PeriodKey = {periodKey}");

    private static async Task<double> ScalarAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;

        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Навантаження в одну партицію: саме так виглядає пік у проді.
    /// </summary>
    /// <param name="connectionString">Рядок підключення.</param>
    /// <param name="target">Зріз, у який б'є все навантаження.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Досягнутий темп, p95 читання і запису, ескалації блокувань.</returns>
    /// <remarks>
    /// ⛔ Модель навантаження — **відкрита**: запити ставляться в чергу за
    /// розкладом 1/125 с незалежно від того, впоралася база з попередніми чи
    /// ні, а затримка міряється від ЗАПЛАНОВАНОГО часу, а не від початку
    /// виконання. Закрита модель («сто потоків шлють запит за запитом»)
    /// вимірює пропускну здатність і НІКОЛИ не показує зриву: коли база
    /// сповільнюється, потоки просто шлють рідше, RPS падає, а p95 лишається
    /// гарним. Саме так виглядав цей замір до 2026-09-06.
    /// </remarks>
    private async Task<LoadResult> LoadOnePartitionAsync(
        string connectionString, SliceTarget target, CancellationToken ct)
    {
        // ⚠ Пул з'єднань явно ширший за кількість робітників: типові 100
        // означали б, що при 128 робітниках частина запитів чекає на пул, і
        // замір показав би межу ADO.NET замість межі СУБД.
        var loadConnection = new SqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = LoadWorkers * 2,
        }.ConnectionString;

        var escalationsBefore = await ScalarAsync(connectionString, EscalationSql, ct).ConfigureAwait(false);

        var reads = new ConcurrentBag<Sample>();
        var writes = new ConcurrentBag<Sample>();
        var channel = Channel.CreateUnbounded<ScheduledCall>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

        // ⛔ Догін обмежений у часі. Якщо база не тримає темп, черга росте, і
        // «дочекатися всіх» означало б прогін на години замість вікна: на
        // 20 RPS проти цільових 125 п'ятнадцятихвилинне вікно розсмоктувалося б
        // півтори години. Недообслужені запити — це РЕЗУЛЬТАТ, і вони йдуть
        // у звіт окремим числом, а не в очікування.
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, LoadWorkers)
            .Select(index => Task.Run(
                () => ConsumeAsync(loadConnection, target, channel.Reader, reads, writes, index, drain.Token),
                CancellationToken.None))
            .ToArray();

        var total = await ProduceAsync(channel.Writer, ct).ConfigureAwait(false);

        // ⚠ Порівнюється САМЕ ЗАВДАННЯ, а не його тип. `Task.WhenAny` віддає
        // переможця як `Task`, тому перевірка «це не Task<Task>» істинна
        // завжди — і догін обривався щоразу, навіть коли робітники встигали.
        var all = Task.WhenAll(workers);
        var grace = TimeSpan.FromSeconds(Math.Min(LoadSeconds, 120));
        var winner = await Task.WhenAny(all, Task.Delay(grace, ct)).ConfigureAwait(false);
        if (winner != all)
        {
            await drain.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Скасування догону — очікуваний кінець, а не збій.
        }

        var escalationsAfter = await ScalarAsync(connectionString, EscalationSql, ct).ConfigureAwait(false);

        var readSamples = reads.ToArray();
        var writeSamples = writes.ToArray();
        var completed = readSamples.Length + writeSamples.Length;

        // ⛔ Темп рахується від ФАКТИЧНОГО часу до останнього завершення, а не
        // від номінального вікна. Ділення на номінал давало рівно 125.0 навіть
        // тоді, коли черга розсмоктувалася втричі довше за вікно, — тобто
        // показувало ціль замість заміру.
        var elapsed = completed == 0
            ? clock.Elapsed.TotalSeconds
            : Math.Max(
                LoadSeconds,
                readSamples.Concat(writeSamples).Max(s => s.FinishedAt.TotalSeconds));

        return new LoadResult(
            Rps: completed / elapsed,
            ReadP95: Percentile95([.. readSamples.Select(s => s.LatencyMs)]),
            WriteP95: Percentile95([.. writeSamples.Select(s => s.LatencyMs)]),
            ReadServiceP95: Percentile95([.. readSamples.Select(s => s.ServiceMs)]),
            WriteServiceP95: Percentile95([.. writeSamples.Select(s => s.ServiceMs)]),
            LockEscalations: escalationsAfter - escalationsBefore,
            Scheduled: total,
            Completed: completed);
    }

    /// <summary>Ставить запити в чергу рівно за розкладом і закриває її.</summary>
    private async Task<int> ProduceAsync(ChannelWriter<ScheduledCall> writer, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1.0 / TargetRps);
        var window = TimeSpan.FromSeconds(LoadSeconds);
        var index = 0;

        while (!ct.IsCancellationRequested)
        {
            var due = interval * index;
            if (due >= window)
            {
                break;
            }

            var wait = due - started.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            // Кожен п'ятий — запис. Детермінований шаблон, а не випадковий:
            // два прогони мають відрізнятися станом бази, не жеребом.
            var isWrite = WritePercent > 0 && index % (100 / WritePercent) == 0;
            await writer.WriteAsync(new ScheduledCall(due, isWrite, index, started), ct).ConfigureAwait(false);
            index++;
        }

        writer.Complete();
        return index;
    }

    /// <summary>Обслуговує чергу; затримка рахується від запланованого часу.</summary>
    /// <remarks>
    /// ⚠ Записуються ДВА числа. <c>LatencyMs</c> — від моменту, коли запит мав
    /// бути поданий (те, що бачить користувач: очікування в черзі включно).
    /// <c>ServiceMs</c> — сам час виконання. Різниця між ними і є відповіддю
    /// на питання «база повільна» проти «база не встигає»: при першому обидва
    /// числа великі, при другому велике лише перше.
    /// </remarks>
    private static async Task ConsumeAsync(
        string connectionString,
        SliceTarget target,
        ChannelReader<ScheduledCall> reader,
        ConcurrentBag<Sample> reads,
        ConcurrentBag<Sample> writes,
        int workerIndex,
        CancellationToken ct)
    {
        await using var db = CreateContext(connectionString);
        var store = new NormalizedCellStore(db, new BulkCellLoader(connectionString, 1000));

        try
        {
            await foreach (var call in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var startedAt = call.Clock.Elapsed;
                try
                {
                    if (call.IsWrite)
                    {
                        await ApplyOnceAsync(store, target, call.Index + workerIndex, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await store.ReadSliceAsync(target.TableInstanceId, ct).ConfigureAwait(false);
                    }
                }
                catch (SqlException)
                {
                    // ⚠ Взаємне блокування під навантаженням — це РЕЗУЛЬТАТ
                    // заміру, а не збій прогону. Запит, що не дійшов, не
                    // потрапляє у вибірку затримок, і втрата видно в
                    // «не обслужено».
                    continue;
                }
                catch (InvalidOperationException) when (ct.IsCancellationRequested)
                {
                    // ⛔ Саме `InvalidOperationException`, а не
                    // `OperationCanceledException`: `Microsoft.Data.SqlClient`
                    // на скасуванні вже відкритої команди кидає
                    // «Operation cancelled by user» саме цим типом. Без цього
                    // рядка обрив догону валив увесь прогін необробленим
                    // винятком — після повного вікна навантаження, тобто
                    // втрачаючи всі зняті числа.
                    break;
                }

                var finishedAt = call.Clock.Elapsed;
                (call.IsWrite ? writes : reads).Add(new Sample(
                    LatencyMs: Math.Max(0, (finishedAt - call.Due).TotalMilliseconds),
                    ServiceMs: (finishedAt - startedAt).TotalMilliseconds,
                    FinishedAt: finishedAt));
            }
        }
        catch (OperationCanceledException)
        {
            // Догін обірвано за часом — очікуваний кінець вікна.
        }
    }

    /// <summary>Лічильник ескалацій блокувань — накопичувальний від старту інстансу.</summary>
    /// <remarks>
    /// ⚠ Лічильник на весь інстанс, а не на базу: іншого SQL Server не дає.
    /// Тому замір має сенс лише тоді, коли на інстансі більше нічого не
    /// працює — і про це сказано в скрипті, який його запускає.
    /// </remarks>
    private const string EscalationSql = """
        SELECT CAST(ISNULL(SUM(cntr_value), 0) AS float)
        FROM sys.dm_os_performance_counters
        WHERE counter_name LIKE 'Table Lock Escalations%'
        """;

    /// <summary>Скільки комірок має «типовий» зріз за <c>tz/08</c> §8.2.</summary>
    private const int TypicalSliceCells = 5000;

    /// <summary>
    /// Зріз для заміру: найважчий або типовий.
    /// </summary>
    /// <param name="connectionString">Рядок підключення.</param>
    /// <param name="db">Контекст для добору переліків рядків і колонок.</param>
    /// <param name="worst">
    /// <c>true</c> — найважчий зріз (критерій №1, 500×60);
    /// <c>false</c> — найближчий до 5 000 комірок (профіль §8.2).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Зріз або <c>null</c>, якщо в базі немає даних.</returns>
    /// <remarks>
    /// ⛔ «Найважчий» рахується як рядки × колонки. Попередня версія брала
    /// <c>GroupBy(...).FirstOrDefault()</c> — тобто ДОВІЛЬНУ таблицю, яку
    /// віддасть план: на профілі генератора це ~30×33 замість 500×60, у
    /// тридцять разів менше за зріз, під який записаний бюджет.
    ///
    /// ⚠ Обидва зрізи беруться з ОДНІЄЇ партиції — найпізнішої. Критерій №6
    /// весь сенс має саме в тому, що навантаження не розподілене по періодах.
    /// </remarks>
    private static async Task<SliceTarget?> FindSliceAsync(
        string connectionString, EcrDbContext db, bool worst, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = worst
            ? SliceSql("rc.Rows * cc.Cols DESC")
            : SliceSql(Fmt($"ABS(rc.Rows * cc.Cols - {TypicalSliceCells}) ASC"));

        int periodKey;
        long instanceId;
        int tableDefId;

        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            periodKey = reader.GetInt32(0);
            instanceId = reader.GetInt64(1);
            tableDefId = reader.GetInt32(2);
        }

        var rowIds = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey && r.TableInstanceId == instanceId)
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var columnIds = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == tableDefId)
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        return new SliceTarget(instanceId, periodKey, tableDefId, rowIds, columnIds);
    }

    /// <summary>Добір зрізу в найпізнішій партиції за заданим упорядкуванням.</summary>
    /// <param name="order">Вираз <c>ORDER BY</c> — чим «кращий» зріз для цього заміру.</param>
    /// <returns>Текст запиту.</returns>
    private static string SliceSql(string order) => $"""
        WITH peak AS (SELECT MAX(PeriodKey) AS PeriodKey FROM doc.TableInstance)
        SELECT TOP (1) ti.PeriodKey, ti.Id, ti.TableDefId
        FROM (SELECT PeriodKey, TableInstanceId, COUNT_BIG(*) AS Rows
              FROM doc.TableRow
              WHERE PeriodKey = (SELECT PeriodKey FROM peak)
              GROUP BY PeriodKey, TableInstanceId) AS rc
        JOIN doc.TableInstance AS ti
          ON ti.PeriodKey = rc.PeriodKey AND ti.Id = rc.TableInstanceId
        JOIN (SELECT TableDefId, COUNT_BIG(*) AS Cols
              FROM cfg.ColumnDef
              GROUP BY TableDefId) AS cc ON cc.TableDefId = ti.TableDefId
        ORDER BY {order}, ti.Id;
        """;

    private static EcrDbContext CreateContext(string connectionString)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connectionString, o => o.CommandTimeout(600))
            .Options);

    private static string Fmt(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private sealed record SliceTarget(
        long TableInstanceId, int PeriodKey, int TableDefId,
        IReadOnlyList<long> RowIds, IReadOnlyList<int> ColumnIds);

    private sealed record ScheduledCall(TimeSpan Due, bool IsWrite, int Index, Stopwatch Clock);

    private sealed record Sample(double LatencyMs, double ServiceMs, TimeSpan FinishedAt);

    private sealed record LoadResult(
        double Rps,
        double ReadP95,
        double WriteP95,
        double ReadServiceP95,
        double WriteServiceP95,
        double LockEscalations,
        int Scheduled,
        int Completed);
}

/// <summary>Результат гейта.</summary>
/// <param name="Passed">Чи витримані всі ВИМІРЯНІ бюджети.</param>
/// <param name="Measurements">Виміряні числа.</param>
/// <param name="Failures">Порушені бюджети — кожне дає ненульовий код виходу.</param>
/// <param name="Notes">Неперевірене і застереження — друкуються завжди, коду виходу не дають.</param>
/// <remarks>
/// ⛔ <paramref name="Failures"/> і <paramref name="Notes"/> розділені
/// навмисно. Поки неперевірений замір №4 лежав серед порушень, гейт був
/// червоний ЗАВЖДИ — а завжди червоний гейт перестають читати так само
/// швидко, як завжди зелений.
/// </remarks>
public sealed record GateResult(
    bool Passed,
    IReadOnlyDictionary<string, double> Measurements,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Notes);
