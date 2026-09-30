// tests/Ecr.Infrastructure.Tests/Persistence/UnitUsageForeignKeyTests.cs
using System.Data.Common;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Units;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// «Де використовується одиниця» бачить КОЖЕН зовнішній ключ на <c>uom.Unit</c> (HSE301 U2).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Що було. <c>UnitStore.FindUnitUsageAsync</c> не читав <c>ext.RowWindowMap</c>,
/// <c>ext.RowWindowSource</c>, <c>ext.RowWindowValue</c> (F5) і <c>doc.DocumentHeaderValue</c>.
/// Одиниця, яку тримали лише вони, проходила перевірку «ніщо не посилається», і видалення
/// падало на FK голим <c>500</c> замість <c>409 ECR-UOM-0409</c> із переліком.
/// </para>
/// <para>
/// ⚠ Храповик (<see cref="Кожен_зовнішній_ключ_на_одиницю_читає_перелік_використань"/>) порівнює
/// не список у коді зі списком у тесті, а те, що перелік СПРАВДІ читає (текст команд EF,
/// перехоплених під час виклику), з тим, що справді є в схемі: FK моделі EF І
/// <c>sys.foreign_keys</c> розгорнутої бази (схема має два джерела — міграції й сирі
/// <c>Sql/*.sql</c>). Новий FK без запиту, прибраний запит чи запит не по тому стовпцю —
/// червоне з назвою стовпця.
/// </para>
/// <para>
/// Мутаційні докази (U2): прибрати вид <c>RowWindowMap</c> з переліку → червоні обидва тести
/// прив'язки і храповик; прибрати рядок <c>doc.DocumentHeaderValue</c> → червоний тест шапки і
/// храповик; додати в очікуване фіктивний FK → червоний храповик.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class UnitUsageForeignKeyTests(SqlServerFixture sql)
{
    /// <summary>
    /// Посилання, які до HSE301 U1 перелік читав БЕЗ зовнішнього ключа і тому називав явно.
    /// </summary>
    /// <remarks>
    /// ⛔ Міграція <c>U1UnitForeignKeys</c> поставила на них <c>FK_ColumnDef_Unit</c> і
    /// <c>FK_RegField_Unit</c>: тепер храповик виводить їх зі схеми сам, а посилань на
    /// одиницю без ключа не лишилось жодного. Тест нижче вимагає, щоб обидва стояли і в
    /// моделі EF, і в розгорнутій базі.
    /// </remarks>
    private static readonly string[] FormerlyWithoutForeignKey =
    [
        "cfg.ColumnDef.UnitId",
        "cfg.RegistryFieldDef.UnitId",
    ];

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U2")]
    public async Task Кожен_зовнішній_ключ_на_одиницю_читає_перелік_використань()
    {
        await using var probe = sql.CreateContext();
        var fromModel = ModelForeignKeys(probe);
        var fromDatabase = await DatabaseForeignKeysAsync();

        // ⛔ Порожній перелік FK дав би зелене на будь-якому переліку.
        Assert.NotEmpty(fromModel);
        Assert.NotEmpty(fromDatabase);

        // HSE301 U1: колишні посилання без ключа тепер мають ключ і в моделі, і в базі.
        // Мутація: прибрати HasOne<Unit>() з конфігурації ColumnDef чи RegistryFieldDef —
        // червоне тут (модель), прибрати AddForeignKey з міграції — червоне тут (база).
        foreach (var reference in FormerlyWithoutForeignKey)
        {
            Assert.Contains(reference, fromModel);
            Assert.Contains(reference, fromDatabase);
        }

        var expected = fromModel
            .Union(fromDatabase)
            .Order(StringComparer.Ordinal)
            .ToList();

        var recorder = new CommandRecorder();
        await using (var db = new EcrDbContext(EfWarningGuard.Apply(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo")))
            .AddInterceptors(recorder)
            .Options))
        {
            // take = 0: сторінка не потрібна, але підрахунок і перевірки «чи є» йдуть однаково.
            // R2a: сирі точки читаються від мапінгів, що називають одиницю джерелом, тож одиниця
            // мусить мати мапінг — інакше запит до ext.RawDataPoint не випускається взагалі.
            await new UnitStore(db).FindUnitUsageAsync(await UnitWithFieldMapAsync(), 0, CancellationToken.None);
        }

        var read = ReadUnitColumns(recorder.Commands).Order(StringComparer.Ordinal).ToList();

        var missing = expected.Except(read, StringComparer.Ordinal).ToList();
        var extra = read.Except(expected, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0 && extra.Count == 0,
            "Перелік використань одиниці (UnitStore.FindUnitUsageAsync) розійшовся зі схемою."
            + Environment.NewLine
            + "Не читає (видалення впаде на FK голим 500 замість 409): "
            + (missing.Count == 0 ? "—" : string.Join(", ", missing))
            + Environment.NewLine
            + "Читає стовпці, на яких FK немає: "
            + (extra.Count == 0 ? "—" : string.Join(", ", extra))
            + Environment.NewLine
            + "FK моделі EF: " + string.Join(", ", fromModel)
            + Environment.NewLine
            + "FK бази: " + string.Join(", ", fromDatabase));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U2")]
    public async Task Одиниця_цілі_прив_язки_PI_не_видаляється_409_зі_списком_а_не_500()
    {
        var arranged = await ArrangeRowWindowAsync();
        var unitId = await NewUnitAsync("a");

        int mapId;
        await using (var db = sql.CreateContext())
        {
            var map = await NewMapAsync(db, arranged, unitId);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();
            mapId = map.Id;
        }

        var conflict = await DeleteExpectingConflictAsync(unitId);

        var item = Assert.Single(References(conflict));
        Assert.Equal(
            (UsageKinds.RowWindowMap, mapId.ToString(System.Globalization.CultureInfo.InvariantCulture),
             $"{arranged.TableCode}.{arranged.TargetCode}",
             $"/admin/templates/{arranged.TemplateId}/versions/{arranged.TemplateVersionId}"),
            (item.Kind, item.Id, item.Label, item.Route));
        await AssertUnitKeptAsync(unitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U2")]
    public async Task Одиниця_джерела_прив_язки_PI_не_видаляється_409_один_рядок_на_прив_язку()
    {
        var arranged = await ArrangeRowWindowAsync();
        var unitId = await NewUnitAsync("b");
        var otherId = await NewUnitAsync("c");

        int mapId;
        await using (var db = sql.CreateContext())
        {
            // Ціль — ІНША одиниця: нашу тримають лише два джерела.
            var map = await NewMapAsync(db, arranged, otherId, selector: true);
            map.AddSource(null, arranged.EntityId, "Flare HP|Flow", unitId);
            map.AddSource("FLARE_LP", arranged.EntityId, "Flare LP|Flow", unitId);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();
            mapId = map.Id;
        }

        var conflict = await DeleteExpectingConflictAsync(unitId);

        Assert.Equal("1", conflict.Details!["total"]);
        var item = Assert.Single(References(conflict));
        Assert.Equal(
            (UsageKinds.RowWindowMap, mapId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (item.Kind, item.Id));
        await AssertUnitKeptAsync(unitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U2")]
    public async Task Одиниця_провенансу_підтягування_не_видаляється_409_а_не_500()
    {
        var arranged = await ArrangeRowWindowAsync();
        var unitId = await NewUnitAsync("d");
        var otherId = await NewUnitAsync("e");

        await using (var db = sql.CreateContext())
        {
            // Прив'язка в іншій одиниці; нашу тримає лише записаний провенанс.
            var map = await NewMapAsync(db, arranged, otherId);
            db.RowWindowMaps.Add(map);
            await db.SaveChangesAsync();

            var at = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
            db.RowWindowValues.Add(new RowWindowValue(
                arranged.PeriodKey, arranged.TableInstanceId, arranged.RowKey, arranged.TargetId, map.Id,
                arranged.EntityId, "Flare HP|Flow", at, at.AddSeconds(900), RowWindowSummaryKind.Total,
                unitId, at.AddHours(1)));
            await db.SaveChangesAsync();
        }

        var conflict = await DeleteExpectingConflictAsync(unitId);

        var item = Assert.Single(References(conflict));
        Assert.Equal((UsageKinds.Data, "ext.RowWindowValue"), (item.Kind, item.Id));
        await AssertUnitKeptAsync(unitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-U2")]
    public async Task Одиниця_в_значенні_шапки_документа_не_видаляється_409_а_не_500()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 1, rowCount: 1);
        var unitId = await NewUnitAsync("f");

        await using (var db = sql.CreateContext())
        {
            var field = new HeaderFieldDef(
                doc.TemplateVersionId, EcrCode.Create($"UOM_{_tag}"), Name("Unit"), 0, CellDataType.Unit);
            db.HeaderFieldDefs.Add(field);
            await db.SaveChangesAsync();

            db.DocumentHeaderValues.Add(new DocumentHeaderValue(
                doc.DocumentId, field.Id, new DocumentHeaderValueData { ValueUnitId = unitId }));
            await db.SaveChangesAsync();
        }

        var conflict = await DeleteExpectingConflictAsync(unitId);

        var item = Assert.Single(References(conflict));
        Assert.Equal((UsageKinds.Data, "doc.DocumentHeaderValue"), (item.Kind, item.Id));
        await AssertUnitKeptAsync(unitId);
    }

    /// <summary>
    /// Видалення через справжній обробник і справжнє сховище. Виняток іншого типу (зокрема
    /// <see cref="DbUpdateException"/> на FK — той самий голий 500) пролітає як провал тесту.
    /// </summary>
    private async Task<ConcurrencyConflictException> DeleteExpectingConflictAsync(int unitId)
    {
        await using var db = sql.CreateContext();
        var handler = new DeleteUnitHandler(new UnitStore(db), new UnitOfWork(db), Access(), User());

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => handler.HandleAsync(unitId, CancellationToken.None));

        Assert.Equal("ECR-UOM-0409", conflict.ErrorCode);
        Assert.Equal("err.ECR-UOM-0409.unitInUse", conflict.Details!["messageKey"]);
        return conflict;
    }

    private static IReadOnlyList<UsageItemDto> References(ConcurrencyConflictException conflict)
        => Assert.IsAssignableFrom<IReadOnlyList<UsageItemDto>>(conflict.Details!["references"]);

    private async Task AssertUnitKeptAsync(int unitId)
    {
        await using var read = sql.CreateContext();
        Assert.True(await read.Units.AnyAsync(u => u.Id == unitId));
    }

    /// <summary>Одиниця, яку називає джерелом мапінг поля (потрібна для читання сирих точок).</summary>
    private async Task<int> UnitWithFieldMapAsync()
    {
        var arranged = await ArrangeRowWindowAsync();
        var unitId = await NewUnitAsync("m");
        await using var db = sql.CreateContext();
        var map = EntityFieldMap.ToColumn(arranged.EntityId, $"Path_{_tag}", arranged.TargetId);
        map.SetUnits(unitId, null);
        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync();
        return unitId;
    }
    /// <summary>Небазова одиниця без жодного посилання.</summary>
    private async Task<int> NewUnitAsync(string suffix)
    {
        await using var db = sql.CreateContext();
        var dimension = await db.Dimensions.OrderBy(d => d.Id).FirstAsync();
        var unit = new Unit(
            EcrCode.Create($"u2{suffix}{_tag}".ToLowerInvariant()), Name(suffix), Name($"Unit {suffix}"),
            dimension.Id, isBase: false, factorToBase: 2m, offsetToBase: 0m);
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        return unit.Id;
    }

    private static async Task<RowWindowMap> NewMapAsync(
        EcrDbContext db, Arranged arranged, int targetUnitId, bool selector = false)
    {
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == arranged.TableDefId)
            .ToDictionaryAsync(c => c.Id);

        return RowWindowMap.Create(
            columns[arranged.TargetId], columns[arranged.StartId], columns[arranged.EndId],
            selector ? columns[arranged.SelectorId] : null, RowWindowSummaryKind.Total, isStep: true, targetUnitId);
    }

    /// <summary>
    /// Таблиця з колонками вікна (Date), ціллю (Decimal) і селектором (String), екземпляр і
    /// неактивна сутність джерела — як у <c>RowWindowSchemaTests</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Сутність НЕАКТИВНА навмисно: активна без завершеного збору лишилась би в спільній базі
    /// й фарбувала б <c>SourcesHealthCheck</c> для кожного наступного прогону.
    /// </remarks>
    private async Task<Arranged> ArrangeRowWindowAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202601);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await using var db = sql.CreateContext();

        var start = new ColumnDef(doc.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
        var end = new ColumnDef(doc.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
        db.ColumnDefs.AddRange(start, end);

        var dataSource = new DataSource(
            EcrCode.Create($"U2SRC{tag}"), Name("HSE301-U2"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        var entity = new SourceEntity(dataSource.Id, $"U2Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var targetId = doc.ColumnDefIds[1];
        var codes = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == targetId)
            .Join(db.TableDefs, c => c.TableDefId, t => t.Id, (c, t) => new { Table = t.Code, Column = c.Code })
            .SingleAsync();

        var periodKey = doc.PeriodKey.Value;
        var firstRowId = doc.RowIds[0];
        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey && r.Id == firstRowId)
            .Select(r => r.RowKeyValue)
            .SingleAsync();

        return new Arranged(
            doc.TemplateId, doc.TemplateVersionId, doc.TableDefId, codes.Table, targetId, codes.Column,
            start.Id, end.Id, SelectorId: doc.ColumnDefIds[0], entity.Id, periodKey, doc.TableInstanceId, rowKey);
    }

    /// <summary><c>схема.Таблиця.Стовпець</c> кожного FK моделі EF, що вказує на <see cref="Unit"/>.</summary>
    private static List<string> ModelForeignKeys(EcrDbContext db)
        => [.. db.Model.GetEntityTypes()
            .SelectMany(e => e.GetDeclaredForeignKeys())
            .Where(fk => fk.PrincipalEntityType.ClrType == typeof(Unit))
            .SelectMany(fk => fk.Properties.Select(p =>
                $"{fk.DeclaringEntityType.GetSchema()}.{fk.DeclaringEntityType.GetTableName()}.{p.GetColumnName()}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>Те саме з каталогу розгорнутої бази — разом із FK, яких модель EF не знає.</summary>
    private async Task<List<string>> DatabaseForeignKeysAsync()
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT s.name + N'.' + t.name + N'.' + c.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables t ON t.object_id = fkc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE fk.referenced_object_id = OBJECT_ID(N'uom.Unit')
            """;
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>схема.Таблиця.Стовпець</c> кожного стовпця <c>…UnitId</c>, який читають команди: псевдонім
    /// таблиці (<c>FROM [ext].[RowWindowMap] AS [r]</c>) зводиться до її імені в межах команди.
    /// </summary>
    private static HashSet<string> ReadUnitColumns(IEnumerable<string> commands)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in commands)
        {
            var aliases = TableAlias().Matches(text)
                .GroupBy(m => m.Groups["alias"].Value, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => $"{g.First().Groups["schema"].Value}.{g.First().Groups["table"].Value}",
                    StringComparer.Ordinal);

            foreach (Match m in UnitColumn().Matches(text))
            {
                if (aliases.TryGetValue(m.Groups["alias"].Value, out var table))
                {
                    columns.Add($"{table}.{m.Groups["column"].Value}");
                }
            }
        }

        return columns;
    }

    private static IAccessDecisionService Access()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Uom.EditCatalog").Build());
        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        return user;
    }

    private static LocalizedText Name(string en) => new(new Dictionary<string, string> { ["en"] = en });

    [GeneratedRegex(@"(?:FROM|JOIN)\s+\[(?<schema>\w+)\]\.\[(?<table>\w+)\]\s+AS\s+\[(?<alias>\w+)\]")]
    private static partial Regex TableAlias();

    [GeneratedRegex(@"\[(?<alias>\w+)\]\.\[(?<column>\w*UnitId)\]")]
    private static partial Regex UnitColumn();

    private sealed record Arranged(
        int TemplateId,
        int TemplateVersionId,
        int TableDefId,
        string TableCode,
        int TargetId,
        string TargetCode,
        int StartId,
        int EndId,
        int SelectorId,
        int EntityId,
        int PeriodKey,
        long TableInstanceId,
        string RowKey);

    /// <summary>Текст кожної команди, яку випустив EF.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands
        {
            get
            {
                lock (_commands)
                {
                    return [.. _commands];
                }
            }
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            lock (_commands)
            {
                _commands.Add(command.CommandText);
            }
        }
    }
}
