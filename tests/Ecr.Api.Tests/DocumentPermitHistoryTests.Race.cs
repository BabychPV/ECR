// tests/Ecr.Api.Tests/DocumentPermitHistoryTests.Race.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-1 (аудит 2026-11-10, L5): перевірки історії дозволу («дата після останнього», «розрив», «частин немає») рахуються від
/// шкали, прочитаної поза транзакцією; запис іде лише на ТІЙ САМІЙ шкалі під <c>UPDLOCK, HOLDLOCK</c>, інакше
/// <c>409 ECR-CELL-0409 concurrentChange</c>.
/// </summary>
/// <remarks>
/// ⚠ Справжній HTTP-шлях і справжній <c>PermitHistoryStore</c>; «інший користувач» вклинюється детерміновано —
/// декоратор сховища одразу ПІСЛЯ читання поза транзакцією комітить зміну окремим з'єднанням. Жодних пауз.
///
/// ⛔ Мутації (кожна робить свій тест червоним): прибрати <c>EnsureUnchanged</c> з add рядка → 200 і два сегменти; з add
/// шапки → 200 і два сегменти; повторну перевірку <c>RowHasPartsOfAsync</c> під замком з undo рядка → 200, сегмент
/// видалено, частини лишились; блок замків із <c>SaveCellSplitsHandler</c> → 200, частини скасованого дозволу.
/// </remarks>
public sealed partial class DocumentPermitHistoryTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task AddRowPermit_after_concurrent_change_returns_409()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var race = new PermitHistoryRace
        {
            // Інший користувач додав FEB з 01.02 між нашим читанням шкали і записом.
            AfterRowSegments = (documentId, tableDefId, sheetCopyId, rowKey, columnDefId) => InsertSegmentAsync(
                PermitSegment.ForRow(documentId, tableDefId, sheetCopyId, rowKey, columnDefId, new DateOnly(2026, 2, 1), s.FebId, s.UserId, DateTime.UtcNow)),
        };
        using var app = new EcrApiFactory(sql);
        using var racing = Racing(app, race);
        using var client = await SignedInAsync(racing, s.UserName).ConfigureAwait(true);

        var response = await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "декоратор не вклинився");
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(1, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task AddHeaderPermit_after_concurrent_change_returns_409()
    {
        var s = await ArrangeAsync(rowBase: null).ConfigureAwait(true);
        var race = new PermitHistoryRace
        {
            AfterHeaderSegments = (documentId, headerFieldDefId) => InsertSegmentAsync(
                PermitSegment.ForHeader(documentId, headerFieldDefId, new DateOnly(2026, 2, 1), s.FebId, s.UserId, DateTime.UtcNow)),
        };
        using var app = new EcrApiFactory(sql);
        using var racing = Racing(app, race);
        using var client = await SignedInAsync(racing, s.UserName).ConfigureAwait(true);

        var response = await AddHeaderAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "декоратор не вклинився");
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(1, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Поділ місяця закомічено між перевіркою «частин немає» і видаленням — undo відмовляє, сегмент лишається.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task UndoRowPermit_after_a_concurrent_split_is_refused_and_keeps_the_segment()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var race = new PermitHistoryRace();
        using var app = new EcrApiFactory(sql);
        using var racing = Racing(app, race);
        using var client = await SignedInAsync(racing, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        race.AfterRowHasParts = async (documentId, tableDefId, sheetCopyId, rowKey, registryEntryId) =>
        {
            await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
            db.CellPermitParts.Add(new CellPermitPart(
                202601, documentId, tableDefId, sheetCopyId, rowKey, s.DataColumnId, 1, registryEntryId,
                new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 31), 7m));
            await db.SaveChangesAsync().ConfigureAwait(false);
        };
        var undo = await client.PostAsJsonAsync(
            new Uri($"{Base(s)}/rows/undo", UriKind.Relative),
            new { tableInstanceId = s.TableInstanceId, rowKey = s.RowKey, columnCode = s.PermitColumnCode }).ConfigureAwait(true);
        var body = await undo.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "декоратор не вклинився");
        Assert.True(undo.StatusCode == HttpStatusCode.UnprocessableEntity, $"{undo.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-REQ-0422.permitHistoryNothingToUndo", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(1, await SegmentCountAsync(s).ConfigureAwait(true));
    }

    /// <summary>Дозвіл скасовано між плануванням поділу і записом — поділ відмовляє, частин скасованого дозволу немає.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-1")]
    public async Task SaveCellSplits_after_a_concurrent_undo_returns_409_and_stores_no_parts()
    {
        var s = await ArrangeAsync(rowBase: "year").ConfigureAwait(true);
        var race = new PermitHistoryRace();
        using var app = new EcrApiFactory(sql);
        using var racing = Racing(app, race);
        using var client = await SignedInAsync(racing, s.UserName).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, (await AddRowAsync(client, s, s.MidJanId, "2026-01-15").ConfigureAwait(true)).StatusCode);

        race.AfterList = async documentId =>
        {
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE doc.PermitSegment WHERE DocumentId = @id;";
            command.Parameters.AddWithValue("@id", documentId);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        };
        var split = await SplitAsync(client, s, 5m, 7m).ConfigureAwait(true);
        var body = await split.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(race.Fired, "декоратор не вклинився");
        Assert.True(split.StatusCode == HttpStatusCode.Conflict, $"{split.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(0, await PartCountAsync(s).ConfigureAwait(true));
        Assert.Equal(12m, await CellAsync(s).ConfigureAwait(true));
    }

    private static WebApplicationFactory<Program> Racing(EcrApiFactory app, PermitHistoryRace race)
        => app.WithWebHostBuilder(b => b.ConfigureTestServices(services => services.AddScoped<IPermitHistoryStore>(
            sp => new RacingPermitHistoryStore(new PermitHistoryStore(sp.GetRequiredService<EcrDbContext>()), race))));

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}");
        return client;
    }

    private async Task InsertSegmentAsync(PermitSegment segment)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        db.PermitSegments.Add(segment);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>«Інший користувач»: кожна дія спрацьовує один раз, одразу ПІСЛЯ відповідного читання поза транзакцією.</summary>
    private sealed class PermitHistoryRace
    {
        private int _fired;

        public Func<long, int, long?, string, int, Task>? AfterRowSegments { get; set; }

        public Func<long, int, Task>? AfterHeaderSegments { get; set; }

        public Func<long, Task>? AfterList { get; set; }

        public Func<long, int, long?, string, long, Task>? AfterRowHasParts { get; set; }

        public bool Fired => Volatile.Read(ref _fired) > 0;

        public async Task FireAsync<T>(Func<T?> take, Action clear, Func<T, Task> run)
            where T : class
        {
            if (take() is { } action)
            {
                clear();
                Interlocked.Increment(ref _fired);
                await run(action).ConfigureAwait(false);
            }
        }
    }

    private sealed class RacingPermitHistoryStore(IPermitHistoryStore inner, PermitHistoryRace race) : IPermitHistoryStore
    {
        public async Task<IReadOnlyList<PermitSegmentRecord>> ListAsync(long documentId, CancellationToken ct)
        {
            var result = await inner.ListAsync(documentId, ct).ConfigureAwait(false);
            await race.FireAsync(() => race.AfterList, () => race.AfterList = null, a => a(documentId)).ConfigureAwait(false);
            return result;
        }

        public async Task<IReadOnlyList<PermitSegment>> RowSegmentsAsync(
            long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct)
        {
            var result = await inner.RowSegmentsAsync(documentId, tableDefId, sheetCopyId, rowKey, columnDefId, ct).ConfigureAwait(false);
            await race.FireAsync(
                () => race.AfterRowSegments, () => race.AfterRowSegments = null,
                a => a(documentId, tableDefId, sheetCopyId, rowKey, columnDefId)).ConfigureAwait(false);
            return result;
        }

        public async Task<IReadOnlyList<PermitSegment>> HeaderSegmentsAsync(long documentId, int headerFieldDefId, CancellationToken ct)
        {
            var result = await inner.HeaderSegmentsAsync(documentId, headerFieldDefId, ct).ConfigureAwait(false);
            await race.FireAsync(
                () => race.AfterHeaderSegments, () => race.AfterHeaderSegments = null,
                a => a(documentId, headerFieldDefId)).ConfigureAwait(false);
            return result;
        }

        public Task<IReadOnlyList<PermitSegment>> LockRowSegmentsAsync(
            long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct)
            => inner.LockRowSegmentsAsync(documentId, tableDefId, sheetCopyId, rowKey, columnDefId, ct);

        public Task<IReadOnlyList<PermitSegment>> LockHeaderSegmentsAsync(long documentId, int headerFieldDefId, CancellationToken ct)
            => inner.LockHeaderSegmentsAsync(documentId, headerFieldDefId, ct);

        public Task<IReadOnlyList<CellPermitPart>> PartsAsync(long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
            => inner.PartsAsync(documentId, periodKeys, ct);

        public async Task<bool> RowHasPartsOfAsync(
            long documentId, int tableDefId, long? sheetCopyId, string rowKey, long registryEntryId, CancellationToken ct)
        {
            var result = await inner.RowHasPartsOfAsync(documentId, tableDefId, sheetCopyId, rowKey, registryEntryId, ct).ConfigureAwait(false);
            await race.FireAsync(
                () => race.AfterRowHasParts, () => race.AfterRowHasParts = null,
                a => a(documentId, tableDefId, sheetCopyId, rowKey, registryEntryId)).ConfigureAwait(false);
            return result;
        }

        public void Add(PermitSegment segment) => inner.Add(segment);

        public void Remove(PermitSegment segment) => inner.Remove(segment);

        public Task<IReadOnlyList<CellPermitPart>> CellPartsAsync(
            int periodKey, long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct)
            => inner.CellPartsAsync(periodKey, documentId, tableDefId, sheetCopyId, rowKey, columnDefId, ct);

        public void AddPart(CellPermitPart part) => inner.AddPart(part);

        public void RemovePart(CellPermitPart part) => inner.RemovePart(part);
    }
}
