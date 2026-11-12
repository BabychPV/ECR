using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// P-4 (аудит 2026-11-10): публікація шаблону — без квадратичних кроків під блоком версії (виробники комірок через
/// <c>RecalculationPlanBuilder.ProducerIndex</c>, запис <c>EvaluationOrder</c> за словником, лінійний Кан у
/// <c>TopologicalSorter</c>), і з ТИМ САМИМ порядком обчислення, що й до зміни.
/// </summary>
/// <remarks>
/// Мутації: повернути перебір <c>table.Formulas</c> × <c>FormulaOutputs.Produces</c> у <c>PublishChecks.DependsOn</c>
/// або пошук <c>SelectMany(...).FirstOrDefault</c> на кожен id — тест часу червоний; переставити кандидатів не в
/// порядку <c>TableDef.Formulas</c> чи загубити самопосилання — тест порядку червоний.
/// </remarks>
public sealed class PublishChecksScaleTests(ITestOutputHelper output)
{
    private readonly RealFormulaEngine _engine = new();

    /// <summary>
    /// Порядок обчислення на структурі з усіма областями формул (колонкова, рядкова, комірочна), посиланнями між
    /// таблицями й незалежними гілками — літерали зафіксовано прогоном на коді ДО P-4.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Порядок_і_залежності_публікації_не_змінились()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("S");
        var tables = new List<(TableDef Table, List<ColumnDef> Columns, List<RowDef> Rows)>();
        for (var t = 0; t < 3; t++)
        {
            var table = builder.Table(sheet, $"T{t}");
            var columns = new List<ColumnDef> { builder.Column(table, "A") };
            for (var c = 1; c <= 3; c++)
            {
                columns.Add(builder.Column(table, $"F{c}"));
            }

            var rows = Enumerable.Range(1, 4).Select(r => builder.Row(table, $"R{r}", r)).ToList();
            tables.Add((table, columns, rows));
        }

        var formulas = new List<FormulaDef>();

        // T0: комірочні ланцюжки F1 ← A, F2 ← F1, у зворотному порядку оголошення (щоб порядок не збігався з id).
        var (t0, c0, r0) = tables[0];
        foreach (var row in r0)
        {
            formulas.Add(builder.Formula(t0, "[F1] * 2", FormulaScope.Cell, c0[2], row));
        }

        foreach (var row in r0)
        {
            formulas.Add(builder.Formula(t0, "[A] + 1", FormulaScope.Cell, c0[1], row));
        }

        // T0: колонкова F3 ← F2.
        formulas.Add(builder.Formula(t0, "[F2] + [A]", FormulaScope.Column, c0[3]));

        // T1: колонкові, перша читає T0 між таблицями.
        var (t1, c1, _) = tables[1];
        formulas.Add(builder.Formula(t1, "[T0].[R2].[F3] + [T0].[R1].[F2]", FormulaScope.Column, c1[1]));
        formulas.Add(builder.Formula(t1, "[F1] - 1", FormulaScope.Column, c1[2]));

        // T2: незалежна гілка й гілка, що читає T1.
        var (t2, c2, r2) = tables[2];
        formulas.Add(builder.Formula(t2, "[T1].[R1].[F2] * 3", FormulaScope.Cell, c2[3], r2[0]));
        formulas.Add(builder.Formula(t2, "7", FormulaScope.Column, c2[1]));
        formulas.Add(builder.Formula(t2, "[F1] + [A]", FormulaScope.Cell, c2[2], r2[1]));

        // T3: рядкова область (весь рядок R1), що читає T1.
        var t3 = builder.Table(sheet, "T3");
        builder.Column(t3, "A");
        builder.Column(t3, "B");
        var r3 = builder.Row(t3, "R1", 1);
        formulas.Add(builder.Formula(t3, "[T1].[R3].[F2] + 1", FormulaScope.Row, row: r3));

        var version = builder.Version();

        var diagnostics = Run(version);
        Assert.Empty(diagnostics);

        var order = formulas.OrderBy(f => f.EvaluationOrder).Select(f => formulas.IndexOf(f)).ToList();
        output.WriteLine(string.Join(", ", order));

        Assert.Equal(ExpectedOrder, order);
    }

    /// <summary>
    /// 10 таблиць × 200 рядків × 10 комірочних формул (<c>[F{c-1}]</c> того ж рядка) = 20 000 формул. До P-4 —
    /// квадратичні кроки (виробники × формули таблиці, запис порядку, Кан) під блоком версії.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Публікація_20000_формул_не_квадратична()
    {
        const int tableCount = 10;
        const int rowCount = 200;
        const int formulaColumns = 10;

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("S");
        for (var t = 0; t < tableCount; t++)
        {
            var table = builder.Table(sheet, $"T{t}");
            var columns = new List<ColumnDef> { builder.Column(table, "F0") };
            for (var c = 1; c <= formulaColumns; c++)
            {
                columns.Add(builder.Column(table, $"F{c}"));
            }

            for (var r = 0; r < rowCount; r++)
            {
                var row = builder.Row(table, $"R{r}", r + 1);
                for (var c = 1; c <= formulaColumns; c++)
                {
                    builder.Formula(table, $"[F{c - 1}] + 1", FormulaScope.Cell, columns[c], row);
                }
            }
        }

        var version = builder.Version();

        // Прогрів (JIT) входить у перший замір; найкращий із двох.
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 2; i++)
        {
            var watch = Stopwatch.StartNew();
            var diagnostics = Run(version);
            watch.Stop();
            Assert.Empty(diagnostics);
            best = watch.Elapsed < best ? watch.Elapsed : best;
            output.WriteLine($"PublishChecks.Run, {tableCount * rowCount * formulaColumns} формул: {watch.Elapsed.TotalMilliseconds:F0} мс");
        }

        Assert.True(
            best < TimeSpan.FromSeconds(5),
            $"Перевірка публікації {tableCount * rowCount * formulaColumns} формул тривала {best.TotalMilliseconds:F0} мс "
            + "(бюджет 5 с; квадратичні кроки — хвилини під блоком версії).");
    }

    /// <summary>Порядок формул (індекс у переліку оголошення) за <c>EvaluationOrder</c>, зафіксований до P-4.</summary>
    private static readonly int[] ExpectedOrder = [4, 5, 6, 7, 8, 12, 0, 1, 2, 3, 13, 9, 10, 11, 14];

    private IReadOnlyList<ExpressionDiagnostic> Run(TemplateVersion version)
    {
        var snapshot = PublishChecks.Snapshot(version);
        return PublishChecks.Run(
            version,
            _engine,
            new SnapshotTypeContext(snapshot),
            new SnapshotUnitContext(snapshot, UnitCatalogSnapshot.Empty));
    }
}
