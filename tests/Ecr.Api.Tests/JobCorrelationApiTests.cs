// tests/Ecr.Api.Tests/JobCorrelationApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// BE-08: задача, поставлена HTTP-запитом, несе кореляцію САМЕ цього запиту,
/// і <c>GET /api/v1/jobs/{jobId}</c> та перелік віддають <c>attempt</c> і
/// <c>correlationId</c>.
/// </summary>
/// <remarks>
/// ⚠ Наскрізно: справжній <c>CorrelationIdMiddleware</c> приймає заголовок
/// <c>X-Correlation-Id</c>, справжній планувальник пише рядок черги. Постановка
/// — «Run check now» (<c>POST /consistency/run</c>): єдиний ендпоінт черги без
/// документа-передумови.
/// </remarks>
[Collection("SqlServer")]
public sealed class JobCorrelationApiTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-08")]
    public async Task Кореляція_задачі_дорівнює_кореляції_запиту_що_її_поставив()
    {
        await ClearInFlightChecksAsync().ConfigureAwait(true);

        var correlation = $"be08{Guid.NewGuid():N}";

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SystemHealthControllerTests
                .SignedInAsync(sql, app, "System.RunJob", "System.ViewHealth")
                .ConfigureAwait(true);

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/consistency/run")
            {
                Content = JsonContent.Create(new { reason = "BE-08 correlation probe" }),
            };
            request.Headers.Add("X-Correlation-Id", correlation);

            var accepted = await client.SendAsync(request).ConfigureAwait(true);
            Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, $"{accepted.StatusCode}: {app.ErrorsText}");

            var jobId = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync().ConfigureAwait(true))
                .RootElement.GetProperty("jobId").GetString();

            var status = await client
                .GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId!)}", UriKind.Relative))
                .ConfigureAwait(true);

            // ⛔ Мутація: не передавати `correlationId` у `QueueAsync`/дані задачі —
            // тут приїде null або новий GUID, а не ідентифікатор запиту з логу.
            Assert.Equal(correlation, status.GetProperty("correlationId").GetString());

            // Спроба є в контракті відповіді: null до старту, від 1 після.
            var attempt = status.GetProperty("attempt");
            Assert.True(
                attempt.ValueKind == JsonValueKind.Null || attempt.GetInt32() >= 1,
                $"attempt = {attempt}");

            var listed = await client
                .GetFromJsonAsync<JsonElement>(new Uri(
                    $"/api/v1/jobs?code={typeof(Ecr.Application.Ports.IConsistencyCheckJob).FullName}",
                    UriKind.Relative))
                .ConfigureAwait(true);

            var row = listed.EnumerateArray().Single(j => j.GetProperty("jobId").GetString() == jobId);
            Assert.Equal(correlation, row.GetProperty("correlationId").GetString());
            Assert.True(row.TryGetProperty("attempt", out _));
        }
        finally
        {
            await ClearInFlightChecksAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Та сама вузька чистка, що в <c>ConsistencyIssuesControllerTests</c>.</summary>
    private async Task ClearInFlightChecksAsync()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE itg.JobProgress
            WHERE State IN (N'Queued', N'Running')
              AND JobCode LIKE N'%ConsistencyCheckJob';
            """;

        await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
