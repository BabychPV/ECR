using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Q-326: механізм структурованого повідомлення прогресу задачі —
/// кодування/декодування конверта (<see cref="JobProgressMessageCodec"/>) і
/// резолв мовою читача (<see cref="JobProgressMessageResolver"/>), окремо від
/// <c>GetJobStatusHandlerTests</c> (права доступу).
/// </summary>
public sealed class JobProgressMessageTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Кодування_і_декодування_повертає_той_самий_ключ_і_параметри()
    {
        var envelope = new JobProgressMessageEnvelope(
            "jobs.recalcFormulasDone",
            new Dictionary<string, string> { ["cells"] = "42" });

        var encoded = JobProgressMessageCodec.Encode(envelope);
        var decoded = JobProgressMessageCodec.TryDecode(encoded, out var result);

        Assert.True(decoded);
        Assert.Equal("jobs.recalcFormulasDone", result.Key);
        Assert.Equal("42", result.Params!["cells"]);
        Assert.Null(result.Inner);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Вкладене_повідомлення_переживає_кодування_і_декодування()
    {
        var inner = new JobProgressMessageEnvelope(
            "jobs.batchProgress", new Dictionary<string, string> { ["ordinal"] = "2", ["total"] = "4" });
        var outer = new JobProgressMessageEnvelope("jobs.phaseMethodologies", Inner: inner);

        var decoded = JobProgressMessageCodec.TryDecode(JobProgressMessageCodec.Encode(outer), out var result);

        Assert.True(decoded);
        Assert.Equal("jobs.phaseMethodologies", result.Key);
        Assert.NotNull(result.Inner);
        Assert.Equal("jobs.batchProgress", result.Inner!.Key);
        Assert.Equal("4", result.Inner.Params!["total"]);
    }

    /// <summary>
    /// ⛔ Мутаційний доказ фолбеку: без цієї перевірки (`raw[0] != '{'`)
    /// `TryDecode` кидав би <c>JsonException</c> на кожному старому прямому
    /// записі (готовий український текст ДО `Q-326`) і на `exportId`
    /// (`ExcelExportJob`, 100 %) — обидва не JSON.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Перерахунок методологій.")]
    [InlineData("4f2c9a10-91d1-4b6a-9c9e-4b8e4b9a2b10")]
    [InlineData("{не валідний json")]
    public void Нерозпізнаний_рядок_не_декодується_як_конверт(string? raw)
    {
        Assert.False(JobProgressMessageCodec.TryDecode(raw, out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_підставляє_параметри_в_шаблон_каталогу_мовою_читача()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "jobs.orphanScanDone", "Rows changed: {changed}");

        var raw = JobProgressMessageCodec.Encode(
            new JobProgressMessageEnvelope(
                "jobs.orphanScanDone", new Dictionary<string, string> { ["changed"] = "7" }));

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", raw, CancellationToken.None);

        Assert.Equal("Rows changed: 7", resolved);
    }

    /// <summary>
    /// Q-326: композиція (`RecalculationJob`'s префікс документа,
    /// `CalculationOrchestrator.PhaseProgress`'s префікс фази) резолвиться
    /// РЕКУРСИВНО — вкладене повідомлення підставляється як <c>{message}</c>
    /// уже резольваним текстом, не сирим ключем.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_розгортає_вкладене_повідомлення_рекурсивно()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("en", "jobs.documentPrefix", "Document {id} ({index} of {count}): {message}")
            .Add("en", "jobs.recalcFormulas", "Recalculating template formulas.");

        var raw = JobProgressMessageCodec.Encode(
            new JobProgressMessageEnvelope(
                "jobs.documentPrefix",
                new Dictionary<string, string> { ["id"] = "5", ["index"] = "1", ["count"] = "3" },
                Inner: new JobProgressMessageEnvelope("jobs.recalcFormulas")));

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", raw, CancellationToken.None);

        Assert.Equal("Document 5 (1 of 3): Recalculating template formulas.", resolved);
    }

    /// <summary>
    /// Не JSON-конверт (стара форма запису чи дані на кшталт <c>exportId</c>)
    /// — резолвер повертає його БЕЗ ЗМІН, не звертаючись до каталогу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_нерозпізнаного_рядка_повертає_його_без_змін()
    {
        var catalog = Substitute.For<IUiStringCatalog>();

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", "export-abc123", CancellationToken.None);

        Assert.Equal("export-abc123", resolved);
        await catalog.DidNotReceiveWithAnyArgs()
            .GetScopedAsync(default!, default, CancellationToken.None);
    }

    /// <summary>
    /// ⛔ Q-304-подібний принцип: збій самого каталогу не має валити читання
    /// стану задачі. Мутаційний доказ — прибери <c>try/catch</c> у
    /// <see cref="JobProgressMessageResolver.ResolveAsync"/>, і цей тест
    /// впаде на необробленому винятку замість повернутого сирого конверта.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Збій_каталогу_повертає_сирий_конверт_а_не_валить_читання()
    {
        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetScopedAsync(Arg.Any<string>(), Arg.Any<UiStringScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<UiStringCatalog>(new InvalidOperationException("каталог недоступний")));

        var raw = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope("jobs.orphanScanChecking"));

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", raw, CancellationToken.None);

        Assert.Equal(raw, resolved);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Резолв_без_повідомлення_повертає_null()
    {
        var catalog = Substitute.For<IUiStringCatalog>();

        var resolved = await JobProgressMessageResolver
            .ResolveAsync(catalog, "en", null, CancellationToken.None);

        Assert.Null(resolved);
    }
}
