// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.AnnualRecheck.cs
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ B79 (Q9): записувачі історії дозволу (додати / скасувати дозвіл рядка, частини поділу місяця) беруть те саме
/// рішення про комірку, що й PATCH, але пишуть не через <c>PatchCellsHandler</c>. Річне рішення вони тепер повторюють
/// під замками аркушів року: місяць, затверджений МІЖ рішенням і записом, запис відхиляє.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Справжній HTTP-шлях; «затвердження іншим користувачем» вклинюється детерміновано — обгортка служби доступу одразу
/// ПІСЛЯ <c>CanEditCellAsync</c> комітить стан аркуша окремим з'єднанням. Таблиця фікстури стає «місяці в колонках», а
/// маршрутизація вмикається змінною оточення лише на час старту свого хоста (правило 394).
/// </para>
/// <para>
/// ⛔ МУТАЦІЇ (локально): прибрати <c>row.EnsureStillEditableAsync</c> з <c>AddRowPermitHandler</c> → «Додавання…»
/// (обидва рядки); з <c>UndoRowPermitHandler</c> → «Скасування…»; цикл <c>AnnualDecisionRecheck.EnsureAsync</c> із
/// <c>SaveCellSplitsHandler</c> → «Частини_без_суми…»; у <c>AnnualDecisionRecheck</c> гілку <c>DependentInput</c> →
/// «Додавання…(true)» дає <c>DocumentApproved</c> замість <c>DependentMonthsLocked</c>.
/// </para>
/// </remarks>
public sealed partial class DocumentPermitHistoryTests
{
    private const string RoutingFlag = "ECR_Collection__MonthColumnRouting";
    private const int January = 202601;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(false, "DocumentApproved")]
    [InlineData(true, "DependentMonthsLocked")]
    public async Task Додавання_дозволу_рядка_місяць_затверджено_між_рішенням_і_записом_відмова(bool dependent, string reason)
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var ids = await MakeAnnualAsync(s, dependent).ConfigureAwait(true);
        var race = new DecisionRace { AfterDecision = () => ApproveJanuaryAsync(s, ids) };
        using var app = new EcrApiFactory(sql);
        using var racing = RacingDecision(app, race);
        using var client = await SignedInRoutedAsync(racing, s.UserName).ConfigureAwait(true);

        var response = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "обгортка служби доступу не вклинилась");
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(reason, root.GetProperty("reason").GetString());
        Assert.Equal(dependent, root.TryGetProperty("months", out var months));
        if (dependent)
        {
            Assert.Equal(new[] { January }, months.EnumerateArray().Select(m => m.GetInt32()));
        }

        Assert.Equal(0, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Контроль: та сама річна таблиця без затвердження між рішенням і записом — дозвіл додається.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Додавання_дозволу_рядка_без_затвердження_між_рішенням_і_записом_проходить()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        await MakeAnnualAsync(s, dependent: true).ConfigureAwait(true);
        var race = new DecisionRace();
        using var app = new EcrApiFactory(sql);
        using var racing = RacingDecision(app, race);
        using var client = await SignedInRoutedAsync(racing, s.UserName).ConfigureAwait(true);

        var response = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(1, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Скасування_дозволу_рядка_місяць_затверджено_між_рішенням_і_записом_відмова_сегмент_лишається()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var ids = await MakeAnnualAsync(s, dependent: false).ConfigureAwait(true);
        var race = new DecisionRace();
        using var app = new EcrApiFactory(sql);
        using var racing = RacingDecision(app, race);
        using var client = await SignedInRoutedAsync(racing, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        race.AfterDecision = () => ApproveJanuaryAsync(s, ids);
        var undo = await client.PostAsJsonAsync(
            new Uri($"{Base(s)}/rows/undo", UriKind.Relative),
            new { tableInstanceId = s.TableInstanceId, rowKey = s.RowKey, columnCode = s.PermitColumnCode }).ConfigureAwait(true);
        var body = await undo.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "обгортка служби доступу не вклинилась");
        Assert.True(undo.StatusCode == HttpStatusCode.Forbidden, $"{undo.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("DocumentApproved", JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString());
        Assert.Equal(1, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Частини без суми (jh3) не йдуть через PATCH: без повторної перевірки їх не зупинило б ніщо.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Частини_без_суми_місяць_затверджено_між_рішенням_і_записом_відмова_частин_немає()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var ids = await MakeAnnualAsync(s, dependent: false).ConfigureAwait(true);
        var race = new DecisionRace();
        using var app = new EcrApiFactory(sql);
        using var racing = RacingDecision(app, race);
        using var client = await SignedInRoutedAsync(racing, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        race.AfterDecision = () => ApproveJanuaryAsync(s, ids);
        var split = await SplitAsync(client, s, 5m, null).ConfigureAwait(true);
        var body = await split.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "обгортка служби доступу не вклинилась");
        Assert.True(split.StatusCode == HttpStatusCode.Forbidden, $"{split.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("DocumentApproved", JsonDocument.Parse(body).RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, await PartCountAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record AnnualIds(int SheetDefId, int TableDefId);

    /// <summary>
    /// Робить таблицю фікстури річною («місяці в колонках»; календар року — один січень 2026) і, за потреби, додає
    /// формулу, що читає колонку дозволу (стовпець із залежними розрахунками).
    /// </summary>
    private async Task<AnnualIds> MakeAnnualAsync(Scenario s, bool dependent)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var tableDefId = await db.TableInstances.AsNoTracking()
            .Where(i => i.PeriodKeyValue == January && i.Id == s.TableInstanceId)
            .Select(i => i.TableDefId).SingleAsync().ConfigureAwait(false);
        var sheetDefId = await db.TableDefs.AsNoTracking()
            .Where(t => t.Id == tableDefId).Select(t => t.SheetDefId).SingleAsync().ConfigureAwait(false);

        await db.TableDefs.Where(t => t.Id == tableDefId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.LayoutKind, TableLayoutKind.MonthsInColumns)).ConfigureAwait(false);

        if (dependent)
        {
            var permitColumnId = (await db.ColumnDefs.AsNoTracking()
                    .Where(c => c.TableDefId == tableDefId).ToListAsync().ConfigureAwait(false))
                .Single(c => string.Equals(c.Code, s.PermitColumnCode, StringComparison.Ordinal)).Id;
            var formula = new FormulaDef(tableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
            formula.AssignColumn(s.DataColumnId);
            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.FormulaDependencies.Add(FormulaDependency.ForFormula(
                formula.Id, dependsOnKind: 0, tableDefId, rowKey: null, permitColumnId,
                filterJson: null, periodOffset: null, sortOrder: 0));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new AnnualIds(sheetDefId, tableDefId);
    }

    /// <summary>«Інший користувач» затверджує аркуш січня — окремим з'єднанням, у точці після рішення.</summary>
    private async Task ApproveJanuaryAsync(Scenario s, AnnualIds ids)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var state = new ApprovalState(s.DocumentId, ids.SheetDefId, January);
        state.Submit(s.UserId, DateTime.UtcNow.AddMinutes(-2));
        state.Approve(s.UserId, DateTime.UtcNow.AddMinutes(-1));
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Вхід із маршрутизацією місячних стовпців: змінна оточення діє лише на час старту хоста.</summary>
    private static async Task<HttpClient> SignedInRoutedAsync(WebApplicationFactory<Program> app, string userName)
    {
        Environment.SetEnvironmentVariable(RoutingFlag, "true");
        try
        {
            return await SignedInAsync(app, userName).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RoutingFlag, null);
        }
    }

    /// <summary>Хост, у якому служба доступу загорнута: після <c>CanEditCellAsync</c> спрацьовує <paramref name="race"/>.</summary>
    private static WebApplicationFactory<Program> RacingDecision(EcrApiFactory app, DecisionRace race)
        => app.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            var registered = services.Last(d => d.ServiceType == typeof(IAccessDecisionService));
            services.Remove(registered);
            services.AddScoped(sp =>
            {
                var inner = (IAccessDecisionService)(registered.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!));
                return RacingAccess.Wrap(inner, race);
            });
        }));

    /// <summary>Одноразова дія в точці «рішення вже прийнято, транзакція запису ще не почалась».</summary>
    private sealed class DecisionRace
    {
        private int _fired;

        public Func<Task>? AfterDecision { get; set; }

        public bool Fired => Volatile.Read(ref _fired) > 0;

        public async Task FireAsync()
        {
            if (AfterDecision is { } action)
            {
                AfterDecision = null;
                Interlocked.Increment(ref _fired);
                await action().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Прозора обгортка служби доступу: усе — справжній службі; після рішення про комірку — гонка.</summary>
    public class RacingAccess : DispatchProxy
    {
        private IAccessDecisionService _inner = null!;
        private DecisionRace _race = null!;

        internal static IAccessDecisionService Wrap(IAccessDecisionService inner, object race)
        {
            var proxy = Create<IAccessDecisionService, RacingAccess>();
            var self = (RacingAccess)(object)proxy;
            self._inner = inner;
            self._race = (DecisionRace)race;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var result = targetMethod.Invoke(_inner, args);

            return targetMethod.Name == nameof(IAccessDecisionService.CanEditCellAsync) && result is Task<EditDecision> decision
                ? ThenRaceAsync(decision)
                : result;
        }

        private async Task<EditDecision> ThenRaceAsync(Task<EditDecision> decision)
        {
            var value = await decision.ConfigureAwait(false);
            await _race.FireAsync().ConfigureAwait(false);
            return value;
        }
    }
}
