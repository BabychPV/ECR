using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// P8, застосування імпорту 1/3: пакетні <c>CanEditCellsBatchAsync</c> і
/// <c>CanCreateRowsBatchAsync</c> дають ТІ САМІ рішення, що й поштучні
/// <c>CanEditCellsAsync</c>/<c>CanCreateRowsAsync</c> на кожен екземпляр, — на
/// тому самому строкатому наборі зрізів і профілів, що й
/// <c>CanEditSlicesAsync</c> (фікстура — <see cref="ArrangeAsync"/>).
/// </summary>
/// <remarks>
/// ⚠ Поштучні реалізовані через пакетні з одним екземпляром, тож
/// еквівалентність стереже ПАКЕТНІ місця: спільний запит рядків на кілька
/// екземплярів (адреса з рядком СУСІДНЬОЇ таблиці мусить лишитися без
/// рішення, як у поштучного), групування за запитаним періодом, порожні
/// запити.
/// </remarks>
public sealed partial class AccessDecisionBatchEquivalenceTests
{
    /// <summary>Колонка, якої немає в жодній таблиці набору.</summary>
    private const int MissingColumnDefId = 987_654_321;

    /// <summary>
    /// Екземпляр без адрес — друга таблиця поданого аркуша: стан «подано»
    /// лишається покритим першою, а закритий період — таблицею документа B.
    /// </summary>
    private const int EmptyRequestIndex = 1;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакетні_рішення_на_адреси_тотожні_поштучним_для_кожного_екземпляра_і_профілю()
    {
        var world = await ArrangeAsync();
        var requests = await CellRequestsAsync(world);

        var reasons = new HashSet<EditDenyReason>();
        var allowed = 0;

        foreach (var (name, profile) in world.Profiles)
        {
            IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>> batch;
            await using (var db = world.Builder.CreateContext())
            {
                batch = await Service(db).CanEditCellsBatchAsync(profile, requests.All, CancellationToken.None);
            }

            Assert.Equal(requests.All.Select(r => r.TableInstanceId).Order(), batch.Keys.Order());

            foreach (var request in requests.All)
            {
                IReadOnlyDictionary<CellAddress, EditDecision> single;
                await using (var db = world.Builder.CreateContext())
                {
                    single = await Service(db).CanEditCellsAsync(
                        profile, request.TableInstanceId, request.PeriodKey, request.Addresses, CancellationToken.None);
                }

                AssertSame($"профіль «{name}», екземпляр {request.TableInstanceId}", single, batch[request.TableInstanceId]);

                foreach (var decision in single.Values)
                {
                    reasons.Add(decision.Reason);
                    allowed += decision.IsAllowed ? 1 : 0;
                }
            }

            // ── Предметно: те, що пакет ділить між екземплярами ─────────────
            foreach (var foreign in requests.Foreign)
            {
                Assert.False(
                    batch[foreign.TableInstanceId].ContainsKey(foreign.Address),
                    $"профіль «{name}»: адреса з рядком сусідньої таблиці отримала рішення в екземплярі {foreign.TableInstanceId}.");
            }

            Assert.Empty(batch[requests.WrongPeriodInstanceId]);
            Assert.Empty(batch[requests.EmptyInstanceId]);
        }

        // ⛔ Проти хибнозеленого — ті самі гілки, що й у зрізів.
        Assert.True(allowed > 0, "Жодної дозволеної комірки — набір не перевіряє дозволу.");
        Assert.Contains(EditDenyReason.NoGrant, reasons);
        Assert.Contains(EditDenyReason.DocumentSubmitted, reasons);
        Assert.Contains(EditDenyReason.DocumentApproved, reasons);
        Assert.Contains(EditDenyReason.PeriodClosed, reasons);
        Assert.Contains(EditDenyReason.OutsidePermitWindow, reasons);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакетні_рішення_на_нові_рядки_тотожні_поштучним_для_кожного_екземпляра_і_профілю()
    {
        var world = await ArrangeAsync();

        // Шаблонний ключ (є `RowDef`) і вигадані — для кожного екземпляра; один
        // екземпляр — без ключів.
        var templateKeys = new Dictionary<long, string>
        {
            [world.Submitted.TableInstanceId] = world.Submitted.RowKeys[0],
            [world.Approved.TableInstanceId] = world.Approved.RowKeys[0],
            [world.Denied.TableInstanceId] = world.Denied.RowKeys[0],
        };

        var emptyInstanceId = world.InstanceIds[^1];
        var keys = world.InstanceIds.ToDictionary(
            id => id,
            IReadOnlyCollection<string> (id) => id == emptyInstanceId
                ? []
                : templateKeys.TryGetValue(id, out var key) ? [key, "P8NEW-1", "P8NEW-2"] : ["P8NEW-1", "P8NEW-2"]);

        var rowAllowed = 0;
        var columnDecisions = 0;

        foreach (var (name, profile) in world.Profiles)
        {
            IReadOnlyDictionary<long, IReadOnlyDictionary<string, NewRowAccess>> batch;
            await using (var db = world.Builder.CreateContext())
            {
                batch = await Service(db).CanCreateRowsBatchAsync(profile, keys, CancellationToken.None);
            }

            Assert.Equal(keys.Keys.Order(), batch.Keys.Order());
            Assert.Empty(batch[emptyInstanceId]);

            foreach (var (instanceId, rowKeys) in keys)
            {
                IReadOnlyDictionary<string, NewRowAccess> single;
                await using (var db = world.Builder.CreateContext())
                {
                    single = await Service(db).CanCreateRowsAsync(profile, instanceId, rowKeys, CancellationToken.None);
                }

                var batched = batch[instanceId];
                Assert.Equal(single.Keys.Order(StringComparer.Ordinal), batched.Keys.Order(StringComparer.Ordinal));

                foreach (var (rowKey, expected) in single)
                {
                    var actual = batched[rowKey];
                    var where = $"профіль «{name}», екземпляр {instanceId}, рядок {rowKey}";

                    AssertSameDecision(where, expected.Row, actual.Row);
                    Assert.Equal(expected.Columns.Keys.Order(), actual.Columns.Keys.Order());
                    foreach (var (columnId, decision) in expected.Columns)
                    {
                        AssertSameDecision($"{where}, колонка {columnId}", decision, actual.Columns[columnId]);
                        columnDecisions++;
                    }

                    rowAllowed += expected.Row.IsAllowed ? 1 : 0;
                }
            }
        }

        Assert.True(rowAllowed > 0, "Жодного дозволеного рядка — набір не перевіряє дозволу.");
        Assert.True(columnDecisions > 0, "Жодного рішення на колонку.");
    }

    /// <summary>
    /// Храповик: пакетні рішення про запис коштують ОДНАКОВО на 3 і на 12
    /// таблицях одного документа.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакетні_рішення_про_запис_коштують_сталу_кількість_звернень_на_3_і_12_таблиць()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 2, ct: CancellationToken.None);
        var extra = await MultiTableDocument.AddTablesAsync(builder, doc, [3, 3, 3, 2], ct: CancellationToken.None);

        var twelve = new List<long> { doc.TableInstanceId };
        twelve.AddRange(extra.Select(t => t.TableInstanceId));
        Assert.Equal(12, twelve.Count);

        // ⚠ Три таблиці на ТРЬОХ аркушах: стан решти аркушів читається й тут.
        var three = new List<long> { doc.TableInstanceId, extra[0].TableInstanceId, extra[3].TableInstanceId };

        var profile = new AccessBuilder().Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write).Build();

        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);
        var service = Service(db);

        // Прогрів знімка метаданих — і адреси всіх зрізів.
        var slices = await service.CanEditSlicesAsync(profile, twelve, CancellationToken.None);

        List<CellsAccessRequest> Cells(IEnumerable<long> ids)
            => [.. ids.Select(id => new CellsAccessRequest(id, new PeriodKey(PeriodKeyValue), [.. slices[id].Keys]))];

        Dictionary<long, IReadOnlyCollection<string>> Rows(IEnumerable<long> ids)
            => ids.ToDictionary(id => id, IReadOnlyCollection<string> (_) => ["P8NEW-1"]);

        async Task<CommandTallySnapshot> Measure(Func<Task> action)
        {
            counter.Tally.Reset();
            await action();
            return counter.Tally.Snapshot();
        }

        var cells3 = await Measure(() => service.CanEditCellsBatchAsync(profile, Cells(three), CancellationToken.None));
        var cells12 = await Measure(() => service.CanEditCellsBatchAsync(profile, Cells(twelve), CancellationToken.None));
        var rows3 = await Measure(() => service.CanCreateRowsBatchAsync(profile, Rows(three), CancellationToken.None));
        var rows12 = await Measure(() => service.CanCreateRowsBatchAsync(profile, Rows(twelve), CancellationToken.None));

        Assert.True(cells3.Total == cells12.Total, $"CanEditCellsBatchAsync: 3 → {cells3.Format()}; 12 → {cells12.Format()}");
        Assert.True(rows3.Total == rows12.Total, $"CanCreateRowsBatchAsync: 3 → {rows3.Format()}; 12 → {rows12.Format()}");

        // ⛔ Рівність без підлоги пройшла б і на «нуль звернень» (кеш усього).
        Assert.True(cells12.Total > 0 && rows12.Total > 0, "Нуль звернень — вимір нічого не міряє.");
    }

    private sealed record ForeignAddress(long TableInstanceId, CellAddress Address);

    private sealed record CellRequests(
        IReadOnlyList<CellsAccessRequest> All,
        IReadOnlyList<ForeignAddress> Foreign,
        long WrongPeriodInstanceId,
        long EmptyInstanceId);

    /// <summary>
    /// Адреси на кожен екземпляр: усі комірки зрізу, крім першої; комірка з
    /// рядком СУСІДНЬОГО екземпляра; комірка з неіснуючою колонкою. Базова
    /// таблиця документа B питається за чужим періодом, одна таблиця — без
    /// адрес.
    /// </summary>
    private async Task<CellRequests> CellRequestsAsync(World world)
    {
        IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>> slices;
        await using (var db = world.Builder.CreateContext())
        {
            slices = await Service(db).CanEditSlicesAsync(
                world.Profiles["Write на проєкти"], world.InstanceIds, CancellationToken.None);
        }

        var period = new PeriodKey(PeriodKeyValue);
        var ids = world.InstanceIds;
        var all = new List<CellsAccessRequest>();
        var foreign = new List<ForeignAddress>();

        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            var own = slices[id].Keys.OrderBy(Key).ToList();

            if (i == EmptyRequestIndex)
            {
                all.Add(new CellsAccessRequest(id, period, []));
                continue;
            }

            var neighbour = slices[ids[(i + 1) % ids.Count]].Keys.OrderBy(Key).First();
            var stranger = new CellAddress(period, neighbour.TableRowId, own[0].ColumnDefId);
            foreign.Add(new ForeignAddress(id, stranger));

            var addresses = own.Skip(1).ToList();
            addresses.Add(stranger);
            addresses.Add(new CellAddress(period, own[0].TableRowId, MissingColumnDefId));

            all.Add(new CellsAccessRequest(
                id, id == world.ClosedInstanceId ? new PeriodKey(PeriodKeyValue + 1) : period, addresses));
        }

        return new CellRequests(all, foreign, world.ClosedInstanceId, ids[EmptyRequestIndex]);
    }

    private static void AssertSameDecision(string where, EditDecision expected, EditDecision actual)
    {
        Assert.True(expected.IsAllowed == actual.IsAllowed, $"{where}: IsAllowed {expected.IsAllowed} ≠ {actual.IsAllowed}");
        Assert.True(expected.Reason == actual.Reason, $"{where}: Reason {expected.Reason} ≠ {actual.Reason}");
        Assert.True(expected.RequiresConfirmation == actual.RequiresConfirmation, $"{where}: RequiresConfirmation");
        Assert.True(string.Equals(expected.Detail, actual.Detail, StringComparison.Ordinal), $"{where}: Detail");
    }
}
