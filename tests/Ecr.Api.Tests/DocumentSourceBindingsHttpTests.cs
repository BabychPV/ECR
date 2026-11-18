using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations.SourceBindings;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B80 (N1): ендпоінти зв'язок документа-джерела — диференційна пара Allow/Deny за правом
/// <c>Calculation.ManageSourceBinding</c> на справжньому шляху HTTP.
/// </summary>
/// <remarks>
/// ⛔ Доступ доведено ДИФЕРЕНЦІЙНО, без мутації фільтра: той самий запит із правом проходить перевірку права (200 /
/// 409 бізнес-відмови), без нього — 403 з кодом права, і стан зв'язки не змінюється. Для AU: вимкнути
/// <c>PermissionCheck.RequireAsync</c> у <c>SwitchDocumentSourceStateHandler.HandleAsync</c> — Deny-тест червоніє
/// (409/200 замість 403).
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentSourceBindingsHttpTests(SqlServerFixture sql)
{
    private static readonly Uri List = new("/api/v1/document-source-bindings", UriKind.Relative);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task З_правом_перелік_і_перемикання_проходять_перевірку_права()
    {
        var binding = await ArrangeBindingAsync();
        using var app = new EcrApiFactory(sql);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, DocumentSourceBindings.Permission);

        var list = await admin.GetAsync(List);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.True(list.StatusCode == HttpStatusCode.OK, $"{list.StatusCode}: {listBody}");
        using (var document = JsonDocument.Parse(listBody))
        {
            var mine = Assert.Single(
                document.RootElement.EnumerateArray(), e => e.GetProperty("id").GetInt32() == binding.Id);
            Assert.Equal("any", mine.GetProperty("sourceState").GetString());
            Assert.Equal(binding.Code, mine.GetProperty("code").GetString());
        }

        // Право є — доходимо до правила: документа-джерела поточного року немає → 409, стан не змінено.
        var put = await admin.PutAsJsonAsync(SwitchUri(binding.Id), new { sourceState = "approvedOnly" });
        var putBody = await put.Content.ReadAsStringAsync();
        Assert.True(put.StatusCode == HttpStatusCode.Conflict, $"{put.StatusCode}: {putBody}");
        Assert.Contains("ECR-CALC-0409", putBody, StringComparison.Ordinal);

        // Назад на «any» (вже any) — 200 без змін.
        var same = await admin.PutAsJsonAsync(SwitchUri(binding.Id), new { sourceState = "any" });
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(DocumentSourceState.Any, await StateAsync(binding.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Без_права_перелік_і_перемикання_403_з_кодом_права_і_стан_не_змінюється()
    {
        var binding = await ArrangeBindingAsync();
        using var app = new EcrApiFactory(sql);

        // Право сусіднє (адмін здоров'я), але не керування зв'язками.
        using var viewer = await SystemHealthControllerTests.SignedInAsync(sql, app, "System.ViewHealth", "Calculation.View");

        var list = await viewer.GetAsync(List);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains(DocumentSourceBindings.Permission, listBody, StringComparison.Ordinal);
        Assert.DoesNotContain(binding.Code, listBody, StringComparison.Ordinal);

        foreach (var state in new[] { "approvedOnly", "any" })
        {
            var put = await viewer.PutAsJsonAsync(SwitchUri(binding.Id), new { sourceState = state });
            Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
            Assert.Contains(DocumentSourceBindings.Permission, await put.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(DocumentSourceState.Any, await StateAsync(binding.Id));
    }

    private static Uri SwitchUri(int id) => new($"/api/v1/document-source-bindings/{id}/source-state", UriKind.Relative);

    /// <summary>Власна зв'язка: шаблон тестового документа (без таблиць-потоків) і власний довідник.</summary>
    private async Task<(int Id, string Code)> ArrangeBindingAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"HB{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "B80 test" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        var binding = new DocumentSourceBinding($"B80H_{tag}", doc.TemplateId, registry.Id);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        return (binding.Id, binding.Code);
    }

    private async Task<DocumentSourceState> StateAsync(int id)
    {
        await using var db = sql.CreateContext();
        return await db.DocumentSourceBindings.AsNoTracking().Where(b => b.Id == id).Select(b => b.SourceState).SingleAsync();
    }
}
