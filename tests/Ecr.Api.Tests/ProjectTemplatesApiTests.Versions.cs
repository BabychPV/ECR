// tests/Ecr.Api.Tests/ProjectTemplatesApiTests.Versions.cs
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B74 P3 (D-576): фільтр і сортування переліку документів за версією шаблону, а також варіанти фільтра — за версією
/// ШАБЛОНУ ДОКУМЕНТА: у проєкті «рік · об'єкт» документи різних шаблонів мають різні версії.
/// </summary>
public sealed partial class ProjectTemplatesApiTests
{
    /// <summary>
    /// Проєкт A з документами шаблонів A і B: фільтр версії B дає лише документи B, версії A — лише A; варіанти фільтра
    /// називають обидві версії з правильними лічильниками; сортування за версією групує документи за їхнім шаблоном.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: у <c>DocumentStore.ProjectVersionLabelsAsync</c> брати версію основного шаблону для будь-якого документа
    /// (<c>VersionOf</c>) — червоне: фільтр версії B порожній, документи B потрапляють під версію A.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B74")]
    [Trait("Decision", "D-576")]
    public async Task Фільтр_і_сортування_за_версією_групують_за_шаблоном_документа()
    {
        var s = await ArrangeAsync(ManagerPermissions, GrantLevel.Manage).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);
        (await AttachAsync(client, s.A.ProjectId, s.B.TemplateVersionId).ConfigureAwait(true)).EnsureSuccessStatusCode();

        var b1 = await CreateDocumentAsync(app, client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        var b2 = await CreateDocumentAsync(app, client, s.A.ProjectId, s.B.TemplateId, s.B.SheetDefId).ConfigureAwait(true);
        var a1 = await CreateDocumentAsync(app, client, s.A.ProjectId, s.A.TemplateId, s.A.SheetDefId).ConfigureAwait(true);
        var ofA = new[] { s.A.DocumentId, a1 };
        var ofB = new[] { b1, b2 };

        async Task<List<long>> IdsAsync(string query)
        {
            var page = await GetJsonAsync(app, client, $"/api/v1/documents?projectId={s.A.ProjectId}&limit=50&{query}")
                .ConfigureAwait(false);
            return [.. page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetInt64())];
        }

        Assert.Equal(ofB.Order(), (await IdsAsync($"templateVersions={Uri.EscapeDataString(s.BVersion)}").ConfigureAwait(true)).Order());
        Assert.Equal(ofA.Order(), (await IdsAsync($"templateVersions={Uri.EscapeDataString(s.AVersion)}").ConfigureAwait(true)).Order());

        // Сортування: документи йдуть суцільними групами своїх шаблонів (порядок груп — за версією).
        var sorted = await IdsAsync("sort=templateVersion").ConfigureAwait(true);
        Assert.Equal(4, sorted.Count);
        var groups = sorted.Select(id => ofB.Contains(id) ? 'B' : 'A').ToList();
        Assert.True(
            string.Concat(groups) is "AABB" or "BBAA",
            $"документи не згруповані за версією свого шаблону: {string.Concat(groups)}");

        var options = await GetJsonAsync(app, client, $"/api/v1/documents/template-versions?projectId={s.A.ProjectId}")
            .ConfigureAwait(true);
        var counts = options.GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("version").GetString()!, x => x.GetProperty("count").GetInt32());
        Assert.Equal(2, counts[s.AVersion]);
        Assert.Equal(2, counts[s.BVersion]);
    }
}
