// tests/Ecr.Domain.Tests/Configuration/PermitTypeDraftTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Чернетка визначення типу дозволу й нові версії (Permits, PM-03, міграція <c>PRM02</c>).
/// </summary>
public sealed class PermitTypeDraftTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Чернетка_зберігається_і_несе_автора_та_момент()
    {
        var type = NewType();

        type.SaveDraft("""{"tables":[]}""", 7, Now);

        Assert.True(type.HasDraft);
        Assert.Equal(("""{"tables":[]}""", (DateTime?)Now, (int?)7), (type.DraftJson, type.UpdatedAt, type.UpdatedByUserId));
    }

    [Fact]
    public void Публікація_знімає_чернетку_а_версія_першої_публікації_не_росте()
    {
        var type = NewType();
        type.SaveDraft("{}", 7, Now);

        type.Publish(7, Now.AddHours(1));

        Assert.False(type.HasDraft);
        Assert.Equal((PermitTypeStatus.Published, 1), (type.Status, type.Version));
    }

    [Fact]
    public void Нова_версія_відкривається_лише_для_опублікованого_типу_і_лише_раз()
    {
        var type = NewType();

        Assert.Throws<InvalidOperationException>(() => type.OpenNewVersion("{}", 7, Now));

        type.Publish(7, Now);
        type.OpenNewVersion("{}", 8, Now.AddDays(1));

        Assert.True(type.HasDraft);
        Assert.Equal(PermitTypeStatus.Published, type.Status);
        Assert.Throws<InvalidOperationException>(() => type.OpenNewVersion("{}", 8, Now.AddDays(2)));
    }

    [Fact]
    public void Повторна_публікація_піднімає_версію_знімає_чернетку_і_оновлює_автора()
    {
        var type = NewType();
        type.Publish(7, Now);
        type.OpenNewVersion("{}", 8, Now.AddDays(1));

        type.Republish(9, Now.AddDays(2));

        Assert.Equal((2, false, (int?)9, (DateTime?)Now.AddDays(2)), (type.Version, type.HasDraft, type.PublishedByUserId, type.PublishedAt));
    }

    [Fact]
    public void Повторна_публікація_неопублікованого_типу_відхиляється()
    {
        var type = NewType();

        Assert.Throws<InvalidOperationException>(() => type.Republish(7, Now));
        Assert.Equal(1, type.Version);
    }

    [Fact]
    public void Назва_і_категорія_застосовуються_до_опублікованого_типу_а_Update_його_не_чіпає()
    {
        var type = NewType();
        type.Publish(7, Now);

        Assert.Throws<InvalidOperationException>(() => type.Update(Text("x"), PermitCategory.Waste));

        type.ApplyDetails(Text("Новий підпис"), PermitCategory.Waste);

        Assert.Equal((PermitCategory.Waste, "Новий підпис"), (type.Category, type.NameL10n.Get("en")));
    }

    [Fact]
    public void Момент_чернетки_має_бути_в_UTC()
    {
        var type = NewType();

        Assert.Throws<ArgumentException>(() => type.SaveDraft("{}", 7, new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Local)));
    }

    private static PermitTypeDef NewType()
        => new(EcrCode.Create("AIR_PERMIT"), Text("Air permit"), PermitCategory.Air, headerRegistryDefId: 5);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
