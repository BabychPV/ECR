// tests/Ecr.Domain.Tests/Dictionaries/RegistryValueLengthTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Xunit;

namespace Ecr.Domain.Tests.Dictionaries;

/// <summary>
/// Стелі текстових колонок довідника перевіряє домен, а не SQL Server (аудит 2026-10-03, L4-03 = L5-10).
/// </summary>
/// <remarks>
/// Мутаційний доказ (2026-10-03, локальний worktree): у <c>RegistryValue.Set</c> прибрати перевірку
/// <c>text.Length &gt; MaxStringLength</c> → <see cref="Рядок_довший_за_стелю_відхиляється_з_ключем_і_межею"/>
/// червоний (винятку немає).
/// </remarks>
public sealed class RegistryValueLengthTests
{
    [Fact]
    public void Рядок_рівно_на_стелі_приймається()
    {
        var value = new RegistryValue(registryEntryId: 1, registryFieldDefId: 2);

        value.Set(CellDataType.String, new string('x', RegistryValue.MaxStringLength), unitId: null);

        Assert.Equal(RegistryValue.MaxStringLength, value.ValueString!.Length);
    }

    [Fact]
    public void Рядок_довший_за_стелю_відхиляється_з_ключем_і_межею()
    {
        var value = new RegistryValue(registryEntryId: 1, registryFieldDefId: 2);

        var error = Assert.Throws<DomainException>(
            () => value.Set(CellDataType.String, new string('x', RegistryValue.MaxStringLength + 1), unitId: null));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.valueTooLong", error.Details!["messageKey"]);
        Assert.Equal(1000, error.Details["max"]);
        Assert.Equal(1001, error.Details["length"]);
        Assert.Null(value.ValueString);
    }

    [Fact]
    public void Задовгий_зовнішній_ідентифікатор_чи_шлях_відхиляється_до_збереження()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RegistryExternalKey(1, 2, new string('g', RegistryExternalKey.MaxExternalIdLength + 1)));

        var key = new RegistryExternalKey(1, 2, "guid");
        var now = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => key.MarkSynced(new string('p', RegistryExternalKey.MaxExternalPathLength + 1), now));
        Assert.Null(key.ExternalPath);

        key.MarkSynced(new string('p', RegistryExternalKey.MaxExternalPathLength), now);
        Assert.Equal(RegistryExternalKey.MaxExternalPathLength, key.ExternalPath!.Length);
    }
}
