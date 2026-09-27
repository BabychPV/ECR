// tests/Ecr.Domain.Tests/Dictionaries/RegistryEntryKeyTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Dictionaries;

/// <summary>
/// Похідний рядок унікальності ключа (RT-01, FEATURE-REGISTRY-TABLES §3.2):
/// вікно й живість — дзеркало запису, а не окремі факти.
/// </summary>
public sealed class RegistryEntryKeyTests
{
    private const int KeyDefId = 5;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Рядок_дзеркалить_вікно_і_живість_запису()
    {
        var entry = Entry(41);
        entry.SetValidity(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));

        var key = new RegistryEntryKey(entry, KeyDefId, Hash(1), "1D-2 · 370 Winter");

        Assert.Equal(41, key.RegistryEntryId);
        Assert.Equal(new DateOnly(2025, 1, 1), key.ValidFromKey);
        Assert.Equal(new DateOnly(2026, 1, 1), key.ValidTo);
        Assert.True(key.IsLive);
        Assert.Equal(entry.Window, key.Window);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Вікно_від_початку_дає_сентинел_а_не_null()
    {
        // ⛔ NULL в унікальному індексі SQL Server вважає рівними — сентинел
        // робить унікальність нетемпорального довідника повною.
        var key = new RegistryEntryKey(Entry(41), KeyDefId, Hash(1), "A");

        Assert.Equal(DateOnly.MinValue, key.ValidFromKey);
        Assert.Null(key.ValidTo);
        Assert.Equal(ValidityWindow.Always, key.Window);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Видалення_й_обмін_виводять_рядок_з_унікальності_а_перерахунок_повертає()
    {
        var entry = Entry(41);
        var key = new RegistryEntryKey(entry, KeyDefId, Hash(1), "A");

        // Перша фаза обміну ключами в пакеті (§4.3).
        key.Retire();
        Assert.False(key.IsLive);

        key.Recompute(entry, Hash(2), "B");
        Assert.True(key.IsLive);
        Assert.Equal(Hash(2), key.KeyHash);

        entry.SoftDelete();
        key.Recompute(entry, Hash(2), "B");
        Assert.False(key.IsLive);

        entry.Restore();
        key.Recompute(entry, Hash(2), "B");
        Assert.True(key.IsLive);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Хеш_не_SHA256_довжини_відхиляється(int length)
    {
        Assert.Throws<ArgumentException>(
            () => new RegistryEntryKey(Entry(41), KeyDefId, new byte[length], "A"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Хеш_копіюється_а_не_ділиться_з_викликачем()
    {
        var hash = Hash(1);
        var key = new RegistryEntryKey(Entry(41), KeyDefId, hash, "A");

        hash[0] ^= 0xFF;

        Assert.NotEqual(hash, key.KeyHash);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Довгий_текст_ключа_обрізається_до_межі_колонки()
    {
        var exact = new string('x', RegistryEntryKey.MaxKeyTextLength);
        var longer = new string('y', RegistryEntryKey.MaxKeyTextLength + 100);

        Assert.Equal(exact, new RegistryEntryKey(Entry(41), KeyDefId, Hash(1), exact).KeyText);

        var cut = new RegistryEntryKey(Entry(41), KeyDefId, Hash(1), longer).KeyText;
        Assert.Equal(RegistryEntryKey.MaxKeyTextLength, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => new RegistryEntryKey(Entry(41), KeyDefId, Hash(1), " "));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "RT-01")]
    public void Перерахунок_чужим_записом_відхиляється()
    {
        var key = new RegistryEntryKey(Entry(41), KeyDefId, Hash(1), "A");

        Assert.Throws<ArgumentException>(() => key.Recompute(Entry(42), Hash(1), "A"));
    }

    private static RegistryEntry Entry(long id)
    {
        var entry = new RegistryEntry(3, EcrCode.Create($"E{id}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "e" }));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        return entry;
    }

    private static byte[] Hash(byte seed) => Enumerable.Repeat(seed, RegistryEntryKey.KeyHashLength).ToArray();
}
