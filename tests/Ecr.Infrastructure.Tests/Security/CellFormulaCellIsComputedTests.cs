using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// WW-02: комірка з КОМІРОЧНОЮ формулою (<c>FormulaScope.Cell</c>) на колонці ручного вводу не редагується
/// (<see cref="EditDenyReason.CalculatedCell"/>), а решта комірок її рядка й колонки лишається вводом.
/// Справжній SQL, знімок версії (формули з <c>MetadataCache</c>) і <c>AccessDecisionService</c>.
/// </summary>
/// <remarks>
/// ⚠ Доказ «падає без фіксу»: до WW-02 рішення читало лише <c>ColumnDef.IsComputed</c> і <c>RowDef.IsReadOnly</c>,
/// тож комірка з комірочною формулою на колонці Decimal лишалась редагованою (перерахунок затирав введене),
/// а єдиний спосіб її захистити — зробити весь рядок «лише читання» (закривав комірки вводу того ж рядка).
/// </remarks>
[Collection("SqlServer")]
public sealed class CellFormulaCellIsComputedTests(SqlServerFixture sql) : IDisposable
{
    private const int PeriodKeyValue = 202611;

    private static readonly DateTime Now = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-02")]
    public async Task Комірка_з_комірочною_формулою_не_редагується_а_сусідні_комірки_рядка_й_колонки_так()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 3, rowCount: 3, ct: CancellationToken.None);

        // Формула комірки (рядок 0 × колонка C2): колонка C2 - Decimal, тобто колонка вводу.
        await using (var db = builder.CreateContext())
        {
            var formula = new FormulaDef(doc.TableDefId, FormulaScope.Cell, "1+1", ExpressionDialect.Template);
            formula.AssignCell(doc.RowDefIds[0], doc.ColumnDefIds[1]);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync(CancellationToken.None);

            var period = await db.Periods.FirstAsync(
                p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue, CancellationToken.None);
            period.AdvanceTo(PeriodState.Open, Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var profile = new AccessBuilder().Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write).Build();

        var computed = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        var sameRowOtherColumn = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[2]);
        var sameColumnOtherRow = new CellAddress(doc.PeriodKey, doc.RowIds[1], doc.ColumnDefIds[1]);
        var addresses = new[] { computed, sameRowOtherColumn, sameColumnOtherRow };

        await using var context = builder.CreateContext();
        var service = new AccessDecisionService(
            context, new MetadataCache(_memory, context), new AccessProfileCache(_memory),
            new TestClock(Now), Substitute.For<ICurrentUser>(), new WorkflowStore(context));

        // PATCH-шлях (адреси, що пише користувач).
        var cells = await service.CanEditCellsAsync(
            profile, doc.TableInstanceId, doc.PeriodKey, addresses, CancellationToken.None);

        Assert.False(cells[computed].IsAllowed);
        Assert.Equal(EditDenyReason.CalculatedCell, cells[computed].Reason);
        Assert.True(cells[sameRowOtherColumn].IsAllowed, "сусідня комірка того ж рядка редагується");
        Assert.True(cells[sameColumnOtherRow].IsAllowed, "та сама колонка в іншому рядку редагується");

        // Зріз (прапори для клієнта) - те саме рішення.
        var slice = await service.CanEditSliceAsync(profile, doc.TableInstanceId, CancellationToken.None);
        Assert.Equal(EditDenyReason.CalculatedCell, slice[computed].Reason);
        Assert.True(slice[sameRowOtherColumn].IsAllowed);
        Assert.True(slice[sameColumnOtherRow].IsAllowed);

        // Рядок не «лише для читання»: рішення про створення/рядок його не закриває.
        var rowDecision = (await service.CanEditCellsAsync(
            profile, doc.TableInstanceId, doc.PeriodKey,
            [new CellAddress(doc.PeriodKey, doc.RowIds[2], doc.ColumnDefIds[2])], CancellationToken.None)).Single().Value;
        Assert.True(rowDecision.IsAllowed);
    }
}
