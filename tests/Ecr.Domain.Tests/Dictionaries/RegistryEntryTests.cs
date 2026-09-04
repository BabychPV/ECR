// tests/Ecr.Domain.Tests/Dictionaries/RegistryEntryTests.cs
using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Dictionaries;

/// <summary>
/// Темпоральність запису довідника (ФВ-8.5). Межі вікна **включні** з обох
/// боків — саме на цьому найлегше помилитися на день.
/// </summary>
public sealed class RegistryEntryTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("2026-01-01", true)]   // рівно ValidFrom
    [InlineData("2026-06-30", true)]   // рівно ValidTo
    [InlineData("2025-12-31", false)]  // день до
    [InlineData("2026-07-01", false)]  // день після
    public void IsValidOn_включає_обидві_межі(string date, bool expected)
    {
        var entry = Entry();
        entry.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30));

        // ⚠ Включність меж — не деталь реалізації. Виключний кінець зробив би
        // запис нечинним у той самий день, яким його закрили: користувач, що
        // заповнює звіт за 30 червня, не знайшов би дозволу, чинного «до 30
        // червня».
        Assert.Equal(expected, entry.IsValidOn(DateOnly.Parse(date, CultureInfo.InvariantCulture)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidFrom_означає_чинність_від_початку()
    {
        var entry = Entry();
        entry.SetValidity(null, new DateOnly(2026, 6, 30));

        Assert.True(entry.IsValidOn(new DateOnly(1990, 1, 1)));
        Assert.True(entry.IsValidOn(new DateOnly(2026, 6, 30)));
        Assert.False(entry.IsValidOn(new DateOnly(2026, 7, 1)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidTo_означає_чинність_без_обмеження()
    {
        var entry = Entry();
        entry.SetValidity(new DateOnly(2026, 1, 1), null);

        Assert.False(entry.IsValidOn(new DateOnly(2025, 12, 31)));
        Assert.True(entry.IsValidOn(new DateOnly(2026, 1, 1)));
        Assert.True(entry.IsValidOn(new DateOnly(2099, 12, 31)));

        // Порожнє вікно — помилка вводу, а не «нічого не чинне»: запис, який
        // не чинний ніколи, неможливо ні обрати, ні пояснити.
        var error = Assert.Throws<DomainException>(
            () => entry.SetValidity(new DateOnly(2026, 6, 30), new DateOnly(2026, 1, 1)));
        Assert.Equal("ECR-REG-0422", error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SoftDelete_не_видаляє_запис_фізично()
    {
        var entry = Entry();

        entry.SoftDelete();

        // ⚠ У комірках лежить Id запису, а не його текст (ФВ-8.8). Фізичне
        // видалення перетворило б історію на набір чисел без підписів — і
        // поданий два роки тому звіт перестав би читатися.
        Assert.True(entry.IsDeleted);
        Assert.False(entry.IsActive);
        Assert.Equal("PERMIT_7001", entry.Code);
        Assert.NotNull(entry.DisplayL10n);

        // Вікно чинності лишається як було: видалення не переписує історію.
        Assert.True(entry.IsValidOn(new DateOnly(2026, 3, 1)));
    }

    private static RegistryEntry Entry()
        => new(
            registryDefId: 1,
            EcrCode.Create("PERMIT_7001"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit 7001" }));
}
