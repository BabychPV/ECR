// tests/Ecr.Domain.Tests/RowActivityFilterSpecTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests;

/// <summary>D-425: формат фільтра активності рядків і правило «усі колонки статусу — активний запис».</summary>
/// <remarks>
/// Мутації: <c>Admits</c> повертає <c>true</c> для <c>null</c> — червоний «порожній статус»; <c>Any</c> замість
/// <c>All</c> — червоний «одна з двох колонок».
/// </remarks>
public sealed class RowActivityFilterSpecTests
{
    [Theory]
    [InlineData("LAND_GENERIC_EQUIPSTATUS:Running", "LAND_GENERIC_EQUIPSTATUS", "Running")]
    [InlineData("A:b-c_1", "A", "b-c_1")]
    public void Коректний_формат_розбирається_на_довідник_і_запис(string value, string registry, string entry)
    {
        var spec = RowActivityFilterSpec.TryParse(value);

        Assert.NotNull(spec);
        Assert.Equal(registry, spec.RegistryCode);
        Assert.Equal(entry, spec.EntryCode);
        Assert.Equal(value, spec.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Running")]
    [InlineData("A:B:C")]
    [InlineData(":B")]
    [InlineData("A:")]
    [InlineData("A B:C")]
    public void Зіпсований_формат_не_розбирається(string? value)
        => Assert.Null(RowActivityFilterSpec.TryParse(value));

    [Fact]
    public void Фільтр_на_методології_ставиться_знімається_і_відхиляє_зіпсований_формат()
    {
        var methodology = new Methodology(
            EcrCode.Create("ECW_X"), new LocalizedText(new Dictionary<string, string> { ["en"] = "X" }));
        Assert.Null(methodology.RowActivityFilter);

        methodology.SetRowActivityFilter("REG:Running");
        Assert.Equal("REG:Running", methodology.RowActivityFilter);

        var error = Assert.Throws<DomainException>(() => methodology.SetRowActivityFilter("Running"));
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("REG:Running", methodology.RowActivityFilter);

        methodology.SetRowActivityFilter(null);
        Assert.Null(methodology.RowActivityFilter);
    }

    [Fact]
    public void Рядок_проходить_лише_коли_усі_колонки_статусу_активний_запис_а_порожній_статус_ні()
    {
        Assert.True(RowActivityFilterSpec.Admits(["77"], "77"));
        Assert.True(RowActivityFilterSpec.Admits(["77", "77"], "77"));
        Assert.False(RowActivityFilterSpec.Admits(["78"], "77"));
        Assert.False(RowActivityFilterSpec.Admits(["77", "78"], "77"));
        Assert.False(RowActivityFilterSpec.Admits([null], "77"));
        Assert.False(RowActivityFilterSpec.Admits([], "77"));
    }
}
