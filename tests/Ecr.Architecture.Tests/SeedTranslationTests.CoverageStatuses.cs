using Ecr.Domain.Entities.Integration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>Статуси журналу покриття мають підпис усіма мовами продукту.</summary>
public sealed partial class SeedTranslationTests
{
    /// <summary>
    /// Кожен статус <see cref="CollectionCoverage.KnownStatuses"/> має ключ
    /// <c>status.coverage.*</c> в en-каталозі й у перекладах ru і kz.
    /// </summary>
    /// <remarks>
    /// ⚠ Виняток із «повноти не вимагаємо» (заголовок класу): статус події —
    /// закритий перелік сервера, і бейдж без перекладу показав би
    /// адміністраторові ru/kz англійське слово посеред журналу. Новий статус
    /// (<c>D-212</c> PR-3) без трьох рядків сіду тут червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "ФВ-14.9")]
    public void Кожен_статус_покриття_має_підпис_en_ru_kz()
    {
        var text = SeedText();
        var catalog = CatalogRows(text);
        var translated = TranslationRows(text)
            .Select(r => (r.Key, r.Lang))
            .ToHashSet();

        var missing = new List<string>();
        foreach (var status in CollectionCoverage.KnownStatuses)
        {
            var key = $"status.coverage.{status}";
            if (!catalog.ContainsKey(key))
            {
                missing.Add($"{key} / en");
            }

            missing.AddRange(TranslationLanguages
                .Where(lang => !translated.Contains((key, lang)))
                .Select(lang => $"{key} / {lang}"));
        }

        Assert.True(
            missing.Count == 0,
            $"У {SeedFile} бракує підписів статусів покриття:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }
}
