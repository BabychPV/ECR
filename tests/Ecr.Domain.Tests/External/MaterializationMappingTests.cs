// tests/Ecr.Domain.Tests/External/MaterializationMappingTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Матеріалізація точок AF у комірки — <c>D-118</c>.
/// </summary>
/// <remarks>
/// ⛔ Найважливіше правило розділу: **здогадка тут дає ЧИСЛО, а не відмову.**
/// Мапінг без адресата або без способу згортання не «спрацює якось» — він
/// покладе в комірку значення, яке ніхто не замовляв, звіт складеться, і
/// розбіжність знайдуть на звірці через місяць, коли його вже подали.
/// </remarks>
public sealed class MaterializationMappingTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Мапінг_без_рядка_НЕ_матеріалізується_і_це_легально()
    {
        // ⚠ `null` означає рівно одне: точки лишаються сирими для звірки. Тег
        // може збиратися для контролю, а не для форми, і це нормальний стан —
        // а не «забули налаштувати».
        var map = EntityFieldMap.ToColumn(sourceEntityId: 1, sourceField: "Flare_01_CO", columnDefId: 10);

        Assert.False(map.IsMaterialized);
        Assert.Null(map.Aggregation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Рядок_без_агрегації_є_помилкою_конфігурації()
    {
        // ⛔ Пара нерозривна. Система не знає, чи величина миттєва
        // (концентрація → Last), чи накопичувальна (обсяг → Sum); це знає той,
        // хто налаштовує мапінг. Значення за замовчуванням тут неможливе.
        var map = EntityFieldMap.ToColumn(1, "Flare_01_CO", 10);

        var error = Assert.Throws<DomainException>(
            () => map.SetMaterialization("Flare_01", aggregation: null));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);

        // ⚠ І стан не змінився: невдала спроба не лишає мапінг напів-заданим.
        Assert.False(map.IsMaterialized);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(AggregationKind.Sum)]
    [InlineData(AggregationKind.Avg)]
    [InlineData(AggregationKind.Min)]
    [InlineData(AggregationKind.Max)]
    [InlineData(AggregationKind.Last)]
    [InlineData(AggregationKind.First)]
    public void Кожна_агрегація_переліку_зберігається_і_читається(AggregationKind kind)
    {
        // ⚠ Перевіряються ВСІ шість: `TransformCode` зберігається рядком, і
        // будь-яка розбіжність між назвою члена і тим, що читається назад,
        // мовчки перетворила б мапінг на «без агрегації».
        var map = EntityFieldMap.ToColumn(1, "Flare_01_CO", 10);

        map.SetMaterialization("Flare_01", kind);

        Assert.True(map.IsMaterialized);
        Assert.Equal(kind, map.Aggregation);
        Assert.Equal(kind.ToString(), map.TransformCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Скасування_матеріалізації_прибирає_і_агрегацію()
    {
        // ⚠ Інакше лишився б мапінг без рядка, але з `TransformCode` — стан,
        // який виглядає налаштованим і нічого не робить.
        var map = EntityFieldMap.ToColumn(1, "Flare_01_CO", 10);
        map.SetMaterialization("Flare_01", AggregationKind.Sum);

        map.SetMaterialization(targetRowKey: null, aggregation: null);

        Assert.False(map.IsMaterialized);
        Assert.Null(map.TransformCode);
        Assert.Null(map.Aggregation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ключ_рядка_з_поля_джерела_НЕ_підтримується()
    {
        // ⛔ Варіанту «взяти ключ рядка з поля джерела» немає навмисно (`D-118`):
        // у чинній системі такого немає, і він відкрив би шлях до рядків, яких
        // у шаблоні не існує. Адресат мапінгу — фіксований рядок, і це видно з
        // того, що задати його можна лише сталим значенням.
        var writable = typeof(EntityFieldMap)
            .GetProperties()
            .Where(p => p.Name.Contains("RowSource", StringComparison.Ordinal)
                        || p.Name.Contains("RowField", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(writable);
    }
}
