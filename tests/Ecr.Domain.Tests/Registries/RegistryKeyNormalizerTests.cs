// tests/Ecr.Domain.Tests/Registries/RegistryKeyNormalizerTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Registries;

/// <summary>
/// Серверний нормалізатор ключа на спільній фікстурі (FEATURE-REGISTRY-TABLES §4.2, RT-02).
/// </summary>
/// <remarks>
/// ⛔ Той самий файл читає клієнтський <c>normalizeKey.test.ts</c>. Копія випадків тут розійшлася б
/// із клієнтською при першій правці — і обидва набори лишалися б зеленими.
/// </remarks>
public sealed class RegistryKeyNormalizerTests
{
    private static readonly JsonElement Fixture = Load();

    public static TheoryData<string> CaseIds()
    {
        var data = new TheoryData<string>();
        foreach (var testCase in Fixture.GetProperty("cases").EnumerateArray())
        {
            data.Add(testCase.GetProperty("id").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Випадок_фікстури_дає_очікуваний_канонічний_рядок_і_хеш(string id)
    {
        var testCase = Case(id);
        var expected = Expected(testCase);

        var actual = Normalize(testCase);

        Assert.Equal(expected, actual);

        var hashes = Fixture.GetProperty("sha256");
        if (expected is null)
        {
            Assert.False(hashes.TryGetProperty(id, out _), $"«{id}» без ключа не має хеша.");
            return;
        }

        var hash = Convert.ToHexStringLower(RegistryKeyNormalizer.Hash(actual!));
        Assert.Equal(hashes.GetProperty(id).GetString(), hash);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Фікстура_повна_ідентифікатори_унікальні_хеш_на_кожен_ключ()
    {
        var cases = Fixture.GetProperty("cases").EnumerateArray().ToList();
        var ids = cases.Select(c => c.GetProperty("id").GetString()!).ToList();

        Assert.True(cases.Count >= 40, $"Випадків {cases.Count}, потрібно ≥ 40.");
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        // Хеш є рівно для тих випадків, що дають ключ: зайвий або забутий хеш у фікстурі видно тут.
        var withKey = cases.Where(c => c.GetProperty("canonical").ValueKind != JsonValueKind.Null)
            .Select(c => c.GetProperty("id").GetString()!).Order(StringComparer.Ordinal);
        var hashed = Fixture.GetProperty("sha256").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(withKey, hashed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Групи_same_дають_один_ключ_а_distinct_різні()
    {
        foreach (var group in Fixture.GetProperty("same").EnumerateArray())
        {
            var keys = group.EnumerateArray().Select(id => Normalize(Case(id.GetString()!))).ToList();
            Assert.All(keys, Assert.NotNull);
            Assert.Single(keys.Distinct(StringComparer.Ordinal));
        }

        foreach (var group in Fixture.GetProperty("distinct").EnumerateArray())
        {
            var keys = group.EnumerateArray().Select(id => Normalize(Case(id.GetString()!))).ToList();
            Assert.All(keys, Assert.NotNull);
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Значення_не_того_типу_і_порожній_ключ_відхиляються()
    {
        // Помилка програміста, а не користувача: тип частини задає опис ключа.
        Assert.Throws<ArgumentException>(() => RegistryKeyNormalizer.NormalizePart(CellDataType.Decimal, 1.5d));
        Assert.Throws<ArgumentException>(() => RegistryKeyNormalizer.NormalizePart(CellDataType.String, 42));
        Assert.Throws<ArgumentException>(() => RegistryKeyNormalizer.NormalizePart(CellDataType.Lookup, "162"));
        Assert.Throws<ArgumentException>(() => RegistryKeyNormalizer.NormalizePart(CellDataType.Formula, "x"));
        Assert.Throws<ArgumentException>(() => RegistryKeyNormalizer.Canonical([]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Дата_і_ціле_приймаються_в_усіх_типах_домену()
    {
        Assert.Equal("D:2026-09-27", RegistryKeyNormalizer.NormalizePart(CellDataType.Date, new DateOnly(2026, 9, 27)));
        Assert.Equal("D:2026-09-27", RegistryKeyNormalizer.NormalizePart(
            CellDataType.Date, new DateTime(2026, 9, 27, 23, 59, 0, DateTimeKind.Utc)));
        Assert.Equal("N:5", RegistryKeyNormalizer.NormalizePart(CellDataType.Int, 5));
        Assert.Equal("L:162", RegistryKeyNormalizer.NormalizePart(CellDataType.Lookup, 162));
    }

    private static string? Normalize(JsonElement testCase)
    {
        var parts = testCase.GetProperty("parts").EnumerateArray()
            .Select(p => ToPart(p[0].GetString()!, p[1]))
            .ToList();

        return RegistryKeyNormalizer.Canonical(parts, testCase.GetProperty("ignoreCase").GetBoolean());
    }

    /// <summary>Значення з JSON у той CLR-тип, у якому його тримає домен.</summary>
    private static RegistryKeyPart ToPart(string typeName, JsonElement value)
    {
        var type = Enum.Parse<CellDataType>(typeName);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new RegistryKeyPart(type, null);
        }

        object clr = type switch
        {
            CellDataType.String => value.GetString()!,
            CellDataType.Int => long.Parse(value.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture),
            CellDataType.Decimal => decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
            CellDataType.Bool => value.GetBoolean(),
            CellDataType.Date => DateOnly.FromDateTime(
                DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
            CellDataType.Lookup or CellDataType.Unit => value.GetInt64(),
            _ => throw new InvalidOperationException($"Тип {typeName} не буває частиною ключа."),
        };

        return new RegistryKeyPart(type, clr);
    }

    private static string? Expected(JsonElement testCase)
    {
        var canonical = testCase.GetProperty("canonical");
        return canonical.ValueKind == JsonValueKind.Null
            ? null
            : string.Join(RegistryKeyNormalizer.Separator, canonical.EnumerateArray().Select(p => p.GetString()));
    }

    private static JsonElement Case(string id)
        => Fixture.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);

    private static JsonElement Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(TestFixtures.Path("registry-key-normalization.json")));
        return document.RootElement.Clone();
    }
}
