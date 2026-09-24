// tests/Ecr.Application.Tests/Templates/FormulaTargetConflictTests.cs
using Ecr.Application.Templates;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Публікація відхиляє дві формули, що обчислюють одну комірку (V-03).
/// </summary>
/// <remarks>
/// ⛔ До виправлення жодна перевірка публікації не дивилася на ЦІЛІ формул:
/// рядкова <c>RTOT</c> і колонкова <c>CFRM</c> публікувалися, а перерахунок
/// падав на <c>PRIMARY KEY … dbo.@cells</c> (рушійна половина доведена на
/// справжньому SQL у <c>RowFormulaTargetTests</c>). Тут — лише структура:
/// перевірка не торкається бази.
/// </remarks>
public sealed class FormulaTargetConflictTests
{
    private readonly RealFormulaEngine _engine = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Рядкова_й_колонкова_формули_в_одну_комірку_дають_зауваження_з_ключем_і_обома_адресами()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("FSHEET"), "FT2");
        var dec = builder.Column(table, "CDEC");
        var frm = builder.Column(table, "CFRM", CellDataType.Formula);
        builder.Row(table, "R1");
        builder.Row(table, "R2");
        var total = builder.Row(table, "RTOT");
        builder.Formula(table, "[R1].[CDEC] + [R2].[CDEC]", FormulaScope.Row, row: total);
        builder.Formula(table, "[CDEC] * 2", FormulaScope.Column, column: frm);
        _ = dec;

        var diagnostic = Assert.Single(FormulaTargetConflicts.Check(builder.Version(), _engine));

        Assert.Equal(FormulaTargetConflicts.MessageKey, diagnostic.MessageKey);
        Assert.Equal("RTOT·*", diagnostic.MessageParams!["first"]);
        Assert.Equal("*·CFRM", diagnostic.MessageParams["second"]);
        Assert.Equal("RTOT·CFRM", diagnostic.MessageParams["cell"]);
        Assert.Equal("FT2", diagnostic.MessageParams["table"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Колонка_куди_рядкова_формула_не_пише_конфлікту_не_утворює()
    {
        // ⚠ Контроль межі (V-04): числова формула рядка в текстову колонку не
        // пише, тож колонкова формула там — не суперник. Без урахування типу
        // перевірка відхиляла б законну конфігурацію.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("FSHEET"), "FT2");
        builder.Column(table, "CDEC");
        var text = builder.Column(table, "CSTR", CellDataType.String);
        builder.Row(table, "R1");
        var total = builder.Row(table, "RTOT");
        builder.Formula(table, "[R1].[CDEC] + 1", FormulaScope.Row, row: total);
        builder.Formula(table, "'x'", FormulaScope.Column, column: text);

        Assert.Empty(FormulaTargetConflicts.Check(builder.Version(), _engine));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Формули_різних_колонок_не_конфліктують()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("FSHEET"), "FT2");
        var a = builder.Column(table, "CA", CellDataType.Formula);
        var b = builder.Column(table, "CB", CellDataType.Formula);
        builder.Row(table, "R1");
        builder.Formula(table, "1", FormulaScope.Column, column: a);
        builder.Formula(table, "2", FormulaScope.Column, column: b);

        Assert.Empty(FormulaTargetConflicts.Check(builder.Version(), _engine));
    }
}
