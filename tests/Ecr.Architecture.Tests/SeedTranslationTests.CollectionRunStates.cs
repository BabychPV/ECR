using Ecr.Application.Integration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>Стани прогону збору мають підпис усіма мовами продукту.</summary>
public sealed partial class SeedTranslationTests
{
    /// <summary>
    /// Кожен стан <see cref="ListCollectionRunsHandler.KnownStates"/> має ключ
    /// <c>status.collectionRun.*</c> в en-каталозі й у перекладах ru і kz.
    /// </summary>
    /// <remarks>
    /// ⚠ Той самий виняток із «повноти не вимагаємо», що й для статусів покриття:
    /// стан прогону — закритий перелік сервера. Без <c>Running</c> бейдж на
    /// <c>/admin/sources</c>, поки триває збір, малював
    /// <c>⟦status.collectionRun.Running⟧</c> (Y4-03).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_стан_прогону_збору_має_підпис_en_ru_kz()
    {
        var text = SeedText();
        var catalog = CatalogRows(text);
        var translated = TranslationRows(text)
            .Select(r => (r.Key, r.Lang))
            .ToHashSet();

        var missing = new List<string>();
        foreach (var state in ListCollectionRunsHandler.KnownStates)
        {
            var key = $"status.collectionRun.{state}";
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
            $"У {SeedFile} бракує підписів станів прогону збору:{Environment.NewLine}"
            + string.Join(Environment.NewLine, missing));
    }
}
