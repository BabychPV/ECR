// tests/Ecr.Infrastructure.Tests/Persistence/RegistryTemporalTests.cs
using Ecr.Application.Common;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>RK03RegistryTemporalHistory</c> на РЕАЛЬНОМУ SQL Server (RT-04,
/// D-158, FEATURE-REGISTRY-TABLES §3.2, §3.6): системна історія записів і
/// значень довідника й автор кожної версії рядка.
/// </summary>
/// <remarks>
/// ⚠ Момент «до правки» береться з ГОДИННИКА БАЗИ (<c>SYSUTCDATETIME()</c>), а
/// не з <see cref="TestClock"/>: системний час пише SQL Server, і момент із
/// годинника застосунку міг би розійтися з ним на мілісекунди, яких якраз
/// досить, щоб «станом на» потрапити по інший бік правки.
///
/// Мутаційний доказ (RT-04, §9.2): прибрати <c>.IsTemporal</c> у
/// <c>RegistryValueConfiguration</c> — червоніє
/// <see cref="AS_OF_повертає_старе_значення"/>; не викликати
/// <c>StampRegistryAuthors</c> в <c>UnitOfWork</c> — червоніє
/// <see cref="Історичний_рядок_має_автора"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryTemporalTests(SqlServerFixture sql)
{
    private const int Author = 7;
    private const int Editor = 8;

    private static readonly string[] ExpectedPeriod =
        ["PeriodEnd:datetime2(3):hidden", "PeriodStart:datetime2(3):hidden"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-04")]
    public async Task AS_OF_повертає_старе_значення()
    {
        var setup = await ArrangeAsync();
        var before = await DatabaseNowAsync();

        await using (var db = sql.CreateContext())
        {
            var value = await db.RegistryValues.SingleAsync(v => v.Id == setup.ValueId);
            value.Set(CellDataType.Decimal, 17.5m, null);
            await new UnitOfWork(db, currentUser: User(Editor)).SaveChangesAsync(CancellationToken.None);
        }

        await using var read = sql.CreateContext();
        var then = await read.RegistryValues.TemporalAsOf(before)
                             .Where(v => v.Id == setup.ValueId)
                             .Select(v => v.ValueNumeric)
                             .SingleAsync();
        var now = await read.RegistryValues.AsNoTracking()
                            .Where(v => v.Id == setup.ValueId)
                            .Select(v => v.ValueNumeric)
                            .SingleAsync();

        Assert.Equal(16.043m, then);
        Assert.Equal(17.5m, now);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-04")]
    public async Task Історичний_рядок_має_автора()
    {
        var setup = await ArrangeAsync();

        await using (var db = sql.CreateContext())
        {
            var entry = await db.RegistryEntries.SingleAsync(e => e.Id == setup.EntryId);
            var value = await db.RegistryValues.SingleAsync(v => v.Id == setup.ValueId);
            entry.Rename(Text("Renamed"));
            value.Set(CellDataType.Decimal, 18m, null);
            await new UnitOfWork(db, currentUser: User(Editor)).SaveChangesAsync(CancellationToken.None);
        }

        await using var read = sql.CreateContext();

        // Версії значення від найстаршої: перша — автора вставки, друга — редактора.
        var valueVersions = await read.RegistryValues.TemporalAll()
            .Where(v => v.Id == setup.ValueId)
            .OrderBy(v => EF.Property<DateTime>(v, "PeriodStart"))
            .Select(v => new { v.ValueNumeric, v.ChangedByUserId })
            .ToListAsync();

        Assert.Equal(2, valueVersions.Count);
        Assert.Equal(16.043m, valueVersions[0].ValueNumeric);
        Assert.Equal(Author, valueVersions[0].ChangedByUserId);
        Assert.Equal(18m, valueVersions[1].ValueNumeric);
        Assert.Equal(Editor, valueVersions[1].ChangedByUserId);

        var entryAuthors = await read.RegistryEntries.TemporalAll()
            .Where(e => e.Id == setup.EntryId)
            .OrderBy(e => EF.Property<DateTime>(e, "PeriodStart"))
            .Select(e => e.ChangedByUserId)
            .ToListAsync();

        Assert.Equal(new int?[] { Author, Editor }, entryAuthors);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-04")]
    public async Task Зміна_без_автора_не_успадковує_автора_попередньої_версії()
    {
        var setup = await ArrangeAsync();

        // Одиниця роботи без користувача (задача без автора): рядок мусить
        // сказати «невідомо», а не приписати зміну авторові вставки.
        await using (var db = sql.CreateContext())
        {
            var value = await db.RegistryValues.SingleAsync(v => v.Id == setup.ValueId);
            value.Set(CellDataType.Decimal, 19m, null);
            await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);
        }

        await using var read = sql.CreateContext();
        var author = await read.RegistryValues.AsNoTracking()
                               .Where(v => v.Id == setup.ValueId)
                               .Select(v => v.ChangedByUserId)
                               .SingleAsync();
        Assert.Null(author);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-04")]
    public async Task TemporalAsOf_переводить_id_int_в_long_у_фільтрі_і_зʼєднанні()
    {
        // `Id` запису в сутності — long, у схемі — int (конверсія значення,
        // DictionariesConfiguration). «Станом на» мусить пройти ту саму
        // конверсію і в предикаті, і в зʼєднанні значень із записом.
        var setup = await ArrangeAsync();
        var asOf = await DatabaseNowAsync();

        await using var read = sql.CreateContext();
        var rows = await (
                from e in read.RegistryEntries.TemporalAsOf(asOf)
                join v in read.RegistryValues.TemporalAsOf(asOf) on e.Id equals v.RegistryEntryId
                where e.Id == setup.EntryId
                select new { e.Id, e.Code, v.RegistryEntryId, v.ValueNumeric })
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(setup.EntryId, row.Id);
        Assert.Equal(setup.EntryId, row.RegistryEntryId);
        Assert.Equal(16.043m, row.ValueNumeric);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "RT-04")]
    [InlineData("RegistryEntry", "RegistryEntryHistory")]
    [InlineData("RegistryValue", "RegistryValueHistory")]
    public async Task Таблиця_має_системну_історію_з_періодом_datetime2_3(string table, string history)
    {
        await using var db = sql.CreateContext();

        var qualified = "dic." + table;
        var historyName = Assert.Single(await db.Database.SqlQuery<string>(
            $"""
            SELECT OBJECT_SCHEMA_NAME(history_table_id) + N'.' + OBJECT_NAME(history_table_id) AS Value
            FROM sys.tables WHERE object_id = OBJECT_ID({qualified}) AND temporal_type = 2
            """).ToListAsync());
        Assert.Equal($"dic.{history}", historyName);

        // D-68: мітки часу — datetime2(3); HIDDEN — `SELECT *` колонок періоду не бачить.
        var period = (await db.Database.SqlQuery<string>(
            $"""
            SELECT c.name + N':' + TYPE_NAME(c.user_type_id) + N'(' + CAST(c.scale AS nvarchar(2)) + N')'
                   + CASE WHEN c.is_hidden = 1 THEN N':hidden' ELSE N'' END AS Value
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID({qualified}) AND c.generated_always_type IN (1, 2)
            """).ToListAsync()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(ExpectedPeriod, period);
    }

    /// <summary>Довідник із числовим полем, один запис і одне значення 16.043 від <see cref="Author"/>.</summary>
    private async Task<(long EntryId, long ValueId)> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();

        var registry = new RegistryDef(EcrCode.Create($"RT04_{tag}"), Text("Temporal probe"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var field = new RegistryFieldDef(registry.Id, EcrCode.Create("MW"), Text("MW"), CellDataType.Decimal, 1);
        db.RegistryFieldDefs.Add(field);
        await db.SaveChangesAsync();

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("C1"), Text("Methane"), Author, DateTime.UtcNow);
        var value = new RegistryValue(entry, field.Id);
        value.Set(CellDataType.Decimal, 16.043m, null);
        db.RegistryEntries.Add(entry);
        db.RegistryValues.Add(value);
        await new UnitOfWork(db, currentUser: User(Author)).SaveChangesAsync(CancellationToken.None);

        // ⚠ Наступна правка в ту саму мілісекунду дала б версію нульової
        // тривалості (datetime2(3), §3.2), і «станом на» не мав би моменту,
        // коли видно саме вставлене значення.
        await Task.Delay(20);
        return (entry.Id, value.Id);
    }

    private async Task<DateTime> DatabaseNowAsync()
    {
        await using var db = sql.CreateContext();
        var now = Assert.Single(await db.Database
            .SqlQueryRaw<DateTime>("SELECT CAST(SYSUTCDATETIME() AS datetime2(3)) AS Value")
            .ToListAsync());
        await Task.Delay(20);
        return now;
    }

    private static ICurrentUser User(int id)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(id);
        return user;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
