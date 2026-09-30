// tests/Ecr.Domain.Tests/Configuration/RegistryKeyDefTests.cs
using System.Reflection;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Інваріанти складеного ключа довідника (RT-01, FEATURE-REGISTRY-TABLES §4.1).
/// </summary>
public sealed class RegistryKeyDefTests
{
    private const int RegistryId = 7;
    private static readonly DateTime Now = new(2026, 9, 27, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Частини_нумеруються_від_одного_в_порядку_полів()
    {
        var key = Key([Field(12, "CASE_NAME"), Field(11, "STREAM")], isPrimary: true);

        // Порядок частин — порядок аргументів REGFIND, а не порядок полів у
        // довіднику: STREAM оголошено першим, але в ключі він другий.
        Assert.Equal(
            new[] { (1, 12), (2, 11) },
            key.Fields.Select(f => ((int)f.Ordinal, f.RegistryFieldDefId)));
        Assert.True(key.IsActive);
        Assert.True(key.IsPrimary);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    [InlineData(0)]
    [InlineData(RegistryKeyDef.MaxFields + 1)]
    public void Ключ_без_полів_або_понад_вісім_відхиляється(int count)
    {
        var fields = Enumerable.Range(1, count).Select(i => Field(i, $"F{i}")).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => Key(fields, isPrimary: false));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Вісім_полів_приймаються()
    {
        var fields = Enumerable.Range(1, RegistryKeyDef.MaxFields).Select(i => Field(i, $"F{i}")).ToArray();

        Assert.Equal(RegistryKeyDef.MaxFields, Key(fields, isPrimary: false).Fields.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Поле_двічі_в_ключі_відхиляється()
    {
        var stream = Field(11, "STREAM");

        Assert.Throws<ArgumentException>(() => Key([stream, Field(12, "CASE_NAME"), stream], isPrimary: false));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Поле_іншого_довідника_або_незбережене_відхиляється()
    {
        Assert.Throws<ArgumentException>(() => Key([Field(11, "STREAM", registryId: RegistryId + 1)], isPrimary: false));
        Assert.Throws<ArgumentException>(() => Key([Field(0, "STREAM")], isPrimary: false));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    [InlineData(CellDataType.String, true)]
    [InlineData(CellDataType.Int, true)]
    [InlineData(CellDataType.Decimal, true)]
    [InlineData(CellDataType.Bool, true)]
    [InlineData(CellDataType.Date, true)]
    [InlineData(CellDataType.Lookup, true)]
    [InlineData(CellDataType.Unit, true)]
    [InlineData(CellDataType.Formula, false)]
    [InlineData(CellDataType.Calculated, false)]
    public void Частиною_ключа_буває_лише_збережене_значення(CellDataType type, bool allowed)
    {
        Assert.Equal(allowed, RegistryKeyDef.AllowsPartType(type));

        var field = Field(11, "F", type);
        if (allowed)
        {
            Assert.Single(Key([field], isPrimary: false).Fields);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Key([field], isPrimary: false));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Первинний_ключ_вимагає_обовязкових_полів_альтернативний_ні()
    {
        var optional = Field(11, "LEGACY_ID", CellDataType.Int, isRequired: false);

        // D-153: REGFIND мусить мати повну адресу; в альтернативному ключі
        // null-частина просто не бере участі в перевірці.
        Assert.Throws<ArgumentException>(() => Key([optional], isPrimary: true));
        Assert.False(Key([optional], isPrimary: false).IsPrimary);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Склад_і_прапорці_після_створення_не_змінюються()
    {
        // ⛔ Зміна складу, IgnoreCase чи IsPrimary мовчки перебудувала б хеш
        // кожного запису. Дозволені мутації — лише назва й вмикання.
        var mutators = typeof(RegistryKeyDef)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(["Rename", "SetActive"], mutators);
        Assert.All(
            typeof(RegistryKeyDef).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => Assert.False(p.SetMethod?.IsPublic ?? false, $"{p.Name} має публічний сеттер"));

        var key = Key([Field(11, "STREAM")], isPrimary: false);
        key.SetActive(false);
        key.Rename(Text("Renamed"));

        Assert.False(key.IsActive);
        Assert.True(key.IgnoreCase);
    }

    private static RegistryKeyDef Key(IReadOnlyList<RegistryFieldDef> fields, bool isPrimary)
        => new(RegistryId, EcrCode.Create("PK"), Text("Primary"), fields, isPrimary, ignoreCase: true, createdByUserId: 3, Now);

    private static RegistryFieldDef Field(
        int id, string code, CellDataType type = CellDataType.String, bool isRequired = true, int registryId = RegistryId)
    {
        var field = new RegistryFieldDef(registryId, EcrCode.Create(code), Text(code), type, id);
        field.Update(Text(code), id, isRequired);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, id);
        return field;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
