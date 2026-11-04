using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Audit;

/// <summary>
/// Експорт журналів аудиту читає сторінками й не матеріалізує журнал: у пам'яті щомиті одна сторінка, подальші
/// сторінки читаються лише коли споживач потоку дійшов до кінця попередньої.
/// </summary>
/// <remarks>
/// ⛔ Мутація: зібрати всі сторінки в список до `yield` — червоніє `Сторінки_читаються_ліниво_по_мірі_споживання`;
/// прибрати перевірку стелі — червоніє `Понад_стелю_422_до_першого_байта_і_без_події`.
/// </remarks>
public sealed class AuditExportRunnerTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Pages(int pageCount, int pageSize)
    {
        public int Fetches { get; private set; }

        public Task<PagedResult<int>> FetchAsync(string? cursor)
        {
            Fetches++;
            var page = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var items = Enumerable.Range(page * pageSize, pageSize).ToList();

            return Task.FromResult(new PagedResult<int>(
                items, page + 1 < pageCount ? (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null, null));
        }
    }

    private static (IAuditWriter Writer, IClock Clock, ICurrentUser User) Deps()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(5);

        return (Substitute.For<IAuditWriter>(), clock, user);
    }

    [Fact]
    public async Task Сторінки_читаються_ліниво_по_мірі_споживання()
    {
        var pages = new Pages(pageCount: 4, pageSize: 3);
        var (writer, clock, user) = Deps();

        var export = await AuditExportRunner.PrepareAsync(
            pages.FetchAsync, _ => Task.FromResult(12), maxRows: 100, "AuditTest", total => new { rows = total },
            "f.csv", writer, clock, user, CancellationToken.None);

        // Після підготовки прочитано лише перша сторінка (право/вікно/стеля), не весь журнал.
        Assert.Equal(1, pages.Fetches);
        Assert.Equal(12, export.Total);

        var seen = new List<int>();
        await foreach (var item in export.Rows)
        {
            seen.Add(item);

            // На початку кожної сторінки (індекс кратний 3) прочитано рівно стільки сторінок, скільки пройдено.
            Assert.Equal((seen.Count - 1) / 3 + 1, pages.Fetches);
        }

        Assert.Equal(Enumerable.Range(0, 12), seen);
    }

    [Fact]
    public async Task Понад_стелю_422_до_першого_байта_і_без_події()
    {
        var pages = new Pages(pageCount: 4, pageSize: 3);
        var (writer, clock, user) = Deps();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => AuditExportRunner.PrepareAsync(
            pages.FetchAsync, _ => Task.FromResult(12), maxRows: 10, "AuditTest", total => new { rows = total },
            "f.csv", writer, clock, user, CancellationToken.None));

        Assert.Equal("err.ECR-REQ-0422.auditExportTooLarge", ex.Details!["messageKey"]);
        await writer.DidNotReceiveWithAnyArgs().WriteIndependentSecurityEventAsync(default!, default);
    }

    [Fact]
    public async Task Подія_експорту_пишеться_до_потоку_з_кількістю_і_без_даних()
    {
        var pages = new Pages(pageCount: 1, pageSize: 2);
        var (writer, clock, user) = Deps();

        _ = await AuditExportRunner.PrepareAsync(
            pages.FetchAsync, p => Task.FromResult(p.Items.Count), maxRows: 100, "AuditTest",
            total => new { tab = "t", rows = total }, "f.csv", writer, clock, user, CancellationToken.None);

        await writer.Received(1).WriteIndependentSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e =>
                e.EventType == "AuditTest" && e.ChangedByUserId == 5 && e.DetailsJson == "{\"tab\":\"t\",\"rows\":2}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Стеля_обрізає_потік_навіть_якщо_журнал_виріс_після_підрахунку()
    {
        var pages = new Pages(pageCount: 5, pageSize: 3);
        var (writer, clock, user) = Deps();

        // Підрахунок збрехав (12 ≤ стелі), а сторінок насправді 15 рядків: потік зупиняється на стелі.
        var export = await AuditExportRunner.PrepareAsync(
            pages.FetchAsync, _ => Task.FromResult(12), maxRows: 12, "AuditTest", total => new { rows = total },
            "f.csv", writer, clock, user, CancellationToken.None);

        var count = 0;
        await foreach (var unused in export.Rows)
        {
            count++;
        }

        Assert.Equal(12, count);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("звичайний текст", "звичайний текст")]
    public void Формули_нейтралізуються_у_рядку_експорту_комірок(string value, string expected)
    {
        var line = ExportCellChangesHandler.ToCsv(new CellChangeView(
            Now, 202603, 1, "R1", 2, null, value, 3, "UserEdit", false));

        Assert.Contains("," + expected + ",", line, StringComparison.Ordinal);
        Assert.Matches("^2026-10-29T12:00:00\\.000Z,", line);
    }
}
