// tests/Ecr.Domain.Tests/External/RegistrySyncPolicyTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.External;

/// <summary>
/// Політика синку довідника з AF (<c>D-212</c>, PR-2): налаштування на
/// <see cref="SourceEntity"/>, позначка зникнення на <see cref="RegistryExternalKey"/>,
/// вимкнення запису <see cref="RegistryEntry"/>.
/// </summary>
/// <remarks>
/// ⚠ Швидкий рівень доказу: правила без бази. Що політика доходить до бази й
/// вимагає прав, доводить <c>SourceRegistryPolicyEndpointTests</c>.
/// </remarks>
public sealed class RegistrySyncPolicyTests
{
    private static readonly DateTime T1 = new(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 9, 2, 3, 0, 0, DateTimeKind.Utc);

    private static SourceEntity Entity() => new(1, "Flare_01", RegistrySourceKind.External);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Нова_сутність_має_найменш_руйнівну_політику()
    {
        var entity = Entity();

        Assert.Equal(RegistryMissingPolicy.MarkOrphaned, entity.OnMissingInSource);
        Assert.Null(entity.ValidFromAttribute);
        Assert.Null(entity.ValidToAttribute);
        Assert.False(entity.ValidToInclusive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Політика_ставиться_цілком_з_обрізаними_іменами_атрибутів()
    {
        var entity = Entity();

        entity.ConfigureRegistrySync(RegistryMissingPolicy.Deactivate, " StartDate ", "EndDate", validToInclusive: true);

        Assert.Equal(RegistryMissingPolicy.Deactivate, entity.OnMissingInSource);
        Assert.Equal("StartDate", entity.ValidFromAttribute);
        Assert.Equal("EndDate", entity.ValidToAttribute);
        Assert.True(entity.ValidToInclusive);

        // Повторне налаштування замінює, а не доповнює: null — «не синхронізувати».
        entity.ConfigureRegistrySync(RegistryMissingPolicy.Ignore, null, null, validToInclusive: false);

        Assert.Equal(RegistryMissingPolicy.Ignore, entity.OnMissingInSource);
        Assert.Null(entity.ValidFromAttribute);
        Assert.Null(entity.ValidToAttribute);
        Assert.False(entity.ValidToInclusive);
    }

    public static TheoryData<RegistryMissingPolicy, string?, string?, bool> Invalid() => new()
    {
        { (RegistryMissingPolicy)7, null, null, false },
        { RegistryMissingPolicy.MarkOrphaned, "", null, false },
        { RegistryMissingPolicy.MarkOrphaned, "   ", null, false },
        { RegistryMissingPolicy.MarkOrphaned, null, "", false },
        { RegistryMissingPolicy.MarkOrphaned, new string('a', 201), null, false },
        { RegistryMissingPolicy.MarkOrphaned, null, new string('b', 201), false },
        { RegistryMissingPolicy.MarkOrphaned, "Start", null, true },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Невалідна_політика_відмовляє_422_і_не_змінює_сутність(
        RegistryMissingPolicy policy, string? from, string? to, bool inclusive)
    {
        var entity = Entity();
        entity.ConfigureRegistrySync(RegistryMissingPolicy.Deactivate, "Keep", null, validToInclusive: false);

        var ex = Assert.Throws<DomainException>(() => entity.ConfigureRegistrySync(policy, from, to, inclusive));

        Assert.Equal("ECR-REQ-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.registrySyncPolicyInvalid", ex.Details!["messageKey"]);
        Assert.Equal(RegistryMissingPolicy.Deactivate, entity.OnMissingInSource);
        Assert.Equal("Keep", entity.ValidFromAttribute);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Атрибут_рівно_на_межі_ширини_колонки_приймається()
    {
        var entity = Entity();
        var name = new string('x', SourceEntity.MaxValidityAttributeLength);

        entity.ConfigureRegistrySync(RegistryMissingPolicy.MarkOrphaned, name, name, validToInclusive: false);

        Assert.Equal(name, entity.ValidFromAttribute);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Позначка_зникнення_тримає_ПЕРШИЙ_момент_і_знімається()
    {
        var key = new RegistryExternalKey(1, 2, "guid-1");
        Assert.Null(key.MissingInSourceSince);

        key.MarkMissing(T1);
        key.MarkMissing(T2);

        // «Відколи», а не «коли востаннє»: другий прогін не зсуває момент.
        Assert.Equal(T1, key.MissingInSourceSince);

        key.ClearMissing();
        Assert.Null(key.MissingInSourceSince);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Перев_язка_міняє_ідентифікатор_і_знімає_позначку_зникнення()
    {
        var key = new RegistryExternalKey(1, 2, "guid-old");
        key.MarkMissing(T1);

        key.Relink("guid-new");

        Assert.Equal("guid-new", key.ExternalId);
        Assert.Null(key.MissingInSourceSince);
        Assert.Equal(1, key.RegistryEntryId);
        Assert.Equal(2, key.DataSourceId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Перев_язка_на_порожній_чи_задовгий_ідентифікатор_відмовляє_і_не_змінює_ключ()
    {
        var key = new RegistryExternalKey(1, 2, "guid-old");
        key.MarkMissing(T1);

        Assert.ThrowsAny<ArgumentException>(() => key.Relink(" "));
        Assert.ThrowsAny<ArgumentException>(() => key.Relink(new string('g', RegistryExternalKey.MaxExternalIdLength + 1)));

        Assert.Equal("guid-old", key.ExternalId);
        Assert.Equal(T1, key.MissingInSourceSince);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Запис_вимикається_і_вмикається_без_видалення()
    {
        var entry = new RegistryEntry(1, EcrCode.Create("FL01"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Flare" }));

        entry.Deactivate();
        Assert.False(entry.IsActive);
        Assert.False(entry.IsDeleted);

        entry.Activate();
        Assert.True(entry.IsActive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-212")]
    public void Видалений_запис_не_вмикається_через_Activate()
    {
        var entry = new RegistryEntry(1, EcrCode.Create("FL01"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Flare" }));
        entry.SoftDelete();

        Assert.Throws<InvalidOperationException>(entry.Activate);
        Assert.False(entry.IsActive);
        Assert.True(entry.IsDeleted);
    }
}
