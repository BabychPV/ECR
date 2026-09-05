// tests/Ecr.Domain.Tests/Configuration/RegistryDefTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>Визначення довідника: версійність даних і зміна master-джерела.</summary>
public sealed class RegistryDefTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.2")]
    public void Зміна_запису_інкрементує_DataRevision()
    {
        var registry = Registry();
        var before = registry.DataRevision;

        registry.BumpDataRevision();

        // ⚠ DataRevision — це ключ кешу довідника на клієнті. Без інкременту
        // grid показував би старий перелік дозволів після того, як довідник
        // уже змінили: користувач обирав би значення, якого більше немає.
        Assert.Equal(before + 1, registry.DataRevision);

        registry.BumpDataRevision();
        Assert.Equal(before + 2, registry.DataRevision);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void DataRevision_не_змінюється_від_читання()
    {
        var registry = Registry();
        var before = registry.DataRevision;

        // Читання будь-яких полів — і жодного руху ревізії. Інакше кеш
        // інвалідувався б на кожен перегляд, тобто не існував би зовсім.
        _ = registry.Code;
        _ = registry.NameL10n;
        _ = registry.IsTemporal;
        _ = registry.SourceKind;
        _ = registry.Fields;
        _ = registry.DefinitionVersion;

        Assert.Equal(before, registry.DataRevision);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-8.1")]
    public void Перемикання_SourceKind_змінює_master()
    {
        var registry = Registry();

        // External: master — зовнішня система, локальні правки лише
        // доповнюють. Local: master — ECR; Hybrid — обидва.
        registry.SwitchSource(RegistrySourceKind.External);
        Assert.Equal(RegistrySourceKind.External, registry.SourceKind);

        registry.SwitchSource(RegistrySourceKind.Local);
        Assert.Equal(RegistrySourceKind.Local, registry.SourceKind);

        // ⚠ Перемикання саме по собі ревізію даних НЕ рухає: змінився
        // власник довідника, а не його вміст. Рухати її тут означало б
        // інвалідувати кеш там, де нічого не змінилося — і привчити клієнта
        // ігнорувати ревізію.
        Assert.Equal(0, registry.DataRevision);

        // Заборона перемикання у відкритому періоді (ФВ-8.9,
        // `ECR-REG-0422`) — правило use-case, не сутності: періодів вона не
        // бачить і бачити не повинна.
        Assert.Equal(RegistrySourceKind.Local, registry.SourceKind);
    }

    private static RegistryDef Registry()
        => new(
            EcrCode.Create("PERMITS"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permits" }),
            isTemporal: true);
}
