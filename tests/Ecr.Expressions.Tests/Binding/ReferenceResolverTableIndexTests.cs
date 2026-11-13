using System.Collections;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// ✎ B13 (аудит P-1): <see cref="ReferenceResolver"/> будує перелік таблиць знімка ОДИН раз, а не на кожне посилання,
/// і резолвить так само, як доти (власна таблиця, код у власному аркуші, код з аркушем, невідома таблиця, регістр).
/// </summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально): повернути в <c>FindTable</c> <c>snapshot.Sheets.SelectMany(...).ToList()</c> на кожен виклик —
/// <see cref="Перелік_таблиць_знімка_будується_один_раз_на_резолвер"/> червоний (200 обходів аркушів замість 1).
/// </remarks>
public sealed class ReferenceResolverTableIndexTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "P-1")]
    public void Перелік_таблиць_знімка_будується_один_раз_на_резолвер()
    {
        var (snapshot, _, mainA, _) = Arrange();
        var counting = new CountingList<SheetDef>(snapshot.Sheets);
        var resolver = new ReferenceResolver(snapshot with { Sheets = counting });

        for (var i = 0; i < 100; i++)
        {
            Assert.NotNull(resolver.Resolve(Ref(null, "Main", "C1"), mainA.Id, "R1"));
            Assert.NotNull(resolver.Resolve(Ref("SB", "Main", "C1"), mainA.Id, "R1"));
        }

        Assert.Equal(1, counting.Enumerations);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "P-1")]
    public void Резолвінг_таблиць_той_самий_власна_свій_аркуш_чужий_аркуш_невідома()
    {
        var (snapshot, sheetB, mainA, mainB) = Arrange();
        var resolver = new ReferenceResolver(snapshot);

        // Без коду таблиці — власна таблиця формули.
        Assert.Equal(mainA.Id, resolver.Resolve(Ref(null, null, "C1"), mainA.Id, "R1")!.TableDefId);

        // Код без аркуша — таблиця з таким кодом у ВЛАСНОМУ аркуші формули (обидва аркуші мають «Main»), без регістру.
        Assert.Equal(mainA.Id, resolver.Resolve(Ref(null, "main", "C1"), mainA.Id, "R1")!.TableDefId);
        Assert.Equal(mainB.Id, resolver.Resolve(Ref(null, "Main", "C1"), mainB.Id, "R1")!.TableDefId);

        // Код з аркушем — таблиця названого аркуша.
        Assert.Equal(mainB.Id, resolver.Resolve(Ref(sheetB.Code, "Main", "C1"), mainA.Id, "R1")!.TableDefId);
        Assert.Equal(mainB.Id, resolver.Resolve(Ref("sb", "MAIN", "C1"), mainA.Id, "R1")!.TableDefId);

        // Таблиця лише в іншому аркуші без коду аркуша — не знайдена (як і доти).
        var diagnostics = new List<ExpressionDiagnostic>();
        Assert.Null(resolver.Resolve(Ref(null, "OnlyB", "C1"), mainA.Id, "R1", diagnostics));
        Assert.Single(diagnostics);

        // Невідома таблиця і невідома власна таблиця — зауваження, null.
        diagnostics.Clear();
        Assert.Null(resolver.Resolve(Ref(null, "Nope", "C1"), mainA.Id, "R1", diagnostics));
        Assert.Null(resolver.Resolve(Ref(null, null, "C1"), currentTableDefId: 99_999, "R1", diagnostics));
        Assert.Equal(2, diagnostics.Count);
    }

    private static CellReferenceNode Ref(string? sheet, string? table, string column)
        => new(sheet, table, new RowSelector.Current(), column, 0);

    private static (TemplateVersionSnapshot Snapshot, SheetDef SheetB, TableDef MainA, TableDef MainB) Arrange()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheetA = builder.Sheet("SA");
        var mainA = builder.Table(sheetA, "Main");
        builder.Column(mainA, "C1");
        builder.Row(mainA, "R1");

        var sheetB = builder.Sheet("SB");
        var mainB = builder.Table(sheetB, "Main");
        builder.Column(mainB, "C1");
        builder.Row(mainB, "R1");
        var onlyB = builder.Table(sheetB, "OnlyB");
        builder.Column(onlyB, "C1");

        return (builder.Build(), sheetB, mainA, mainB);
    }

    /// <summary>Список, що рахує обходи (кожен <c>GetEnumerator</c>).</summary>
    private sealed class CountingList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public int Enumerations { get; private set; }

        public int Count => inner.Count;

        public T this[int index] => inner[index];

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
