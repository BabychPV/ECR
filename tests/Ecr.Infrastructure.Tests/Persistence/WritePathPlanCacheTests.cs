using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>WR-01</c>: форма запиту на шляху запису не залежить від ДАНИХ.
/// </summary>
/// <remarks>
/// ⛔ Це не тест «код виглядає правильно», а той самий замір, що в
/// <c>docs/build/MS-01-BASELINE.md</c> §3.2, тільки автоматичний і на тестовій
/// базі. Лінійка виміряла на продукті: 20 <c>PATCH</c> із різним масштабом
/// числа → <b>21 план</b> <c>MERGE doc.CellValue</c> у кеші. Причина —
/// <c>SqlClient</c> виводить <c>Precision</c>/<c>Scale</c> зі значення, тож
/// <c>1m</c> і <c>1.25m</c> дають РІЗНІ сигнатури запиту, а з ними й різні
/// ключі кешу планів.
///
/// ⚠ Чому замір, а не сторож по тексту джерела. Сторож «у файлі немає
/// <c>AddWithValue</c>» лишився б зеленим на параметрі, оголошеному з
/// неправильною довжиною, і на новому запиті, доданому поруч. Тут рахується
/// те, що СУБД справді поклала в кеш — тобто наслідок, а не написання.
///
/// ⚠ Старий режим гейта (<c>--store-bench</c>) цієї вади побачити не міг: він
/// писав сталий <c>1m</c>, тобто рівно одну сигнатуру, скільки його не
/// повторюй (<c>MS-01</c> §3.2, рядок «2 плани»). Саме тому перевірка мусить
/// перебирати ЗНАЧЕННЯ, а не повторювати одне.
/// </remarks>
[Collection("SqlServer")]
public sealed class WritePathPlanCacheTests(SqlServerFixture sql)
{
    /// <summary>Скільки записів робить кожен замір.</summary>
    /// <remarks>
    /// Двадцять — не кругле число для краси, а рівно стільки, скільки зробила
    /// лінійка в §3.2, щоб число «21 план» було з чим зіставляти.
    /// </remarks>
    private const int Writes = 20;

    /// <summary>
    /// Стеля планів у кеші. Один на форму запиту; другий — запас на випадковий
    /// збіг тексту з чужим запитом у тій самій базі.
    /// </summary>
    /// <remarks>
    /// ⚠ Стеля навмисно не «рівно 1»: усі записи заміру мають ОДНАКОВУ
    /// кількість комірок, тож форма одна, але тестова база спільна на збірку.
    /// Різниця між «≤ 2» і «21» на порядок більша за цей запас, тож
    /// послаблення нічого не приховує — воно лише не робить тест залежним від
    /// сусідів.
    /// </remarks>
    private const int MaxPlans = 2;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Двадцять_записів_із_різними_числами_дають_один_план_MERGE()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 4, ct: CancellationToken.None);
        var ct = CancellationToken.None;

        // Розігрів ПЕРЕД очищенням кешу: перший виклик тягне за собою
        // підключення, метадані й перший план. Без нього замір рахував би ще й
        // одноразову вартість старту, і «на один план більше» читалося б як
        // дефект.
        var store = new NormalizedCellStore(builder.CreateContext());
        await store.ApplyAsync(Change(doc, 0), ct);

        await ClearPlanCacheAsync(ct);

        for (var i = 0; i < Writes; i++)
        {
            await store.ApplyAsync(Change(doc, i + 1), ct);
        }

        var plans = await PlanCountAsync("%MERGE doc.CellValue%", ct);

        AssertPlans(plans, "MERGE doc.CellValue", await PlanFilterWorksAsync(ct));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Записи_що_різняться_ЛИШЕ_числом_дають_один_план_MERGE()
    {
        // ⚠ Ізолюючий тест. Попередній міняє одразу три типи, тож його падіння
        // не каже, ЯКИЙ параметр лишився нетипізованим. Цей міняє лише
        // decimal — і падає рівно тоді, коли справа в ньому.
        var plans = await MeasureAsync(
            (doc, seed) => new CellValueData { ValueNumeric = Numeric(seed) }, CancellationToken.None);

        AssertPlans(
            plans,
            "MERGE doc.CellValue (лише число)",
            await PlanFilterWorksAsync(CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Записи_що_різняться_ЛИШЕ_датою_дають_один_план_MERGE()
    {
        var plans = await MeasureAsync(
            (doc, seed) => new CellValueData
            {
                ValueDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(seed * 12345),
            },
            CancellationToken.None);

        AssertPlans(
            plans,
            "MERGE doc.CellValue (лише дата)",
            await PlanFilterWorksAsync(CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Двадцять_записів_аудиту_з_різними_рядками_дають_один_план_INSERT()
    {
        // ⛔ Друга половина WR-01, і вона важить не менше за першу: на шляху
        // PATCH аудит пишеться тим самим батчем, що й значення, а
        // AddWithValue для рядка оголошував параметр завдовжки як САМЕ
        // ЗНАЧЕННЯ. RowKey "R1" і "R123456" давали різні сигнатури, і на
        // трьох рядкових стовпцях це добуток довжин — тобто майже унікальний
        // план на кожен PATCH.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 4, ct: CancellationToken.None);
        var ct = CancellationToken.None;

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);

        await writer.WriteCellChangesAsync([AuditChange(doc, 0)], ct);

        await ClearPlanCacheAsync(ct);

        for (var i = 0; i < Writes; i++)
        {
            await writer.WriteCellChangesAsync([AuditChange(doc, i + 1)], ct);
        }

        var plans = await PlanCountAsync("%INSERT INTO aud.CellChange%", ct);

        AssertPlans(plans, "INSERT aud.CellChange", await PlanFilterWorksAsync(ct));
    }

    /// <summary>Стеля І підлога числа планів.</summary>
    /// <param name="plans">Виміряне число.</param>
    /// <param name="what">Що саме рахували — для тексту відмови.</param>
    /// <param name="filterWorks">
    /// Чи підтвердив контрольний зонд, що фільтр узагалі щось знаходить.
    /// Відрізняє «замір зламаний» від «план витіснено чужим навантаженням».
    /// </param>
    /// <remarks>
    /// ⛔ Підлога важить не менше за стелю. Перша редакція цього тесту мала
    /// лише стелю, замір давав <b>0</b> через хибний фільтр по базі — і тест
    /// проходив на коді ДО <c>WR-01</c>. Той самий урок, що в
    /// <c>SqlClientCommandCounter.AssertObserved</c>: мовчазний нуль читається
    /// як «оптимізація вдалася» і є найгіршим із можливих результатів.
    /// </remarks>
    private static void AssertPlans(int plans, string what, bool filterWorks)
    {
        // ⛔ Нуль має ДВІ різні причини, і плутати їх не можна.
        //
        // (а) Фільтр по базі зламаний — тоді нуль буде завжди, і тест мовчки
        //     проходив би на коді до `WR-01`. Це те, проти чого підлога й
        //     заведена.
        // (б) План ВИТІСНЕНО з кешу чужим навантаженням між записом і
        //     заміром. Теоретично можливо (кеш загальносерверний), але НЕ
        //     спостерігалося: «плаваючі» падіння в повних прогонах мали іншу
        //     причину — Msg 924 від `dm_exec_sql_text` на чужій базі в
        //     SINGLE_USER/CREATE/DROP (виправлено в PlanCountAsync, 204689a9).
        //
        // Захист лишається: нуль без підтвердження зондом — зламаний замір,
        // нуль із підтвердженням — окрема, названа відмова.
        if (plans == 0)
        {
            Assert.True(
                filterWorks,
                $"Планів {what} у кеші: 0, і контрольний зонд теж нічого не "
                + "знайшов. Замір НЕ ВІДБУВСЯ — фільтр по базі в PlanCountAsync "
                + "зламаний, і нуль тут не є доказом.");

            Assert.Fail(
                $"Планів {what} у кеші: 0, хоча контрольний зонд фільтр "
                + "підтвердив. Можливо, план ВИТІСНЕНО з кешу чужим "
                + "навантаженням на цьому ж інстансі SQL Server. Повтори "
                + "прогін на вільній машині; якщо нуль лишається — це вже "
                + "дефект заміру, а не шум.");
        }

        Assert.True(
            plans <= MaxPlans,
            $"Планів {what} у кеші: {plans} на {Writes} записів. Сигнатура "
            + "запиту залежить від даних (Precision/Scale/довжина рядка) — "
            + "WR-01 не діє.");
    }

    /// <summary>
    /// Чи здатен <see cref="PlanCountAsync"/> узагалі щось знайти в ЦЕЙ момент.
    /// </summary>
    /// <remarks>
    /// ⚠ Зонд виконує власний, свідомо унікальний запит і одразу шукає його
    /// план. Якщо не знаходить — справа у фільтрі (база, `dbid`, форма тексту),
    /// а не в тому, що `WR-01` не працює. Унікальний коментар у тексті запиту
    /// гарантує, що ми знаходимо саме свій план, а не чийсь схожий.
    /// </remarks>
    private async Task<bool> PlanFilterWorksAsync(CancellationToken ct)
    {
        var marker = $"ecr-plan-probe-{Guid.NewGuid():N}";

        await ExecuteAsync($"SELECT 1 /* {marker} */;", ct);

        return await PlanCountAsync($"%{marker}%", ct) >= 1;
    }

    /// <summary>Двадцять записів однієї комірки і число планів після них.</summary>
    /// <param name="value">Що саме писати на кроці <c>seed</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<int> MeasureAsync(Func<TestDocument, int, CellValueData> value, CancellationToken ct)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowCount: 4, ct: ct);
        var store = new NormalizedCellStore(builder.CreateContext());
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);

        CellChangeSet Batch(int seed) => new(
            doc.TableInstanceId,
            [new CellRecord(address, doc.TableDefId, value(doc, seed))],
            [],
            [doc.RowIds[0]],
            ChangedByUserId: 1,
            IsLateEdit: false);

        await store.ApplyAsync(Batch(0), ct);
        await ClearPlanCacheAsync(ct);

        for (var i = 0; i < Writes; i++)
        {
            await store.ApplyAsync(Batch(i + 1), ct);
        }

        return await PlanCountAsync("%MERGE doc.CellValue%", ct);
    }

    /// <summary>Число з масштабом і величиною, що змінюються від кроку.</summary>
    private static decimal Numeric(int seed)
    {
        var scale = seed % 10;
        var raw = decimal.Parse(
            "1." + new string('1', scale + 1), CultureInfo.InvariantCulture) * (seed + 1);
        return decimal.Round(raw, scale);
    }

    /// <summary>Одна комірка зі значеннями, несхожими на попередні.</summary>
    /// <remarks>
    /// ⚠ Усі три «плаваючі» типи одразу: <c>decimal</c> різного масштабу,
    /// <c>datetime2</c> різної дробової частини і рядок різної довжини. Якщо
    /// хоч один із них лишиться нетипізованим, число планів виросте — тобто
    /// тест ловить кожен із трьох окремо, а не «щось одне з трьох».
    ///
    /// ⚠ Комірка ОДНА і та сама адреса: інакше змінювалася б довжина списку
    /// <c>VALUES</c>, тобто сам текст запиту, і замір міряв би розмір батчу
    /// замість типів параметрів (це вже <c>WR-02</c>, окремий рядок плану).
    /// </remarks>
    private static CellChangeSet Change(TestDocument doc, int seed)
    {
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);

        return new CellChangeSet(
            doc.TableInstanceId,
            [new CellRecord(address, doc.TableDefId, new CellValueData
            {
                // Масштаб 0…9 — рівно ті сигнатури decimal(p,s), які виводив
                // SqlClient зі значення.
                ValueNumeric = Numeric(seed),
                ValueDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(seed * 12345),
                ValueString = new string('я', (seed % 7) + 1),
            })],
            [],
            [doc.RowIds[0]],
            ChangedByUserId: 1,
            IsLateEdit: false);
    }

    /// <summary>Один запис аудиту з рядками, несхожими на попередні.</summary>
    private static CellChangeRecord AuditChange(TestDocument doc, int seed)
        => new(
            new DateTime(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc).AddTicks(seed * 12345),
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.DocumentId,
            RowKey: new string('R', (seed % 9) + 1),
            OldValue: new string('o', (seed % 11) + 1),
            NewValue: new string('n', (seed % 13) + 1),
            ChangedByUserId: 42,
            Origin: seed % 2 == 0 ? "UserEdit" : "Recalculation",
            IsLateEdit: false,
            CorrelationId: new string('c', (seed % 5) + 1));

    /// <summary>Очищає кеш планів ЦІЄЇ бази, не сервера.</summary>
    /// <remarks>
    /// ⛔ Саме <c>DATABASE SCOPED</c>, а не <c>DBCC FREEPROCCACHE</c>: на цій
    /// машині одночасно працюють кілька агентів зі своїми базами, і глобальне
    /// очищення зіпсувало б їхні заміри, а їхнє — цей.
    /// </remarks>
    private async Task ClearPlanCacheAsync(CancellationToken ct)
        => await ExecuteAsync("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", ct);

    /// <summary>Скільки планів із таким текстом лежить у кеші ЦІЄЇ бази.</summary>
    /// <remarks>
    /// ⚠ Запит СПІВПАДАЄ з тим, яким міряє лінійка
    /// (<c>HttpLoadBenchmark.MergePlansAsync</c>), навмисно: числа цього тесту
    /// й числа гейта продукту мають бути зіставні, а два різні способи рахувати
    /// те саме розійдуться до першої розбіжності.
    ///
    /// ⛔ Саме <c>sys.dm_exec_cached_plans</c>, а НЕ
    /// <c>sys.dm_exec_query_stats</c>. У «Доказ/замір» рядка <c>WR-01</c>
    /// (§3.3 директиви) запит записаний через <c>query_stats</c> +
    /// <c>t.dbid = DB_ID()</c>, і в такому вигляді він завжди дає <b>0</b>:
    /// <c>sys.dm_exec_sql_text(qs.sql_handle)</c> повертає <c>dbid = NULL</c>
    /// для підготовлених пакетів (<c>sp_executesql</c>), тобто для того, чим
    /// <c>Microsoft.Data.SqlClient</c> відправляє БУДЬ-ЯКУ параметризовану
    /// команду. Виміряно на живій базі: три різні сигнатури того самого тексту
    /// → 3 плани; через <c>cached_plans</c> фільтр по базі відбирає 2 з 3
    /// (третій — сам запит-лічильник), через <c>query_stats</c> — жодного.
    ///
    /// ⛔ Перша редакція цього тесту була через це ХИБНОЗЕЛЕНОЮ: «0 планів
    /// ≤ 2» проходить і на коді ДО <c>WR-01</c>. Тому в
    /// <see cref="AssertPlans"/> є не лише стеля, а й підлога.
    ///
    /// ⛔ Плани СВОЄЇ бази відбираються ДО виклику <c>dm_exec_sql_text</c>, у
    /// табличну змінну. Кеш планів загальносерверний, а
    /// <c>dm_exec_sql_text</c> для плану процедури/тригера відкриває базу, якій
    /// той належить. Коли чужий прогін на тому ж інстансі переводить свою базу
    /// в <c>SINGLE_USER</c> (прибирання <c>EcrTest_*</c>) або створює/видаляє
    /// її, запит падав із «Database 'EcrTest_Api_…' is already open and can
    /// only have one user» / «is in transition» — хоча до нашої бази не мав
    /// стосунку. Фільтр <c>t.dbid = DB_ID()</c> у тому самому запиті не рятує:
    /// порядок обчислення <c>CROSS APPLY</c> і <c>WHERE</c> не гарантований.
    /// </remarks>
    private async Task<int> PlanCountAsync(string pattern, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET NOCOUNT ON;
            DECLARE @own TABLE (plan_handle varbinary(64) PRIMARY KEY);
            INSERT @own
            SELECT cp.plan_handle
            FROM sys.dm_exec_cached_plans AS cp
            CROSS APPLY sys.dm_exec_plan_attributes(cp.plan_handle) AS a
            WHERE a.attribute = 'dbid' AND CONVERT(int, a.value) = DB_ID();
            SELECT COUNT(*)
            FROM @own AS o
            CROSS APPLY sys.dm_exec_sql_text(o.plan_handle) AS t
            WHERE t.text LIKE @pattern AND t.dbid = DB_ID();
            """;
        command.Parameters.Add("@pattern", System.Data.SqlDbType.NVarChar, 200).Value = pattern;
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task ExecuteAsync(string sqlText, CancellationToken ct)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync(ct);
    }
}
