using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Z8-03 (аудит R8): <c>TestDatabaseSizeTests</c> знімає розмір файлів тестової бази в ту мить, коли до нього дійшла
/// черга, а порядок класів xUnit не задається — журнал, що розрісся від класу ПІСЛЯ сторожа, проходив зеленим.
/// Тому <c>SqlServerFixture.DisposeAsync</c> перевіряє розмір наприкінці колекції й кидає виняток.
/// </summary>
/// <remarks>
/// Предмет — текст фікстури (порядок дій у <c>DisposeAsync</c>); виконання перевірки — у
/// <c>TestDatabaseSizeTests</c> (Infrastructure.Tests). Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class SqlServerFixtureSizeGuardTests
{
    /// <remarks>
    /// Мутації: прибрати виклик <c>FindOversizedFilesAsync</c> із <c>DisposeAsync</c>; кидати виняток ДО зупинки
    /// контейнера (контейнер лишиться); міряти й для баз з суфіксом (власні бази тесту) — червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Фікстура_міряє_файли_наприкінці_колекції_і_кидає_виняток_після_зупинки_контейнера()
    {
        var source = File.ReadAllText(Path.Combine(SourceTree.Root, "tests", "Ecr.TestKit", "SqlServerFixture.cs"));

        var dispose = source.IndexOf("public async Task DisposeAsync()", StringComparison.Ordinal);
        Assert.True(dispose > 0, "SqlServerFixture.DisposeAsync не знайдено");

        var guard = source.IndexOf("if (_nameSuffix.Length == 0 && ConnectionString.Length > 0)", dispose, StringComparison.Ordinal);
        var check = source.IndexOf("await FindOversizedFilesAsync(ConnectionString, MaxFileMbAtCollectionEnd)", dispose, StringComparison.Ordinal);
        var container = source.IndexOf("await _container.DisposeAsync()", dispose, StringComparison.Ordinal);
        var thrown = source.IndexOf("throw new InvalidOperationException", container, StringComparison.Ordinal);

        Assert.True(guard > dispose && check > guard, "розмір міряється не лише для спільної бази колекції (власні бази з суфіксом — на тесті)");
        Assert.True(container > check, "розмір має бути виміряно ДО зупинки контейнера");
        Assert.True(thrown > container, "виняток про завеликі файли має летіти ПІСЛЯ зупинки контейнера");

        // Стеля в класі-сторожі — та сама константа фікстури; стеля наприкінці колекції — окрема й не нижча (храповик).
        var guardClass = File.ReadAllText(Path.Combine(
            SourceTree.Root, "tests", "Ecr.Infrastructure.Tests", "Persistence", "TestDatabaseSizeTests.cs"));
        Assert.Contains("private const int MaxFileMb = SqlServerFixture.MaxFileMb;", guardClass, StringComparison.Ordinal);
    }
}
