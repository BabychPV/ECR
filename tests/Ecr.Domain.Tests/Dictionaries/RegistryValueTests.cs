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
}
