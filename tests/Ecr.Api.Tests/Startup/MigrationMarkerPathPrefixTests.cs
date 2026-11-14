// tests/Ecr.Api.Tests/Startup/MigrationMarkerPathPrefixTests.cs

using System.Reflection;
using System.Text.RegularExpressions;
using Ecr.Api.Startup;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// B122: фільтр переносу версії (<see cref="MigrationMarkerFilter"/>) розбирає префікс шляху
/// <c>/api/v{n}/documents|projects/{id}</c> без регексу.
/// </summary>
/// <remarks>
/// ⛔ Тут стояв регекс із таймаутом 50 мс. Під навантаженням (голодний пул потоків) таймаут спрацьовував на звичайному
/// шляху, і <c>RegexMatchTimeoutException</c> давав 500 на будь-якому маршруті, доки йде перенос версії (rerun api-1 у
/// CI B84). Мутаційні докази: зламати розбір (наприклад, прийняти <c>/api/v1/documentsX/5</c>, не вимагати <c>/</c>
/// після Id чи забути регістр) — червоніє таблиця нижче; повернути статичне поле <see cref="Regex"/> у фільтр —
/// червоніє другий тест.
/// </remarks>
public sealed class MigrationMarkerPathPrefixTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B122")]
    [InlineData("/api/v1/documents/42", false, 42L)]
    [InlineData("/api/v1/documents/42/cells", false, 42L)]
    [InlineData("/api/v1/documents/42/", false, 42L)]
    [InlineData("/api/v1/projects/7", true, 7L)]
    [InlineData("/api/v1/projects/7/periods", true, 7L)]
    [InlineData("/API/V2/Projects/7/periods", true, 7L)]
    [InlineData("/api/v12/documents/9223372036854775807", false, long.MaxValue)]
    public void Префікс_документа_чи_проєкту_розбирається(string path, bool isProject, long id)
    {
        Assert.Equal((isProject, id), MigrationMarkerFilter.PathPrefixOf(path));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B122")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/api/v1/documents")]
    [InlineData("/api/v1/documents/")]
    [InlineData("/api/v1/documents/abc")]
    [InlineData("/api/v1/documents/42abc")]
    [InlineData("/api/v1/documentsX/42")]
    [InlineData("/api/v1/templates/42")]
    [InlineData("/api/v/documents/42")]
    [InlineData("/api/vx/documents/42")]
    [InlineData("/api/v1documents/42")]
    [InlineData("api/v1/documents/42")]
    [InlineData("/x/api/v1/documents/42")]
    [InlineData("/api/v1/documents/-1")]
    [InlineData("/api/v1/documents/٤٢")]
    [InlineData("/api/v1/documents/9223372036854775808")]
    public void Інший_шлях_не_дає_префікса(string? path)
    {
        Assert.Null(MigrationMarkerFilter.PathPrefixOf(path));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B122")]
    public void Фільтр_не_тримає_регексів()
    {
        var regexFields = typeof(MigrationMarkerFilter)
            .GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => typeof(Regex).IsAssignableFrom(f.FieldType))
            .Select(f => f.Name)
            .ToList();

        Assert.Empty(regexFields);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "B122")]
    public void Довгий_шлях_розбирається_без_винятку()
    {
        var path = "/api/v1/documents/" + new string('1', 100_000) + "/x";

        Assert.Null(MigrationMarkerFilter.PathPrefixOf(path));
    }
}
