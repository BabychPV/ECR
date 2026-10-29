// tests/Ecr.Infrastructure.Tests/Jobs/FormulaRecalculationDocumentPassQueueTests.cs
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2 (recalc-latency): задача формул — ОДНА НА ДОКУМЕНТ. Ключ без періоду, період несе кожна комірка масиву
/// <c>cells</c>; злиття об'єднує періоди; виконувана задача не губить нових періодів (нова стає позаду).
/// </summary>
/// <remarks>
/// Мутації: (1) <c>FormulaRecalculationTarget.OfDocument</c> повертає ключ із періодом — червоні «ключ» і «злиття»;
/// (2) <c>RecalculationSeed.Stamp</c> не пише <c>PeriodKey</c> — червоні «штамп» і «період кожної комірки»;
/// (3) <c>RecalculationSharedLoad</c> не запам'ятовує — червоний «читається раз».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class FormulaRecalculationDocumentPassQueueTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly string FormulaCode = typeof(IFormulaRecalculationJob).FullName!;
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static string Target(long documentId, int? user = null)
        => $"{nameof(IFormulaRecalculationJob)}~{FormulaRecalculationTarget.Of(documentId, periodKey: 202601, user)}";

    // ── ключ ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Ключ_один_на_документ_і_автора_а_період_у_ключ_не_входить()
    {
        Assert.Equal("doc7-formula", FormulaRecalculationTarget.Of(7, 202603, createdByUserId: null));
        Assert.Equal(
            FormulaRecalculationTarget.Of(7, 202603, 5),
            FormulaRecalculationTarget.Of(7, 202611, 5));
        Assert.Equal("doc7-formula-u5", FormulaRecalculationTarget.Of(7, 202603, 5));

        // Автор лишається межею доступу (Q-156): інший автор / інший документ — інша ціль.
        Assert.NotEqual(FormulaRecalculationTarget.Of(7, 202603, 5), FormulaRecalculationTarget.Of(7, 202603, 6));
        Assert.NotEqual(FormulaRecalculationTarget.Of(7, 202603, 5), FormulaRecalculationTarget.Of(8, 202603, 5));
        Assert.NotEqual(FormulaRecalculationTarget.Of(7, 202603, 5), FormulaRecalculationTarget.Of(7, 202603, null));
    }

    // ── штамп періоду ────────────────────────────────────────────────────────

    [Fact]
    public void Штамп_пише_період_і_екземпляр_у_кожну_комірку_і_зберігає_позначку_погоджувача()
    {
        var stamped = RecalculationSeed.Stamp(
            [new RecalculationSeed(10, 1), new RecalculationSeed(11, 2, ApproverSheetDefId: 9)], 202605, 77);

        Assert.All(stamped, s =>
        {
            Assert.Equal(202605, s.PeriodKey);
            Assert.Equal(77L, s.TableInstanceId);
        });
        Assert.Equal(9, stamped[1].ApproverSheetDefId);

        // Вже проставлене не перетирається (злиття різних екземплярів).
        var again = RecalculationSeed.Stamp(stamped, 202699, 5);
        Assert.Equal(202605, again[0].PeriodKey);
    }

    [Fact]
    public void Штамп_читається_задачею_як_період_комірки_а_нештамповане_лишається_без_поля()
    {
        var json = JsonSerializer.Serialize(new
        {
            DocumentId = 1L,
            TableInstanceId = 5L,
            PeriodKey = 202601,
            Cells = RecalculationSeed.Stamp([new RecalculationSeed(10, 1)], 202605, 77),
        });
        var request = JsonSerializer.Deserialize<FormulaRecalculationRequest>(json, WebOptions)!;

        var cell = Assert.Single(request.Cells);
        Assert.Equal(202605, cell.PeriodKey);
        Assert.Equal(77L, cell.TableInstanceId);

        // Форма без штампа (старий формат) — без полів, період береться з тіла.
        Assert.DoesNotContain("periodKey", JsonSerializer.Serialize(new RecalculationSeed(10, 1)), StringComparison.Ordinal);
    }

    // ── злиття ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Правки_різних_періодів_одного_документа_зливаються_в_одну_задачу_з_усіма_періодами()
    {
        await using var host = NewHost();
        var target = Target(1, user: 5);

        var first = (await host.Queue.EnqueueAsync(Formula(target, 202603, rowId: 1), CancellationToken.None)).JobId;
        var second = (await host.Queue.EnqueueAsync(Formula(target, 202605, rowId: 2), CancellationToken.None)).JobId;
        var third = (await host.Queue.EnqueueAsync(Formula(target, 202607, rowId: 3), CancellationToken.None)).JobId;

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(
            [(202603, 1L), (202605, 2L), (202607, 3L)],
            CellsOf((await RowAsync(first))?.Payload));
    }

    [Fact]
    public async Task Виконувана_задача_документа_не_губить_нового_періоду_той_стає_позаду_і_не_стартує_раніше()
    {
        await using var host = NewHost();
        var target = Target(1);

        var running = (await host.Queue.EnqueueAsync(Formula(target, 202603, rowId: 1), CancellationToken.None)).JobId;
        var claim = await host.ClaimAsync();
        Assert.Equal(running, claim?.Claim.JobId);

        // Поки період 3 рахується — правка періоду 5 стає Queued ПОЗАДУ; claim її не бере (ціль зайнята).
        var behind = (await host.Queue.EnqueueAsync(Formula(target, 202605, rowId: 2), CancellationToken.None)).JobId;
        Assert.NotEqual(running, behind);
        Assert.Null(await host.ClaimAsync());

        // Ще одна правка (період 7), доки задача позаду не стартувала: зливається В НЕЇ, а не в виконувану.
        var merged = (await host.Queue.EnqueueAsync(Formula(target, 202607, rowId: 3), CancellationToken.None)).JobId;
        Assert.Equal(behind, merged);
        Assert.Equal([(202603, 1L)], CellsOf((await RowAsync(running))?.Payload));
        Assert.Equal([(202605, 2L), (202607, 3L)], CellsOf((await RowAsync(behind))?.Payload));

        // Виконувана завершилась — задача позаду береться з УСІМА періодами, нічого не втрачено.
        Assert.True(await host.Queue.CompleteAsync(claim!.Claim, CancellationToken.None));
        var next = await host.ClaimAsync();
        Assert.Equal(behind, next?.Claim.JobId);
        Assert.Equal([(202605, 2L), (202607, 3L)], CellsOf(next!.PayloadJson));
    }

    [Fact]
    public async Task Задачі_різних_документів_не_зливаються()
    {
        await using var host = NewHost();

        var one = (await host.Queue.EnqueueAsync(Formula(Target(1), 202603, rowId: 1), CancellationToken.None)).JobId;
        var other = (await host.Queue.EnqueueAsync(Formula(Target(2), 202603, rowId: 1), CancellationToken.None)).JobId;

        Assert.NotEqual(one, other);
    }

    // ── спільні читання ──────────────────────────────────────────────────────

    [Fact]
    public async Task Спільні_читання_проходу_читаються_раз_на_період_і_версію_а_не_раз_на_виклик()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(1, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<TableInstanceRef>>(
                [new TableInstanceRef(100 + call.ArgAt<PeriodKey>(1).Value, 1, 4, 9, call.ArgAt<PeriodKey>(1).Value)]));
        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(9, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FormulaDependency>>([]));

        var shared = new RecalculationSharedLoad(rows, versions);
        foreach (var _ in Enumerable.Range(0, 6))
        {
            foreach (var period in new[] { 202603, 202604, 202605 })
            {
                await shared.InstancesAsync(1, new PeriodKey(period), CancellationToken.None);
            }

            await shared.DependenciesAsync(9, CancellationToken.None);
        }

        Assert.Equal(3, shared.InstanceReads);
        Assert.Equal(1, shared.DependencyReads);
    }

    // ── світ ─────────────────────────────────────────────────────────────────

    private static JobEnqueueRequest Formula(string target, int period, long rowId)
        => new(
            FormulaCode, JobLanes.Default,
            $$"""{"documentId":1,"tableInstanceId":1,"periodKey":{{period}},"cells":[{"rowId":{{rowId}},"columnDefId":7,"periodKey":{{period}}}]}""",
            target);

    private static List<(int Period, long RowId)> CellsOf(string? payload)
    {
        using var json = JsonDocument.Parse(payload!);

        return [.. json.RootElement.GetProperty("cells").EnumerateArray()
            .Select(c => (c.GetProperty("periodKey").GetInt32(), c.GetProperty("rowId").GetInt64()))
            .Order()];
    }
}
