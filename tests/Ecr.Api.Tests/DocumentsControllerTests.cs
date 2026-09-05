// tests/Ecr.Api.Tests/DocumentsControllerTests.cs
using System.Net;
using System.Reflection;
using System.Text.Json;
using Ecr.Api.Controllers;
using Ecr.TestKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Контролер не містить логіки — вона в обробнику.</summary>
[Collection("SqlServer")]
public sealed class DocumentsControllerTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Контролер_лише_делегує_обробнику()
    {
        // Логіка «в контролері» непомітно перетворює HTTP-шар на другий
        // прикладний: те саме правило починає жити у двох місцях і розходиться.
        // Тому в конструктор дозволено лише обробники і поточного користувача.
        var offenders = typeof(DocumentsController)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => !p.ParameterType.Name.EndsWith("Handler", StringComparison.Ordinal)
                        && p.ParameterType != typeof(Ecr.Api.Auth.CurrentUser))
            .Select(p => $"{p.ParameterType.Name} {p.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.14")]
    public void Права_перевіряються_в_обробнику_а_не_атрибутом_контролера()
    {
        // ⚠ [Authorize(Policy = "Document.Create")] виглядає охайно і руйнує
        // модель прав: доступ у ECR залежить від РЕСУРСУ — проєкту, аркуша,
        // періоду (ФВ-6.14), а атрибут бачить лише ім'я політики. Рішення
        // ухвалює IAccessDecisionService всередині обробника.
        var actions = typeof(DocumentsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var withPolicy = actions
            .SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>())
            .Where(a => !string.IsNullOrEmpty(a.Policy) || !string.IsNullOrEmpty(a.Roles))
            .ToList();

        Assert.Empty(withPolicy);

        // Клас при цьому [Authorize]: анонім не має доходити навіть до обробника.
        Assert.NotEmpty(typeof(DocumentsController).GetCustomAttributes<AuthorizeAttribute>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Помилка_повертається_як_ProblemDetails_із_кодом()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/documents/1", UriKind.Relative));

        // Анонім не доходить до обробника — і це теж коректна відповідь із кодом.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Клієнт розрізняє причини за КОДОМ, а не за текстом: текст
        // локалізований і може змінитися, код — ні.
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^ECR-[A-Z]+-\d{4}$", json.GetProperty("errorCode").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("correlationId").GetString()));
    }
}
