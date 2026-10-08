// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionCloneGraphStyleTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D2/D3 (огляд клону версії): «Clone version» мав мовчки лишати в клоні Id ДЖЕРЕЛА у
/// <c>ColumnDef.CascadeFromColumnId</c>, <c>RowDef.ParentRowDefId</c> та у трьох посиланнях на стиль
/// (<c>ColumnDef.StyleId</c>, <c>RowDef.StyleId</c>, <c>TableDef.HeaderStyleId</c>), а <c>cfg.StyleDef</c>
/// (належить версії, <c>UQ_StyleDef</c>) не клонувався взагалі.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати перев'язку графа/стилів зі <c>SaveCloneAsync</c> — тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCloneGraphStyleTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(TestDocument Doc, int StyleA, int StyleB, int StyleC);

    private static async Task<Seed> SeedAsync(TestDocumentBuilder builder)
    {
        var ct = CancellationToken.None;
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 3, ct: ct);
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        await using var db = builder.CreateContext();
        var a = new StyleDef(doc.TemplateVersionId, EcrCode.Create($"STA{suffix}"));
        a.SetAppearance("Arial", 11m, true, false, unchecked((int)0xFF112233), null, null, 1, 0, false, "0.00");
        var b = new StyleDef(doc.TemplateVersionId, EcrCode.Create($"STB{suffix}"));
        b.SetAppearance(null, null, false, true, null, unchecked((int)0xFFEEEEEE), null, null, null, true, null);
        var c = new StyleDef(doc.TemplateVersionId, EcrCode.Create($"STC{suffix}"));
        c.SetAppearance(null, null, true, true, null, null, null, 2, 2, false, null);
        db.StyleDefs.AddRange(a, b, c);
        await db.SaveChangesAsync(ct);

        var cols = await db.ColumnDefs.Where(x => x.TableDefId == doc.TableDefId).OrderBy(x => x.Ordinal).ToListAsync(ct);
        var rows = await db.RowDefs.Where(x => x.TableDefId == doc.TableDefId).OrderBy(x => x.Ordinal).ToListAsync(ct);
        var table = await db.TableDefs.SingleAsync(x => x.Id == doc.TableDefId, ct);

        db.Entry(cols[2]).Property("CascadeFromColumnId").CurrentValue = cols[1].Id;
        cols[0].SetPresentation(null, null, a.Id);
        rows[1].SetParent(rows[0].Id);
        rows[2].SetParent(rows[1].Id);
        db.Entry(rows[0]).Property("StyleId").CurrentValue = b.Id;
        db.Entry(table).Property("HeaderStyleId").CurrentValue = c.Id;
        await db.SaveChangesAsync(ct);

        return new Seed(doc, a.Id, b.Id, c.Id);
    }

    private static async Task<int> CloneAsync(EcrDbContext db, int sourceId, string version)
        => await new TemplateVersionStore(db).CloneAsync(sourceId, version, 1, Now, CancellationToken.None);

    private static string Ver() => $"8.{Random.Shared.Next(1, 99999)}.{Random.Shared.Next(1, 99999)}.0";

    private sealed record Graph(
        List<ColumnDef> Cols, List<RowDef> Rows, TableDef Table, IReadOnlyDictionary<int, StyleDef> Styles);

    private static async Task<Graph> ReadAsync(TestDocumentBuilder builder, int versionId)
    {
        await using var db = builder.CreateContext();
        var table = await db.TableDefs.AsNoTracking()
            .SingleAsync(t => db.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == versionId));
        var cols = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == table.Id).OrderBy(c => c.Ordinal).ToListAsync();
        var rows = await db.RowDefs.AsNoTracking().Where(r => r.TableDefId == table.Id).OrderBy(r => r.Ordinal).ToListAsync();
        var styles = await new StyleCatalog(db).GetAsync(versionId, CancellationToken.None);
        return new Graph(cols, rows, table, styles);
    }

    private static void AssertGraph(Graph g, Graph source)
    {
        // D2: каскад і ієрархія вказують на колонки/рядки САМОГО клону.
        Assert.Equal(g.Cols[1].Id, g.Cols[2].CascadeFromColumnId);
        Assert.NotEqual(source.Cols[1].Id, g.Cols[2].CascadeFromColumnId);
        Assert.Null(g.Cols[0].CascadeFromColumnId);
        Assert.Null(g.Rows[0].ParentRowDefId);
        Assert.Equal(g.Rows[0].Id, g.Rows[1].ParentRowDefId);
        Assert.Equal(g.Rows[1].Id, g.Rows[2].ParentRowDefId);

        // D3: стилі версії клоновані; три посилання резолвляться в каталозі клону, на ІНШІ Id.
        Assert.Equal(3, g.Styles.Count);
        Assert.Empty(g.Styles.Keys.Intersect(source.Styles.Keys));

        var a = g.Styles[g.Cols[0].StyleId!.Value];
        var b = g.Styles[g.Rows[0].StyleId!.Value];
        var c = g.Styles[g.Table.HeaderStyleId!.Value];
        Assert.Equal(source.Styles[source.Cols[0].StyleId!.Value].Code, a.Code);
        Assert.Equal(source.Styles[source.Rows[0].StyleId!.Value].Code, b.Code);
        Assert.Equal(source.Styles[source.Table.HeaderStyleId!.Value].Code, c.Code);
        Assert.Equal(("Arial", 11m, true, 1, "0.00"), (a.FontName, a.FontSize, a.IsBold, (int)a.HorizontalAlign!, a.NumberFormat));
        Assert.Equal((true, unchecked((int)0xFFEEEEEE)), (b.WrapText, b.BackgroundArgb));
        Assert.Equal((true, true, 2), (c.IsBold, c.IsItalic, (int)c.VerticalAlign!));
        Assert.Null(g.Cols[1].StyleId);
        Assert.Null(g.Rows[1].StyleId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_перев_язує_каскад_ієрархію_рядків_і_клонує_стилі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var seed = await SeedAsync(builder);

        int cloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await CloneAsync(db, seed.Doc.TemplateVersionId, Ver());
        }

        var source = await ReadAsync(builder, seed.Doc.TemplateVersionId);
        var clone = await ReadAsync(builder, cloneId);
        AssertGraph(clone, source);

        // Джерело незмінне: його посилання — як були.
        Assert.Equal(source.Cols[1].Id, source.Cols[2].CascadeFromColumnId);
        Assert.Equal(source.Rows[0].Id, source.Rows[1].ParentRowDefId);
        Assert.Equal(seed.StyleA, source.Cols[0].StyleId);
        Assert.Equal(seed.StyleB, source.Rows[0].StyleId);
        Assert.Equal(seed.StyleC, source.Table.HeaderStyleId);
        Assert.Equal(3, source.Styles.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_клона_теж_перев_язаний_на_себе()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var seed = await SeedAsync(builder);

        int cloneId;
        int cloneOfCloneId;
        await using (var db = builder.CreateContext())
        {
            cloneId = await CloneAsync(db, seed.Doc.TemplateVersionId, Ver());
        }

        await using (var db = builder.CreateContext())
        {
            cloneOfCloneId = await CloneAsync(db, cloneId, Ver());
        }

        var first = await ReadAsync(builder, cloneId);
        var second = await ReadAsync(builder, cloneOfCloneId);
        AssertGraph(second, first);
    }
}
