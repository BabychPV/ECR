// tests/Ecr.Domain.Tests/Dictionaries/RegistryEntryTests.cs
using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Dictionaries;

/// <summary>
/// Темпоральність запису довідника (ФВ-8.5). Вікно — **напівінтервал**
/// <c>[ValidFrom, ValidTo)</c>: нижня межа включна, верхня ні.
/// </summary>
/// <remarks>
/// ⚠ Раніше тут перевірялася включність ОБОХ меж. Змінено на кроці
/// <c>I.10</c> (директива ПК-1 №05 §7): у джерелі верхня межа — <c>datetime</c>
/// з трьома різними «нескінченностями», і закрите подання вимагає зберігати
/// «останню мить» (<c>2024-12-31 23:59:59</c>). Саме з неї в корпусі виросли
/// 24 рядки з <c>12:59:59</c> — на вигляд ті самі, на одинадцять годин
/// коротші.
/// </remarks>
public sealed class RegistryEntryTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("2026-01-01", true)]   // рівно ValidFrom — чинний
    [InlineData("2026-06-30", true)]   // останній чинний день
    [InlineData("2026-07-01", false)]  // рівно ValidTo — уже НЕ чинний
    [InlineData("2025-12-31", false)]  // день до
    [Trait("Requirement", "ФВ-8.5")]
    public void IsValidOn_бере_нижню_межу_включно_а_верхню_ні(string date, bool expected)
    {
        var entry = Entry();

        // Перше півріччя 2026-го: чинний по 30 червня, з 1 липня — ні.
        entry.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 7, 1));

        // ⚠ Помилка на день тут не падає і не логується: запис просто
        // з'являється (або зникає) зі списку на добу раніше, ніж має. Знайти
        // її можна лише того дня, коли межа проходить через звітний період.
        Assert.Equal(expected, entry.IsValidOn(DateOnly.Parse(date, CultureInfo.InvariantCulture)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidFrom_означає_чинність_від_початку()
    {
        var entry = Entry();
        entry.SetValidity(null, new DateOnly(2026, 7, 1));

        Assert.True(entry.IsValidOn(new DateOnly(1990, 1, 1)));
        Assert.True(entry.IsValidOn(new DateOnly(2026, 6, 30)));
        Assert.False(entry.IsValidOn(new DateOnly(2026, 7, 1)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Відсутній_ValidTo_означає_чинність_без_обмеження()
    {
        var entry = Entry();
        entry.SetValidity(new DateOnly(2026, 1, 1), null);

        // ⛔ Саме так у наших даних виглядають сентинели джерела `9999-02-20`
        // і `9999-12-31`: відсутністю межі, а не датою.
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
    public void Рівні_межі_відхиляються_бо_вікно_порожнє()
    {
        var entry = Entry();

        // ⛔ Відмінність напівінтервала від закритого подання, і вона
        // непомітна: у закритому `30.06 … 30.06` означало ОДИН чинний день,
        // у напівінтервалі — жодного. Пропустити рівність означало б завести
        // запис, якого ніколи немає у списку, і шукати причину в правах.
        var error = Assert.Throws<DomainException>(
            () => entry.SetValidity(new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 30)));
        Assert.Equal("ECR-REG-0422", error.ErrorCode);

        // Один чинний день записується як `[30.06, 01.07)`.
        entry.SetValidity(new DateOnly(2026, 6, 30), new DateOnly(2026, 7, 1));
        Assert.True(entry.IsValidOn(new DateOnly(2026, 6, 30)));
        Assert.Equal(new DateOnly(2026, 6, 30), entry.Window.LastValidDay);
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
