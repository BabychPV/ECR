using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Аудит 7, ділянка Y5: CSV довідника пройшов «експорт → Excel → зберегти → імпорт».
/// Excel при збереженні переписує значення регіональним форматом — що з цього доходить
/// до довідника і що бачить людина в перевірці (dry-run).
/// </summary>
public sealed class RegistryEntryImportExcelRoundTripTests
{
    private const int RegistryDefId = 11;
    private const int DueFieldId = 601;
    private const int ContractFieldId = 602;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private RegistryValue? _contract;

    public RegistryEntryImportExcelRoundTripTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _clock.UtcNow.Returns(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _user.CorrelationId.Returns("corr-y5");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(UpsertRegistryEntryHandler.Permission).Build());
    }

    // ── Y5-02 ────────────────────────────────────────────────────────────────

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Y5-02")]
    [InlineData("4/1/2024")]
    [InlineData("01/04/2024")]
    public async Task Неоднозначна_слеш_дата_з_Excel_відхиляється_рядком_і_нічого_не_змінює(string cell)
    {
        // ⛔ Excel з регіоном en-US пише 2024-04-01 як «4/1/2024». Доти це ставало
        // 4 СІЧНЯ (точний формат d/M), а звіт казав лише «оновлено: 1».
        // Мутація: прибрати `IsAmbiguousSlashDate` у `CellDateParser.TryParse` — помилки
        // немає, а дата в довіднику — 4 січня.
        var (report, value) = await ImportAsync(cell, dryRun: false);

        var error = Assert.Single(report.Errors);
        Assert.Equal("DUE", error.Field);
        Assert.Equal("err.ECR-REG-0422.valueAmbiguousDate", error.MessageKey);
        Assert.NotNull(error.Params);
        Assert.Equal(cell, error.Params["value"]);
        Assert.False(report.Applied);
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), value.ValueDate);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Y5-02")]
    public async Task Американська_дата_з_днем_понад_12_не_читається_як_M_d()
    {
        // ⛔ «4/13/2024» точного формату d/M не має, і фолбек Invariant читав його як
        // 13 квітня — поруч із «4/1/2024» = 4 січня в тому самому файлі.
        var (report, _) = await ImportAsync("4/13/2024", dryRun: true);

        var error = Assert.Single(report.Errors);
        Assert.Equal("err.ECR-REG-0422.valueNotDate", error.MessageKey);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Y5-02")]
    [InlineData("2024-04-01")]
    [InlineData("01.04.2024")]
    public async Task Однозначна_дата_незміненого_запису_не_дає_змін(string cell)
    {
        var (report, _) = await ImportAsync(cell, dryRun: true);

        Assert.Empty(report.Errors);
        Assert.Equal(0, report.Updated);
    }

    // ── Y5-07 ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Y5-07")]
    public async Task Перевірка_показує_старе_й_нове_значення_поля_і_нічого_не_пише()
    {
        // ⛔ Excel при збереженні CSV зрізав провідні нулі: «0012» → «12». Доти перевірка
        // казала лише «оновлено: 1», і людина, яка нічого не міняла, не бачила що саме.
        // Мутація: не додавати зміни в `reported` — `Changes` порожній.
        var (report, _) = await ImportAsync("2024-04-01", dryRun: true, contractCell: "12");

        Assert.Empty(report.Errors);
        Assert.False(report.Applied);
        Assert.Equal(1, report.Updated);
        var change = Assert.Single(report.Changes);
        Assert.Equal(new RegistryEntryImportChange(2, "E0", "CONTRACT", "0012", "12"), change);
        Assert.False(report.ChangesTruncated);
        Assert.Equal("0012", _contract!.ValueString);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Y5-07")]
    public async Task Дата_в_переліку_змін_інваріантним_форматом()
    {
        var (report, _) = await ImportAsync("13/4/2024", dryRun: true);

        var change = Assert.Single(report.Changes);
        Assert.Equal("DUE", change.Field);
        Assert.Equal("2024-04-01", change.OldValue);
        Assert.Equal("2024-04-13", change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Finding", "Y5-07")]
    public async Task Незмінений_файл_не_дає_змін_у_звіті()
    {
        var (report, _) = await ImportAsync("2024-04-01", dryRun: true);

        Assert.Empty(report.Changes);
        Assert.Equal(0, report.Updated);
    }

    // ── Стенд ────────────────────────────────────────────────────────────────

    private async Task<(RegistryEntryImportReport Report, RegistryValue Value)> ImportAsync(
        string dueCell, bool dryRun, string contractCell = "0012")
    {
        var definition = new RegistryDef(EcrCode.Create("PERMITS"), Text("Permits"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryDefId);

        var due = new RegistryFieldDef(definition.Id, EcrCode.Create("DUE"), Text("DUE"), CellDataType.Date, ordinal: 1);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(due, DueFieldId);
        definition.AddField(due);

        var contract = new RegistryFieldDef(
            definition.Id, EcrCode.Create("CONTRACT"), Text("CONTRACT"), CellDataType.String, ordinal: 2);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(contract, ContractFieldId);
        definition.AddField(contract);

        var entry = new RegistryEntry(definition.Id, EcrCode.Create("E0"), Text("E0"));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, 100L);

        var dueValue = new RegistryValue(entry.Id, due.Id);
        dueValue.Set(CellDataType.Date, new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), null);

        var contractValue = new RegistryValue(entry.Id, contract.Id);
        contractValue.Set(CellDataType.String, "0012", null);
        _contract = contractValue;

        _registries.FindDefinitionAsync("PERMITS", Arg.Any<CancellationToken>()).Returns(definition);
        _registries
            .FindEntriesByCodesAsync(definition.Id, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryEntry>)[entry]);
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[dueValue, contractValue]);

        var handler = new ImportRegistryEntriesHandler(
            _registries, _audit, _access, _user, _clock, new RegistryEntryWriter(_registries, _uow, _audit, _user, _clock));

        var content = $"code,DUE,CONTRACT\r\nE0,{dueCell},{contractCell}\r\n";
        var report = await handler
            .HandleAsync(
                "PERMITS", content, Encoding.UTF8.GetByteCount(content), ImportRegistryEntriesHandler.DefaultMaxBytes,
                dryRun, CancellationToken.None)
            .ConfigureAwait(true);

        return (report, dueValue);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
