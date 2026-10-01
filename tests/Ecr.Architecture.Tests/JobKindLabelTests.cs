using System.Text.RegularExpressions;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Кожен вид фонової задачі має людську назву в клієнті
/// (<c>features/workflow/jobLabel.ts</c>, <c>KindKeys</c>).
/// </summary>
/// <remarks>
/// ⛔ Задача без запису в <c>KindKeys</c> не падає — <c>/admin/jobs</c> і тости
/// показують сирий .NET-тип (<c>ISourceEventSyncJob</c>) там, де решта задач
/// має назву мовою читача. Так і сталося з синком подій джерела: маркер
/// з'явився в прикладному шарі, а в перелік клієнта не потрапив. Сам ключ
/// каталогу стереже <see cref="EndpointCoverageTests"/> (<c>DynamicKeySites</c>),
/// а цей тест — що вид задачі взагалі НАЗВАНО.
/// </remarks>
public sealed partial class JobKindLabelTests
{
    private const string LabelFile = "src/Ecr.Web/src/features/workflow/jobLabel.ts";

    private const string SeedFile = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    private static readonly string[] Languages = ["en", "ru", "kz"];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_маркер_фонової_задачі_названий_у_KindKeys()
    {
        var markers = typeof(IBackgroundJob).Assembly.GetTypes()
            .Where(t => t.IsInterface && t != typeof(IBackgroundJob) && typeof(IBackgroundJob).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        // ⚠ Без маркерів тест був би зеленим ні про що.
        Assert.NotEmpty(markers);

        var named = KindKeyEntry()
            .Matches(File.ReadAllText(Path.Combine(SourceTree.Root, LabelFile)))
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var missing = markers.Where(m => !named.Contains(m)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"Вид фонової задачі без людської назви — допиши в KindKeys ({LabelFile}), "
            + "у DynamicKeySites (EndpointCoverageTests) і рядки en/ru/kz у 09-seed.sql: "
            + string.Join(", ", missing));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Назва_кожного_виду_задачі_є_в_сіді_en_ru_kz()
    {
        // ⚠ Виняток із «повноти перекладів не вимагаємо» (SeedTranslationTests), той
        // самий, що для статусів покриття: види задач — закритий перелік сервера, і
        // ru/kz-екран /admin/jobs показав би посеред таблиці англійське слово.
        var keys = KindKeyEntry()
            .Matches(File.ReadAllText(Path.Combine(SourceTree.Root, LabelFile)))
            .Select(m => m.Groups[2].Value)
            .ToList();
        Assert.NotEmpty(keys);

        var seed = File.ReadAllText(Path.Combine(SourceTree.Root, SeedFile));

        var missing = keys
            .SelectMany(key => Languages.Select(lang => (Key: key, Lang: lang)))
            .Where(p => !Regex.IsMatch(seed, $@"\(N'{Regex.Escape(p.Key)}',\s*N'{p.Lang}'"))
            .Select(p => $"{p.Key} / {p.Lang}")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"У {SeedFile} бракує назв видів задач: " + string.Join(", ", missing));
    }

    /// <summary>Рядок <c>IНазваJob: 'jobs.kind.…'</c> переліку <c>KindKeys</c>.</summary>
    [GeneratedRegex(@"^\s*(I\w+Job)\s*:\s*'(jobs\.kind\.\w+)'", RegexOptions.Multiline)]
    private static partial Regex KindKeyEntry();
}
