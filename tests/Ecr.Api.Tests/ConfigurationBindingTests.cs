using System.Reflection;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Значення з <c>appsettings.json</c> доїжджають до тих, хто ними користується
/// (D14-06).
/// </summary>
/// <remarks>
/// ⛔ Сторож <c>ConfigurationKeysTests</c> цього НЕ доводить і не може: він
/// читає текст і лишається зеленим під будь-якою мутацією, що зберігає рядок
/// ключа. Саме тому тут — звичайний тест: збираємо конфігурацію з РЕАЛЬНОГО
/// <c>src/Ecr.Api/appsettings.json</c>, проганяємо через
/// <c>AddEcrInfrastructure</c> і дивимося на число, яке дістав споживач.
///
/// ⚠ Без SQL Server: <c>AddEcrInfrastructure</c> лише реєструє, а
/// <c>BulkCellLoader</c> і <c>EcrDbContext</c> створюються без відкриття
/// з'єднання.
/// </remarks>
public sealed class ConfigurationBindingTests
{
    /// <summary>Рядок підключення, який ніколи не відкривається.</summary>
    /// <remarks>
    /// У файлі <c>ConnectionStrings:Ecr</c> порожній навмисно (`D-11`), а
    /// <c>AddEcrInfrastructure</c> на порожньому кидає. Підміняємо лише його.
    /// </remarks>
    private const string FakeConnection = "Server=(local);Database=EcrUnused;Integrated Security=true";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D14-06")]
    public void BulkCellLoader_отримує_розмір_пакета_з_appsettings()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var loader = scope.ServiceProvider.GetRequiredService<BulkCellLoader>();

        // 50 000 — значення `Database:BulkBatchSize` у файлі. До `S-11` читач
        // стояв на префіксі `Sql:`, тобто мовчки брав свій дефолт 5 000.
        Assert.Equal(50_000, BatchSizeOf(loader));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D14-06")]
    public void BulkCellLoader_бере_саме_значення_файлу_а_не_збіг_із_дефолтом()
    {
        // ⛔ Друга половина доказу. Одного лише «дорівнює 50 000» не досить:
        // так само зелено було б, якби хтось просто поставив 50 000 дефолтом у
        // коді. Тут значення ЗАДАЄТЬСЯ накладкою — і має доїхати саме воно.
        using var provider = Build(("Database:BulkBatchSize", "7331"));
        using var scope = provider.CreateScope();

        Assert.Equal(7331, BatchSizeOf(scope.ServiceProvider.GetRequiredService<BulkCellLoader>()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D14-06")]
    public void Таймаут_команди_береться_з_конфігурації()
    {
        // ⚠ Навмисно НЕ 60: у файлі стоїть рівно стільки ж, скільки в дефолті
        // читача, тому на 60 тест був би зелений і зі зламаним ключем — саме
        // так дефект `Sql:CommandTimeoutSeconds` і прожив непоміченим (`S-11`).
        using var provider = Build(("Database:CommandTimeoutSeconds", "137"));
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        Assert.Equal(137, db.Database.GetCommandTimeout());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "D14-06")]
    public void Строки_кешів_беруться_з_appsettings()
    {
        using var provider = Build();

        var lifetimes = provider.GetRequiredService<CacheLifetimes>();

        // 240 і 60 — значення `Cache:*SlidingMinutes` у файлі. До `D14-06`
        // обидва ключі не мали читача, а в коді стояло жорстке 30 хв.
        Assert.Equal(TimeSpan.FromMinutes(240), lifetimes.Metadata);
        Assert.Equal(TimeSpan.FromMinutes(60), lifetimes.AccessProfile);
    }

    /// <summary>Розмір пакета, з яким справді створили завантажувач.</summary>
    /// <remarks>
    /// ⚠ Через рефлексію: <c>batchSize</c> — параметр первинного конструктора,
    /// назовні його не видно. Поле шукається за ІМЕНЕМ і за відсутності дає
    /// зрозумілу відмову, а не мовчазне «нуль дорівнює нулю»: інакше
    /// перейменування параметра зробило б тест хибно-зеленим.
    /// </remarks>
    private static int BatchSizeOf(BulkCellLoader loader)
    {
        var field = typeof(BulkCellLoader)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.Name.Contains("batchSize", StringComparison.Ordinal));

        Assert.True(
            field is not null,
            "У BulkCellLoader більше немає поля batchSize — перевірка осліпла. "
            + "Онови BatchSizeOf під нову форму зберігання розміру пакета.");

        return Assert.IsType<int>(field!.GetValue(loader));
    }

    private static ServiceProvider Build(params (string Key, string Value)[] overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "appsettings.json"), optional: false)
            .AddInMemoryCollection(
                overrides
                    .Select(o => new KeyValuePair<string, string?>(o.Key, o.Value))
                    .Append(new KeyValuePair<string, string?>("ConnectionStrings:Ecr", FakeConnection)))
            .Build();

        // ⛔ Перевірка, що файл узагалі прочитався: порожня конфігурація дала б
        // дефолти читачів, тобто зелене там, де перевірки немає. Звіряємося з
        // ключем, якого накладки не чіпають — інакше самоперевірка сперечалася
        // б із власним тестом.
        Assert.Equal("ecr.auth", configuration["Auth:CookieName"]);

        return new ServiceCollection()
            .AddEcrInfrastructure(configuration)
            .BuildServiceProvider();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Не знайдено кореня репозиторію (каталогу з src/).");
    }
}
