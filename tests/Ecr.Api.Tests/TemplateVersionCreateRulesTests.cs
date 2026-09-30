// tests/Ecr.Api.Tests/TemplateVersionCreateRulesTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Створення й клонування версії шаблону: право → валідація номера → номер
/// вільний → джерело з того самого шаблону (B-04, X-30, UX-прохід, четвертий раунд).
/// </summary>
/// <remarks>
/// ⛔ Що відтворили аналітики: порожній номер — <c>500</c> (<c>ArgumentException</c>)
/// ще до перевірки права; номер, що вже є в шаблоні, на шляху клону — <c>500</c>
/// (<c>UQ_TemplateVersion</c>); <c>cloneFromVersionId</c> з іншого шаблону
/// клонував у шаблон-джерело, а не в <c>{id}</c> маршруту, з <c>201</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionCreateRulesTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B-04")]
    public async Task Без_права_порожній_номер_дає_403_а_з_правом_422_з_ключем()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        using (var stranger = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.View").ConfigureAwait(true))
        {
            foreach (var number in new[] { "", "   " })
            {
                await AssertProblemAsync(CreateAsync(stranger, document.TemplateId, number, null), HttpStatusCode.Forbidden, "ECR-AUTH-0403", null, app);
                await AssertProblemAsync(CloneAsync(stranger, document.TemplateVersionId, number), HttpStatusCode.Forbidden, "ECR-AUTH-0403", null, app);
            }
        }

        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.Edit").ConfigureAwait(true);

        await AssertProblemAsync(
            CreateAsync(editor, document.TemplateId, "", null), HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422",
            "A version number is required, in the form Major.Minor.Patch.Build.", app);
        await AssertProblemAsync(
            CreateAsync(editor, document.TemplateId, "", document.TemplateVersionId), HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422",
            "A version number is required, in the form Major.Minor.Patch.Build.", app);
        await AssertProblemAsync(
            CloneAsync(editor, document.TemplateVersionId, " "), HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422",
            "A version number is required, in the form Major.Minor.Patch.Build.", app);

        // ⚠ Формат тепер однаковий для обох входів: клон приймав будь-який рядок.
        await AssertProblemAsync(
            CloneAsync(editor, document.TemplateVersionId, "1.2 final"), HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422",
            "Version number \"1.2 final\" does not match the form Major.Minor.Patch.Build.", app);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "X-30")]
    public async Task Зайнятий_номер_клону_дає_409_а_джерело_з_іншого_шаблону_422_без_запису()
    {
        var mine = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        Assert.NotEqual(mine.TemplateId, other.TemplateId);

        using var app = new EcrApiFactory(sql);
        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, "Template.Edit").ConfigureAwait(true);

        var number = $"{Random.Shared.Next(10, 99)}.{Random.Shared.Next(100, 999)}.0.1";

        // Перший клон — законний.
        using (var created = await CreateAsync(editor, mine.TemplateId, number, mine.TemplateVersionId).ConfigureAwait(true))
        {
            Assert.True(
                created.StatusCode == HttpStatusCode.Created,
                $"Перший клон: {created.StatusCode}\n{await created.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        }

        // Той самий номер у тому самому шаблоні — обома входами 409 з ключем, а не 500.
        var taken = $"Version {number} already exists in this template.";
        await AssertProblemAsync(CreateAsync(editor, mine.TemplateId, number, mine.TemplateVersionId), HttpStatusCode.Conflict, "ECR-TMPL-0409", taken, app);
        await AssertProblemAsync(CloneAsync(editor, mine.TemplateVersionId, number), HttpStatusCode.Conflict, "ECR-TMPL-0409", taken, app);
        await AssertProblemAsync(CreateAsync(editor, mine.TemplateId, number, null), HttpStatusCode.Conflict, "ECR-TMPL-0409", taken, app);

        // Джерело з ІНШОГО шаблону — 422, і в шаблоні-джерелі нічого не з'явилося.
        var crossNumber = $"{Random.Shared.Next(10, 99)}.{Random.Shared.Next(100, 999)}.0.2";
        await AssertProblemAsync(
            CreateAsync(editor, mine.TemplateId, crossNumber, other.TemplateVersionId), HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422",
            $"Version {other.TemplateVersionId} belongs to another template: a version can only be cloned within its own template ({mine.TemplateId}).",
            app);

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        Assert.Equal(0, await db.TemplateVersions.CountAsync(v => v.Version == crossNumber).ConfigureAwait(true));
        Assert.Equal(1, await db.TemplateVersions.CountAsync(v => v.Version == number).ConfigureAwait(true));
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, int templateId, string number, int? source)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/templates/{templateId}/versions", UriKind.Relative),
            new { versionNumber = number, cloneFromVersionId = source });

    private static Task<HttpResponseMessage> CloneAsync(HttpClient client, int versionId, string number)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/clone", UriKind.Relative),
            new { newVersion = number });

    private static async Task AssertProblemAsync(
        Task<HttpResponseMessage> call, HttpStatusCode status, string code, string? detail, EcrApiFactory app)
    {
        using var response = await call.ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == status, $"очікували {(int)status}, отримали {(int)response.StatusCode}\n{body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(code, problem.GetProperty("errorCode").GetString());

        if (detail is not null)
        {
            Assert.Equal(detail, problem.GetProperty("detail").GetString());
        }
    }
}
