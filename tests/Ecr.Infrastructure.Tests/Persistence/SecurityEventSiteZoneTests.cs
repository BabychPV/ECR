using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ debt-1110: журнал безпеки друкує час у поясі МАЙДАНЧИКА, а не браузера — сервер віддає пояс у кожному рядку
/// (<see cref="SecurityEventView.SiteTimeZoneId"/>). Подія безпеки не належить проєкту, тож майданчик — спільний пояс
/// усіх проєктів; різні пояси — <c>null</c> (клієнт лишає пояс читача).
/// </summary>
/// <remarks>
/// Мутації (локально): <see cref="AuditReader.SiteTimeZone"/> повертає перший пояс завжди — червоніє правило; читач не
/// ставить <c>SiteTimeZoneId</c> — червоніє читання, коли пояс у базі один (у спільній тестовій базі він може бути не
/// один, тоді читання звіряє <c>null</c>; правило окремо тримає юніт-тест).
/// </remarks>
[Collection("SqlServer")]
public sealed class SecurityEventSiteZoneTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Пояс_майданчика_лише_коли_він_один()
    {
        Assert.Equal("Asia/Atyrau", AuditReader.SiteTimeZone([("Asia/Atyrau", ProjectStatus.Active)]));
        Assert.Null(AuditReader.SiteTimeZone([("Asia/Atyrau", ProjectStatus.Active), ("Europe/Kyiv", ProjectStatus.Archived)]));
        Assert.Null(AuditReader.SiteTimeZone([]));
    }

    /// <summary>
    /// ✎ p3-1110: чернетка в іншому поясі не скасовує пояс майданчика — на живій установці саме вона давала
    /// <c>null</c>. Мутація (локально): правило не відкидає <see cref="ProjectStatus.Draft"/> — тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Чернетки_не_рахуються_в_поясі_майданчика()
    {
        Assert.Equal(
            "Asia/Atyrau",
            AuditReader.SiteTimeZone(
            [
                ("Asia/Atyrau", ProjectStatus.Active),
                ("Asia/Atyrau", ProjectStatus.Archived),
                ("Europe/Kyiv", ProjectStatus.Draft),
            ]));
        Assert.Null(AuditReader.SiteTimeZone([("Europe/Kyiv", ProjectStatus.Draft)]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_журналу_безпеки_несе_пояс_майданчика()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await builder.BuildAsync(ct: CancellationToken.None);
        var at = new DateTime(2026, 3, 5, 9, 0, 0, DateTimeKind.Utc);
        var eventType = $"SiteZone{Guid.NewGuid():N}"[..40];

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteSecurityEventAsync(
            new SecurityEventRecord(at, eventType, null, null, "{\"reason\":\"idle\"}", 1, null), CancellationToken.None);

        var page = await new AuditReader(db).ReadSecurityEventsAsync(
            new SecurityEventFilter(at.AddMinutes(-1), at.AddMinutes(1), EventType: eventType),
            new CursorRequest(10, null),
            CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Equal(AuditReader.SiteTimeZone(await ZonesAsync()), row.SiteTimeZoneId);
    }

    private async Task<List<(string, ProjectStatus)>> ZonesAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT TimeZoneId, Status FROM doc.Project;";

        var zones = new List<(string, ProjectStatus)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            zones.Add((reader.GetString(0), (ProjectStatus)reader.GetByte(1)));
        }

        return zones;
    }
}
