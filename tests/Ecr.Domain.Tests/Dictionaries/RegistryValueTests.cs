// tests/Ecr.Domain.Tests/Dictionaries/RegistryValueTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Dictionaries;

/// <summary>
/// Відмови типізованого значення довідника несуть messageKey і сирі
/// підстановки: без них подробиця доїжджала б клієнту українським реченням.
/// </summary>
public sealed class RegistryValueTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Нечислове_значення_числового_поля_відхиляється_з_ключем()
    {
        var value = new RegistryValue(registryEntryId: 1, registryFieldDefId: 2);

        var error = Assert.Throws<DomainException>(() => value.Set(CellDataType.Decimal, "abc", unitId: null));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.valueNotNumber", error.Details!["messageKey"]);
        Assert.Equal("abc", error.Details["value"]);
        Assert.Equal("Decimal", error.Details["dataType"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Одиниця_на_нечисловому_полі_відхиляється_з_ключем()
    {
        var value = new RegistryValue(registryEntryId: 1, registryFieldDefId: 2);

        var error = Assert.Throws<DomainException>(() => value.Set(CellDataType.String, "x", unitId: 7));

        Assert.Equal("err.ECR-REG-0422.unitOnNonNumeric", error.Details!["messageKey"]);
        Assert.Equal("String", error.Details["dataType"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Неоднозначна_дата_довідника_розбирається_як_у_комірки_документа()
    {
        // ⛔ До цього тесту `RegistryValue.AsDate` розбирав рядок голим
        // `DateTime.TryParse(s, InvariantCulture, AdjustToUniversal, …)`:
        // InvariantCulture читає `M.d.yyyy`, тож «01.02.2026» стало б 2 січня,
        // а не 1 лютого — та сама тиха перестановка дня й місяця, що й в
        // аудиті 2026-09-16 §8.2 для `CellValueReader.Date()`, лише не
        // виправлена тут. Виправлення — той самий `CellDateParser`
        // (`Ecr.Domain.Services`), тому результат мусить збігатися з
        // `CellValueReaderTests.Дата_у_природному_порядку_дня_і_місяця_не_переставляється`
        // для того самого вхідного рядка: 1 лютого, не 2 січня.
        var value = new RegistryValue(registryEntryId: 1, registryFieldDefId: 2);

        value.Set(CellDataType.Date, "01.02.2026", unitId: null);

        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), value.ValueDate);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Однозначна_ISO_дата_довідника_розбирається_правильно()
    {
        var value = new RegistryValue(registryEntryId: 1, registryFieldDefId: 2);

        value.Set(CellDataType.Date, "2026-01-15", unitId: null);

        Assert.Equal(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), value.ValueDate);
    }
}
