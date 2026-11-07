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

    /// <summary>
    /// F13-p192 (P3): перевизначений запит кореня повертає корені й копії <c>_Day</c> УЖЕ прив'язаних подій (рядки,
    /// записані до рішення Q-1). Копія не додає нічого й не видаляє нічого; корені звіряються як завжди.
    /// </summary>
    /// <remarks>
    /// Мутація: прибрати виключення <c>dayCopyOnlyIds</c> із зв'язків, що йдуть у планувальник, — зв'язок E2 стає
    /// кандидатом <c>Gone</c>, рядок EF-E2 видаляється — червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-1")]
    public async Task Q1_D4_Копія_Day_прив_язаної_події_у_відповіді_кореня_нічого_не_видаляє_й_не_додає()
    {
        await using var stand = await ArrangeAsync();
        var e1 = Ev("E1", Start, End, volume: 10m);
        var e2 = Ev("E2", Start.AddHours(1), End.AddHours(1), volume: 20m);
        var e3 = Ev("E3", Start.AddHours(2), End.AddHours(2), volume: 30m);
        var source = new FakeEventSource(e1, e2, e3);
        await RunAsync(stand, source);
        Assert.Equal(["EF-E1", "EF-E2", "EF-E3"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));

        // Другий прогін: E2 прийшла лише як копія `_Day` (той самий ID), E9 — копія без кореня, E3 зникла, E4 — новий корінь.
        var e4 = Ev("E4", Start.AddHours(3), End.AddHours(3), volume: 40m);
        source.Result = new SourceEventResult(
            [e1, e2 with { TemplateName = "Flare_Auto_Day" }, Ev("E9", Start.AddHours(4), End.AddHours(4), volume: 90m) with { TemplateName = "Flare_Manual_Day" }, e4],
            false,
            null);
        await RunAsync(stand, source);

        Assert.Equal(["EF-E1", "EF-E2", "EF-E4"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(["E1", "E2", "E4"], (await LinksAsync(stand)).Select(l => l.SourceEventId).Order(StringComparer.Ordinal));
        Assert.NotEmpty(await CellsAsync(stand, "EF-E2"));
    }
}
