// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncJobTests.DayCopy.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Q-1 (людина 06.11, варіант «а»): шаблони <c>…_Auto_Day</c> / <c>…_Manual_Day</c> — добові КОПІЇ кореневих подій із
/// власними ID. Синк бере лише кореневі <c>_Auto</c>/<c>_Manual</c>; копія не пишеться ні як своя, ні як братня.
/// </summary>
/// <remarks>
/// Мутації: прибрати ранній вихід <c>IsDayCopy(entity.Code)</c> у <c>ExecuteAsync</c> — червоніє D1 (копія пише
/// EF-E1D); прибрати <c>entities.RemoveAll(IsDayCopy)</c> у <c>ReadSiblingsAsync</c> — червоніє D2 (копія з рангом 1
/// «перемагає» корінь, рядка немає); прибрати <c>!IsDayCopy(e.TemplateName)</c> у фільтрі власних подій — червоніє D3.
/// </remarks>
public sealed partial class SourceEventSyncJobTests
{
    [Theory]
    [InlineData("Flare_Auto_Day", true)]
    [InlineData("Flare_Manual_Day", true)]
    [InlineData("flare_manual_day", true)]
    [InlineData("Flare_Auto", false)]
    [InlineData("Flare_Manual", false)]
    [InlineData("FlareDay", false)]
    [InlineData(null, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "Q-1")]
    public void Q1_Добова_копія_розпізнається_за_суфіксом_шаблону(string? template, bool expected)
        => Assert.Equal(expected, SourceEventTemplateOrder.IsDayCopy(template));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-1")]
    public async Task Q1_D1_Шаблон_Day_не_синхронізується_копія_з_іншим_ID_не_подвоює_рядок()
    {
        await using var stand = await ArrangeAsync();
        var day = await AddSiblingAsync(stand, "_Auto_Day");
        try
        {
            var source = new FakeEventSource();
            source.ByTemplate[stand.EntityCode] = new SourceEventResult([Ev("E1", Start, End, volume: 10m)], false, null);
            source.ByTemplate[day.Code] = new SourceEventResult([Ev("E1D", Start, End, volume: 10m)], false, null);

            await RunAsync(stand, source);
            await RunAsync(stand, source, entityId: day.EntityId);

            Assert.Equal(["EF-E1"], (await RowsAsync(stand)).Select(r => r.RowKey));
            Assert.Empty(await LinksOfMapAsync(day.MapId));

            // Копію джерело навіть не питали: синк шаблону `_Day` завершується до читання.
            Assert.Equal(stand.EntityCode, source.LastQuery!.Template);
        }
        finally
        {
            await DeactivateAsync(day.EntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-1")]
    public async Task Q1_D2_Копія_Day_з_тим_самим_ID_не_забирає_подію_в_кореня()
    {
        await using var stand = await ArrangeAsync();
        var day = await AddSiblingAsync(stand, "_Auto_Day");
        try
        {
            var source = new FakeEventSource();
            source.ByTemplate[stand.EntityCode] = new SourceEventResult([Ev("E1", Start, End, volume: 10m)], false, null);
            source.ByTemplate[day.Code] = new SourceEventResult([Ev("E1", Start, End, volume: 10m)], false, null);

            await RunAsync(stand, source);

            Assert.Equal(["EF-E1"], (await RowsAsync(stand)).Select(r => r.RowKey));
            Assert.Single(await LinksAsync(stand));
            Assert.Empty(await LinksOfMapAsync(day.MapId));
        }
        finally
        {
            await DeactivateAsync(day.EntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-1")]
    public async Task Q1_D3_Подія_з_шаблоном_Day_у_відповіді_кореня_не_пишеться()
    {
        await using var stand = await ArrangeAsync();
        var copy = new SourceEvent(
            "E1D", "Flare_Manual_Day", "Flaring HP", Start, End, null, null, null,
            [new SourceEventAttribute("Volume", SourceEventAttributeScope.Event, 10m, null, "Sm3")]);
        var source = new FakeEventSource(Ev("E1", Start, End, volume: 10m), copy);

        await RunAsync(stand, source);

        Assert.Equal(["EF-E1"], (await RowsAsync(stand)).Select(r => r.RowKey));
        Assert.Equal(["E1"], (await LinksAsync(stand)).Select(l => l.SourceEventId));
    }
}
