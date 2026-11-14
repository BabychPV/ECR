// tests/Ecr.Application.Tests/Security/DocumentReadScopeStructureTests.cs
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// P-6 (аудит 2026-11-10): структурні мапи меж читання залежать лише від знімка шаблону й беруться з кешу за знімком —
/// створення меж і <see cref="DocumentReadScope.InPeriod"/> більше не обходять усі аркуші, таблиці й колонки.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: поверніть цикл побудови мап у конструктор <c>DocumentReadScope</c> — 200 викликів
/// <c>InPeriod</c> на знімку 50 таблиць × 40 колонок алокують десятки мегабайт, стеля 1 МБ червоніє.
/// </remarks>
public sealed class DocumentReadScopeStructureTests
{
    private const int Project = AccessBuilder.ProjectId;
    private const int Tables = 50;
    private const int Columns = 40;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P-6")]
    public void InPeriod_не_перебудовує_структуру_знімка()
    {
        var (snapshot, _) = Build();
        var profile = Reader().Build();
        var scope = DocumentReadScope.For(profile, Project, snapshot);   // прогрів: структура збудована й закешована
        var period = new PeriodKey(202601);
        _ = scope.InPeriod(period);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            _ = scope.InPeriod(period);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1_000_000, $"200 викликів InPeriod алокували {allocated} байт: структура знімка будується заново.");
    }

    /// <summary>Спільна структура не змінює відповідей: заборона одного користувача не протікає в межі іншого.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P-6")]
    [Trait("Requirement", "ФВ-6.6")]
    public void Спільна_структура_дає_кожному_профілю_власні_рішення()
    {
        var (snapshot, ids) = Build();
        var (deniedTable, deniedColumn, otherColumn) = ids;

        var open = DocumentReadScope.For(Reader().Build(), Project, snapshot);
        var closed = DocumentReadScope.For(
            Reader().Deny(ResourceKind.Table, deniedTable).Build(), Project, snapshot);
        var openAgain = DocumentReadScope.For(Reader().Build(), Project, snapshot);

        Assert.True(open.CanReadTable(deniedTable));
        Assert.True(open.CanReadColumn(deniedColumn));
        Assert.False(closed.CanReadTable(deniedTable));
        Assert.False(closed.CanReadColumn(deniedColumn));
        Assert.True(closed.CanReadColumn(otherColumn));

        // Межі, створені ПІСЛЯ меж із забороною, бачать усе: спільні лише структурні мапи, рішення — власні.
        Assert.True(openAgain.CanReadTable(deniedTable));
        Assert.True(openAgain.CanReadColumn(deniedColumn));
        Assert.Equal(Tables, openAgain.TableIds().Count);
        Assert.Equal(Tables * Columns, openAgain.VisibleColumnIds().Count);
        Assert.Equal((Tables - 1) * Columns, closed.VisibleColumnIds().Count);
    }

    /// <summary>Знімок, добудований після першого виклику (так роблять тести), отримує нову структуру.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P-6")]
    public void Добудований_знімок_не_лишається_зі_старою_структурою()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 7 };
        var sheet = builder.Sheet("S");
        var first = builder.Table(sheet, "T1");
        builder.Column(first, "A");
        var snapshot = builder.Build();
        var profile = Reader().Build();

        Assert.Single(DocumentReadScope.For(profile, Project, snapshot).TableIds());

        var second = builder.Table(sheet, "T2");
        builder.Column(second, "B");

        // Той самий екземпляр знімка (аркуш спільний), але таблиць уже дві — підпис структури змінився.
        Assert.Equal([first.Id, second.Id], DocumentReadScope.For(profile, Project, snapshot).TableIds());
    }

    private static AccessBuilder Reader()
        => new AccessBuilder().Grant(ResourceKind.Project, Project, GrantLevel.Read);

    private static (TemplateVersionSnapshot Snapshot, (int Table, int Column, int OtherColumn) Ids) Build()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 5 };
        var sheet = builder.Sheet("S");
        int table0 = 0, column0 = 0, other = 0;
        for (var t = 0; t < Tables; t++)
        {
            var table = builder.Table(sheet, $"T{t}");
            for (var c = 0; c < Columns; c++)
            {
                var column = builder.Column(table, $"C{c}");
                if (t == 0 && c == 0)
                {
                    (table0, column0) = (table.Id, column.Id);
                }
                else if (t == 1 && c == 0)
                {
                    other = column.Id;
                }
            }
        }

        return (builder.Build(), (table0, column0, other));
    }
}
