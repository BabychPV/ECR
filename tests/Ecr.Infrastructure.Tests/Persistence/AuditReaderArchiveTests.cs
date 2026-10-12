using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// НФ-8.9.6 / D-247: журнал змін комірок читає й АРХІВ аудиту (<c>arc.AuditCellChange</c>), а не лише гарячу
/// <c>aud.CellChange</c>: архівні рядки з'являються у видачі з позначкою <c>FromArchive</c>, курсор проходить
/// межу «гаряче/архів» без втрат і дублів, межі читання (R-11) діють на архів так само, як на гаряче,
/// гарячий шлях не дорожчає.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ (кожен — окремо, у detached-worktree):
/// — прибрати з <c>AuditReader.CellChangesWithArchiveSql</c> гілку <c>arc.AuditCellChange</c> (UNION ALL) —
///   червоніють <see cref="Архівне_вікно_віддає_архівні_рядки_з_позначкою_і_іменами"/>,
///   <see cref="Курсор_проходить_межу_гаряче_архів_в_обидва_боки_без_дублів"/> і лічильники;
/// — прибрати з <c>BuildCellChangeWhere</c> умову <c>ColumnDefId IN (… OPENJSON(@visibleColumns))</c> —
///   червоніє <see cref="Межі_читання_діють_на_архівні_рядки_до_сторінки_і_курсора"/> (прихована колонка в
///   архіві віддається).
/// Дані архіву пишуться прямим <c>INSERT</c> у <c>arc.AuditCellChange</c> із унікальним документом тесту
/// (UPDATE/DELETE там заборонені тригером, а SWITCH спільної бази зачепив би чужі рядки) — справжній SWITCH
/// доведено окремим тестом на власній базі.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderArchiveTests(SqlServerFixture sql, ITestOutputHelper output)
{
    // Архівні місяці — далеко в минулому, гарячі — пізніше; усе в межах pf_AuditByMonth.
    private static readonly DateTime A1 = new(2019, 5, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime A2 = new(2019, 5, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime A3 = new(2019, 5, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime H1 = new(2020, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime H2 = new(2020, 11, 11, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime H3 = new(2020, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime ArchiveFrom = new(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ArchiveTo = new(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime HotFrom = new(2020, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime HotTo = new(2020, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Архівне_вікно_віддає_архівні_рядки_з_позначкою_і_іменами()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        await WriteHotAsync(db, doc, "R1", doc.ColumnDefIds[1], H1, "hot-1");
        await WriteArchiveAsync(doc, "R1", doc.ColumnDefIds[1], A1, "arc-1");
        await WriteArchiveAsync(doc, "R1", doc.ColumnDefIds[1], A2, "arc-2");

        var reader = new AuditReader(db);

        // Вікно в архівному місяці: лише архів, кожен рядок із позначкою й людськими іменами (ті самі LEFT JOIN).
        var archived = await reader.ReadCellChangesAsync(
            new CellChangeFilter(ArchiveFrom, ArchiveTo, DocumentId: doc.DocumentId), new CursorRequest(50), CancellationToken.None);
        Assert.Equal(["arc-1", "arc-2"], archived.Items.Select(c => c.NewValue));
        Assert.All(archived.Items, c =>
        {
            Assert.True(c.FromArchive);
            Assert.NotNull(c.ColumnCode);
            Assert.NotNull(c.DocumentBusinessKey);
            Assert.Equal(doc.DocumentId, c.DocumentId);
        });

        // Вікно в гарячому місяці: лише гаряче, без позначки.
        var hot = await reader.ReadCellChangesAsync(
            new CellChangeFilter(HotFrom, HotTo, DocumentId: doc.DocumentId), new CursorRequest(50), CancellationToken.None);
        Assert.Equal(["hot-1"], hot.Items.Select(c => c.NewValue));
        Assert.All(hot.Items, c => Assert.False(c.FromArchive));

        // Вікно через межу: одна видача за ChangedAt, архів першим.
        var both = await reader.ReadCellChangesAsync(
            new CellChangeFilter(ArchiveFrom, HotTo, DocumentId: doc.DocumentId), new CursorRequest(50), CancellationToken.None);
        Assert.Equal(["arc-1", "arc-2", "hot-1"], both.Items.Select(c => c.NewValue));
        Assert.Equal([true, true, false], both.Items.Select(c => c.FromArchive));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Курсор_проходить_межу_гаряче_архів_в_обидва_боки_без_дублів()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        foreach (var (at, value) in new[] { (A1, "a1"), (A2, "a2"), (A3, "a3") })
        {
            await WriteArchiveAsync(doc, "R1", doc.ColumnDefIds[1], at, value);
        }

        foreach (var (at, value) in new[] { (H1, "h1"), (H2, "h2"), (H3, "h3") })
        {
            await WriteHotAsync(db, doc, "R1", doc.ColumnDefIds[1], at, value);
        }

        var reader = new AuditReader(db);

        // Загальний журнал документа — від найстаріших: межа гаряче/архів лягає всередину другої сторінки.
        var forward = await WalkAsync(
            reader, new CellChangeFilter(ArchiveFrom, HotTo, DocumentId: doc.DocumentId), limit: 2);
        Assert.Equal(["a1", "a2", "a3", "h1", "h2", "h3"], forward.Select(c => c.NewValue));
        Assert.Equal([true, true, true, false, false, false], forward.Select(c => c.FromArchive));

        // Історія ОДНІЄЇ комірки — найновіші першими (AN-98): той самий обхід у зворотний бік.
        var single = new CellChangeFilter(
            ArchiveFrom, HotTo, DocumentId: doc.DocumentId, RowKey: "R1", ColumnDefId: doc.ColumnDefIds[1]);
        var backward = await WalkAsync(reader, single, limit: 2);
        Assert.Equal(["h3", "h2", "h1", "a3", "a2", "a1"], backward.Select(c => c.NewValue));
        Assert.Equal([false, false, false, true, true, true], backward.Select(c => c.FromArchive));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Порожній_архів_для_документа_дає_лише_гаряче_а_порожнє_вікно_архіву_порожню_видачу()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var withArchive = await builder.BuildAsync(ct: CancellationToken.None);
        var withoutArchive = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        await WriteArchiveAsync(withArchive, "R1", withArchive.ColumnDefIds[1], A1, "arc");
        await WriteHotAsync(db, withoutArchive, "R1", withoutArchive.ColumnDefIds[1], H1, "hot");

        var reader = new AuditReader(db);

        // Вікно архівне, документ чужий для архіву: архів непорожній у вікні, але рядків документа в ньому немає.
        var none = await reader.ReadCellChangesAsync(
            new CellChangeFilter(ArchiveFrom, ArchiveTo, DocumentId: withoutArchive.DocumentId),
            new CursorRequest(50),
            CancellationToken.None);
        Assert.Empty(none.Items);
        Assert.Null(none.NextCursor);

        // Вікно через межу, документ без архіву: лише його гарячий рядок.
        var hotOnly = await reader.ReadCellChangesAsync(
            new CellChangeFilter(ArchiveFrom, HotTo, DocumentId: withoutArchive.DocumentId),
            new CursorRequest(50),
            CancellationToken.None);
        Assert.Equal(["hot"], hotOnly.Items.Select(c => c.NewValue));
        Assert.False(hotOnly.Items[0].FromArchive);

        // Вікно, у якому немає ні гарячого, ні архівного.
        var empty = await reader.ReadCellChangesAsync(
            new CellChangeFilter(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2017, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                DocumentId: withArchive.DocumentId),
            new CursorRequest(50),
            CancellationToken.None);
        Assert.Empty(empty.Items);
        Assert.Null(empty.NextCursor);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Межі_читання_діють_на_архівні_рядки_до_сторінки_і_курсора()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 2, ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var visible = doc.ColumnDefIds[1];
        var hidden = doc.ColumnDefIds[2];

        // Між видимими архівними змінами лежать приховані: обхід limit=1 не має ані порожньої сторінки,
        // ані nextCursor «за самими прихованими» (оракул існування, R-11).
        await WriteArchiveAsync(doc, "R1", hidden, A1, "SECRET-1");
        await WriteArchiveAsync(doc, "R1", visible, A2, "open-1");
        await WriteArchiveAsync(doc, "R1", hidden, A3, "SECRET-2");
        await WriteHotAsync(db, doc, "R1", hidden, H1, "SECRET-3");
        await WriteHotAsync(db, doc, "R1", visible, H2, "open-2");

        var reader = new AuditReader(db);
        var narrowed = new CellChangeFilter(ArchiveFrom, HotTo, DocumentId: doc.DocumentId, VisibleColumnIds: [visible]);

        var pages = new List<PagedResult<CellChangeView>>();
        string? cursor = null;
        do
        {
            var page = await reader.ReadCellChangesAsync(narrowed, new CursorRequest(1, cursor), CancellationToken.None);
            pages.Add(page);
            cursor = page.NextCursor;
            Assert.True(pages.Count <= 3, "курсор не завершується");
        }
        while (cursor is not null);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, p => Assert.Single(p.Items));
        Assert.Equal(["open-1", "open-2"], pages.SelectMany(p => p.Items).Select(c => c.NewValue));
        Assert.DoesNotContain(pages.SelectMany(p => p.Items), c => c.NewValue!.StartsWith("SECRET", StringComparison.Ordinal));

        // Нічого не видно — порожньо й без курсора, хоч в архіві й у гарячому є зміни.
        var none = await reader.ReadCellChangesAsync(
            narrowed with { VisibleColumnIds = [] }, new CursorRequest(1), CancellationToken.None);
        Assert.Empty(none.Items);
        Assert.Null(none.NextCursor);

        // Контроль: без звуження видно все, включно з архівним.
        var all = await reader.ReadCellChangesAsync(
            narrowed with { VisibleColumnIds = null }, new CursorRequest(50), CancellationToken.None);
        Assert.Equal(5, all.Items.Count);
        Assert.Contains(all.Items, c => c.NewValue == "SECRET-1" && c.FromArchive);

        // Лічильники: ті самі межі, і архівні зміни в них є.
        var counts = await reader.CountCellChangesByColumnAsync(narrowed, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        var seen = Assert.Single(counts);
        Assert.Equal(visible, seen.ColumnDefId);
        Assert.Equal(2, seen.Total);

        var allCounts = await reader.CountCellChangesByColumnAsync(
            narrowed with { VisibleColumnIds = null }, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        Assert.Equal(5, allCounts.Sum(c => c.Total));
        Assert.Equal(3, allCounts.Single(c => c.ColumnDefId == hidden).Total);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Гаряче_вікно_читає_aud_CellChange_так_само_дорого_як_до_архіву()
    {
        // ⛔ Доказ «без регресії гарячого шляху»: БОЙОВИЙ текст пакета (з пробником архіву) проти того самого
        // запиту БЕЗ архіву (`CellChangesHotSql`, дослівно колишній) — на вікні, що архіву не зачіпає, до
        // `aud.CellChange` вони заходять однаково: ті самі заходи й ті самі логічні читання.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        await using var db = builder.CreateContext();

        var noise = new DateTime(2021, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        await new AuditWriter(db).WriteCellChangesAsync(
            [.. Enumerable.Range(0, 120).Select(i => Record(doc, "R1", doc.ColumnDefIds[1], noise.AddMinutes(i), $"v{i}"))],
            CancellationToken.None);

        // Архів існує, але в чужому місяці й чужому документі: пробник на ЦЬОМУ вікні має бути порожнім.
        await WriteArchiveAsync(doc, "R1", doc.ColumnDefIds[1], A1, "arc");

        var filter = new CellChangeFilter(
            new DateTime(2021, 3, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2021, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentId: doc.DocumentId);

        var probed = await MeasureAsync(filter, Plan.Probed);
        var hotOnly = await MeasureAsync(filter, Plan.HotOnly);
        var legacy = await MeasureAsync(filter, Plan.LegacyHot);

        output.WriteLine($"з пробником: {probed}; без архіву: {hotOnly}; до НФ-8.9.6 (datetime): {legacy}");

        Assert.Equal(51, legacy.Rows);
        Assert.Equal(legacy.Rows, hotOnly.Rows);
        Assert.Equal(legacy.Rows, probed.Rows);

        // Пакет з пробником читає aud.CellChange рівно як той самий запит без архіву...
        Assert.Equal(hotOnly.AudScans, probed.AudScans);
        Assert.Equal(hotOnly.AudReads, probed.AudReads);

        // ...а той — не дорожче за запит ДО цієї зміни (параметри `datetime`, як давав AddWithValue).
        Assert.True(hotOnly.AudScans <= legacy.AudScans, $"гарячий запит дорожчий за колишній: {hotOnly} проти {legacy}");
        Assert.True(hotOnly.AudReads <= legacy.AudReads, $"гарячий запит дорожчий за колишній: {hotOnly} проти {legacy}");

        // ⚠ Пробник відсікає партиції так само, як сам запит: ОДИН захід у архівну таблицю на гарячому вікні
        // (з параметром `datetime` їх було по одному на КОЖНУ партицію архіву — виміряно, див. ремарку пробника).
        Assert.True(probed.ArcScans <= 1, $"пробник архіву зайшов у таблицю {probed.ArcScans} разів: {probed}");
        Assert.True(probed.ArcReads <= 4, $"пробник архіву читає {probed.ArcReads} сторінок на гарячому вікні");

        // Архівне вікно (гілка UNION ALL): у кожну з таблиць — не більше пари заходів, без обходу партицій.
        var archiveFilter = new CellChangeFilter(ArchiveFrom, ArchiveTo, DocumentId: doc.DocumentId);
        var archived = await MeasureAsync(archiveFilter, Plan.Probed);
        output.WriteLine($"архівне вікно: {archived}");

        Assert.Equal(1, archived.Rows);
        Assert.True(archived.ArcScans <= 3, $"архівне вікно: заходів у arc.AuditCellChange {archived.ArcScans}: {archived}");
        Assert.True(archived.AudScans <= 3, $"архівне вікно: заходів у aud.CellChange {archived.AudScans}: {archived}");
    }

    [Theory]
    [InlineData(229)]
    [InlineData(230)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Відмова_в_праві_SELECT_на_архів_знімає_архів_а_не_журнал(int number)
    {
        // ⛔ Службовий акаунт з вузьким переліком прав (D-264) без SELECT на `arc.AuditCellChange` не повинен
        // втратити ВЕСЬ журнал: пробник архіву читає архівну таблицю на кожному зверненні. Мутація «прибрати
        // `catch (SqlException) when (229 or 230)`» — тест червоніє SqlException замість значення.
        var calls = new List<bool>();

        var result = await AuditReader.WithArchiveFallbackAsync(
            withArchive =>
            {
                calls.Add(withArchive);
                return withArchive ? throw Sql(number) : Task.FromResult("лише гаряче");
            });

        Assert.Equal("лише гаряче", result);
        Assert.Equal([true, false], calls);
    }

    [Theory]
    [InlineData(208)]
    [InlineData(1205)]
    [InlineData(2)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Інші_помилки_SQL_не_ховаються_падінням_на_гаряче(int number)
    {
        var calls = 0;

        var error = await Assert.ThrowsAsync<SqlException>(() => AuditReader.WithArchiveFallbackAsync<string>(
            _ =>
            {
                calls++;
                throw Sql(number);
            }));

        Assert.Equal(number, error.Number);
        Assert.Equal(1, calls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "НФ-8.9.6")]
    public async Task Повторна_відмова_в_праві_на_гарячій_таблиці_пробивається_нагору()
    {
        var calls = new List<bool>();

        var error = await Assert.ThrowsAsync<SqlException>(() => AuditReader.WithArchiveFallbackAsync<string>(
            withArchive =>
            {
                calls.Add(withArchive);
                throw Sql(229);
            }));

        Assert.Equal(229, error.Number);
        Assert.Equal([true, false], calls);
    }

    /// <summary><see cref="SqlException"/> із заданим номером: публічного конструктора немає, тож — рефлексією.</summary>
    private static SqlException Sql(int number)
    {
        var errorCtor = typeof(SqlError)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = errorCtor.GetParameters()
            .Select(p => p.Name == "infoNumber" ? number : DefaultOf(p.ParameterType))
            .ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(errors, [error]);

        var create = typeof(SqlException)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .First(m => m.Name == "CreateException"
                        && m.GetParameters().Select(p => p.ParameterType)
                            .SequenceEqual([typeof(SqlErrorCollection), typeof(string)]));

        var exception = (SqlException)create.Invoke(null, [errors, "16.0"])!;
        Assert.Equal(number, exception.Number);

        return exception;
    }

    private static object? DefaultOf(Type type)
        => type == typeof(string) ? "test"
            : type.IsValueType ? Activator.CreateInstance(type)
            : null;

    /// <summary>Обхід усіх сторінок; перевірка, що курсор завершується.</summary>
    private static async Task<List<CellChangeView>> WalkAsync(AuditReader reader, CellChangeFilter filter, int limit)
    {
        var items = new List<CellChangeView>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await reader.ReadCellChangesAsync(filter, new CursorRequest(limit, cursor), CancellationToken.None);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
            Assert.True(++pages < 20, "курсор не завершується");
        }
        while (cursor is not null);

        return items;
    }

    private static CellChangeRecord Record(TestDocument doc, string rowKey, int columnDefId, DateTime at, string newValue)
        => new(
            at,
            new CellAddress(new PeriodKey(201905), doc.RowIds[0], columnDefId),
            doc.DocumentId,
            rowKey,
            OldValue: "1",
            NewValue: newValue,
            ChangedByUserId: 1,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null);

    private static Task WriteHotAsync(
        EcrDbContext db, TestDocument doc, string rowKey, int columnDefId, DateTime at, string newValue)
        => new AuditWriter(db).WriteCellChangesAsync([Record(doc, rowKey, columnDefId, at, newValue)], CancellationToken.None);

    /// <summary>Рядок архіву аудиту — прямим ADO: UPDATE/DELETE там заборонені, INSERT — ні.</summary>
    private async Task WriteArchiveAsync(TestDocument doc, string rowKey, int columnDefId, DateTime at, string newValue)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO arc.AuditCellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES
                (@changedAt, 201905, @documentId, @rowId, @rowKey, @columnDefId,
                 N'1', @newValue, 1, N'UserEdit', 0);
            """;
        var moment = command.Parameters.Add("@changedAt", SqlDbType.DateTime2);
        moment.Scale = 3;
        moment.Value = at;
        command.Parameters.Add("@documentId", SqlDbType.BigInt).Value = doc.DocumentId;
        command.Parameters.Add("@rowId", SqlDbType.BigInt).Value = doc.RowIds[0];
        command.Parameters.Add("@rowKey", SqlDbType.NVarChar, 100).Value = rowKey;
        command.Parameters.Add("@columnDefId", SqlDbType.Int).Value = columnDefId;
        command.Parameters.Add("@newValue", SqlDbType.NVarChar, 1000).Value = newValue;

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>Виконує пакет сторінки журналу з <c>SET STATISTICS IO</c> і знімає числа по <c>aud</c>/<c>arc</c>.</summary>
    /// <remarks>
    /// ⛔ <c>ExecuteNonQuery</c>, а не читач: повідомлення <c>STATISTICS IO</c> з асинхронним <c>SqlDataReader</c>
    /// до <c>InfoMessage</c> не долітають (урок <c>AuditLastChangePartitionScanTests</c>); рядки рахуються
    /// окремим прогоном.
    /// </remarks>
    private async Task<IoStats> MeasureAsync(CellChangeFilter filter, Plan plan)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        const string order = "ChangedAt, Id";
        const string outer = "a.ChangedAt, a.Id";

        SqlCommand Build()
        {
            var command = connection.CreateCommand();
            var where = AuditReader.BuildCellChangeWhere(filter, command, after: null).ToString();
            command.CommandText = plan == Plan.Probed
                ? AuditReader.CellChangesPageSql(where, order, outer)
                : AuditReader.CellChangesHotSql(where, order, outer);
            command.Parameters.AddWithValue("@take", 51);

            if (plan == Plan.LegacyHot)
            {
                // Колишній спосіб прив'язки вікна: `AddWithValue(DateTime)` давав `datetime`.
                command.Parameters["@from"].SqlDbType = SqlDbType.DateTime;
                command.Parameters["@to"].SqlDbType = SqlDbType.DateTime;
            }

            return command;
        }

        var rows = 0;
        await using (var command = Build())
        await using (var reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {
            while (await reader.ReadAsync(CancellationToken.None))
            {
                rows++;
            }
        }

        var messages = new List<string>();
        void Handler(object sender, SqlInfoMessageEventArgs args) => messages.Add(args.Message);

        connection.InfoMessage += Handler;
        try
        {
            await using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "SET STATISTICS IO ON;";
                await setup.ExecuteNonQueryAsync(CancellationToken.None);
            }

            await using var command = Build();
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        finally
        {
            connection.InfoMessage -= Handler;
        }

        return IoStats.Parse(messages, rows);
    }

    /// <summary>Що міряємо: бойовий пакет з пробником, той самий запит без архіву, або він із колишніми `datetime`-параметрами.</summary>
    private enum Plan
    {
        Probed,
        HotOnly,
        LegacyHot,
    }

    private sealed record IoStats(int AudScans, int AudReads, int ArcScans, int ArcReads, int Rows)
    {
        public static IoStats Parse(IReadOnlyList<string> messages, int rows)
        {
            int scans = 0, reads = 0, arcScans = 0, arcReads = 0;
            var aud = false;

            foreach (var message in messages)
            {
                // ⚠ Одне повідомлення може нести кілька рядків «Table …» — тому Matches, а не Match.
                // Лапки навколо імені: `'CellChange'` не збігається з `'AuditCellChange'`.
                foreach (Match match in Regex.Matches(
                             message,
                             @"Table\s+'(CellChange|AuditCellChange)'\.\s+Scan count\s+(\d+),\s+logical reads\s+(\d+)",
                             RegexOptions.IgnoreCase,
                             TimeSpan.FromSeconds(5)))
                {
                    var table = match.Groups[1].Value;
                    var count = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    var logical = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

                    if (string.Equals(table, "CellChange", StringComparison.OrdinalIgnoreCase))
                    {
                        scans += count;
                        reads += logical;
                        aud = true;
                    }
                    else
                    {
                        arcScans += count;
                        arcReads += logical;
                    }
                }
            }

            // ⚠ Нерозпізнаний вивід — падіння з повним текстом, а не нулі: нулі пройшли б будь-яку межу.
            if (!aud)
            {
                throw new InvalidOperationException(
                    "У виводі SET STATISTICS IO немає рядка про aud.CellChange. Отримано:"
                    + Environment.NewLine + string.Join(Environment.NewLine, messages));
            }

            return new IoStats(scans, reads, arcScans, arcReads, rows);
        }
    }
}
