// tests/Ecr.Api.Tests/Security/DenyReadTests.TableStatusCounts.cs
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// AU-13 P3-5 (S6): <c>GET …/tables/status</c> рахував заборонену колонку видимої таблиці в заповненості - читач бачив
/// «3 з 6» замість «2 з 4», тобто скільки комірок схованої колонки заповнено.
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>!readable.CanReadColumn(c.Id)</c> із <c>GetTableStatusHandler</c> - червоніє (лічильники читача
/// дорівнюють лічильникам <c>plain</c>).
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Статус_таблиці_не_рахує_заборонену_колонку_ні_в_заповнених_ні_в_знаменнику()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var period = s.Doc.PeriodKey.Value;
        using var app = new EcrApiFactory(sql);

        async Task<(int Filled, int Input, int Rows)> MainAsync(string user)
        {
            using var client = await SignedInAsync(app, user).ConfigureAwait(true);
            var (code, body) = await GetAsync(
                client, $"/api/v1/documents/{s.Doc.DocumentId}/tables/status?periodKey={period}").ConfigureAwait(true);
            Assert.True(code == HttpStatusCode.OK, $"{code}: {body}\n{app.ErrorsText}");
            var row = JsonDocument.Parse(body).RootElement.EnumerateArray()
                .Single(t => t.GetProperty("tableDefId").GetInt32() == s.Doc.TableDefId);
            return (row.GetProperty("filledCells").GetInt32(), row.GetProperty("inputCells").GetInt32(), row.GetProperty("rowCount").GetInt32());
        }

        var plain = await MainAsync(s.Plain).ConfigureAwait(true);
        var reader = await MainAsync(s.Reader).ConfigureAwait(true);

        // Без заборон: обидва значення (видиме C2 і C3) заповнені.
        Assert.True(plain.Filled >= 2, $"plain: {plain}");

        // Читач: мінус одна заповнена комірка (C3) і мінус по комірці C3 на кожен рядок у знаменнику.
        Assert.Equal(plain.Filled - 1, reader.Filled);
        Assert.Equal(plain.Input - plain.Rows, reader.Input);
    }
}
