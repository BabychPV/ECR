// tests/Ecr.Infrastructure.Tests/Persistence/ColumnPathMapperTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// C1 (P3 «Аудиту»): <c>ColumnPathMapper.MapToVersionAsync</c> будує <c>byPath</c> через <c>GroupBy(...).First()</c>.
/// Характеризація: чи може шлях «аркуш → таблиця → колонка» повторитися у цільовій версії.
/// </summary>
/// <remarks>
/// Висновок: НЕ може. Унікальні індекси <c>UQ_SheetDef</c> (версія, код), <c>UQ_TableDef</c> (аркуш, код) і
/// <c>UQ_ColumnDef</c> (таблиця, код) — безумовні, тобто вилучені (<c>IsDeleted</c>) рядки теж займають код, тож
/// «вилучена + нова з тим самим кодом» у одній таблиці неможлива. <c>First()</c> недетермінованим не буває:
/// у групі завжди один елемент (вилучені відсіяно ще в запиті). Якщо хтось колись зробить індекс фільтрованим
/// (<c>WHERE IsDeleted = 0</c>), другий тест червоніє — і тоді <c>First()</c> треба замінити на детермінований
/// вибір за Id.
/// </remarks>
[Collection("SqlServer")]
public sealed class ColumnPathMapperTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C1")]
    public async Task Вилучена_колонка_цільової_версії_не_є_відповідником_решта_відображається_за_шляхом()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, ct: ct);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await new TemplateVersionStore(db).CloneAsync(
                doc.TemplateVersionId, $"7.{tag[..4].GetHashCode() & 0xFFF}.0.2", 1, Now, ct);
        }

        await using var read = builder.CreateContext();
        var sourceCodes = await read.ColumnDefs.AsNoTracking()
            .Where(c => doc.ColumnDefIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var cloneColumns = await (
                from c in read.ColumnDefs
                join t in read.TableDefs on c.TableDefId equals t.Id
                join s in read.SheetDefs on t.SheetDefId equals s.Id
                where s.TemplateVersionId == cloneId
                select c)
            .ToListAsync(ct);
        var cloneByCode = cloneColumns.ToDictionary(c => c.Code);

        // Вилучаємо в клоні колонку, що відповідає другій колонці джерела.
        var deleted = cloneByCode[sourceCodes[doc.ColumnDefIds[1]]];
        deleted.SoftDelete(1, Now);
        await read.SaveChangesAsync(ct);

        var mapped = await new ColumnPathMapper(builder.CreateContext())
            .MapToVersionAsync(doc.ColumnDefIds, cloneId, ct);

        Assert.Equal(2, mapped.Count);
        Assert.Equal(cloneByCode[sourceCodes[doc.ColumnDefIds[0]]].Id, mapped[doc.ColumnDefIds[0]]);
        Assert.Equal(cloneByCode[sourceCodes[doc.ColumnDefIds[2]]].Id, mapped[doc.ColumnDefIds[2]]);
        Assert.DoesNotContain(doc.ColumnDefIds[1], mapped.Keys);

        // Колонка цільової версії відображається сама на себе — навіть вилучена (правило, що її згадує, — її власне).
        var self = await new ColumnPathMapper(builder.CreateContext())
            .MapToVersionAsync([deleted.Id], cloneId, ct);
        Assert.Equal(deleted.Id, Assert.Single(self).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "C1")]
    public async Task Дубль_шляху_у_версії_неможливий_UQ_ColumnDef_безумовний_тож_First_детермінований()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, ct: ct);

        await using var db = builder.CreateContext();
        var code = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == doc.ColumnDefIds[0]).Select(c => c.Code).SingleAsync(ct);

        // Навіть коли існуюча колонка вилучена, її код лишається зайнятим: «вилучена + нова» неможливі.
        var existing = await db.ColumnDefs.SingleAsync(c => c.Id == doc.ColumnDefIds[0], ct);
        existing.SoftDelete(1, Now);
        await db.SaveChangesAsync(ct);

        db.ColumnDefs.Add(new ColumnDef(
            doc.TableDefId, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Dup" }), 2, CellDataType.String));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }
}
