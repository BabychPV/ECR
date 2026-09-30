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
/// CSV-імпорт довідника: число в числовому полі читається за мовою
/// користувача (рішення 2026-09-29), а не <c>Convert.ToDecimal(…, Invariant)</c>.
/// </summary>
/// <remarks>
/// ⛔ Доти рядок ішов прямо в <c>RegistryValue.Set</c>, а той бере
/// <c>NumberStyles.Number</c>: «12,5» мовчки ставало 125, «1 234,5» —
/// відмовою. Мутація: прибрати розбір у <c>ResolveRowAsync</c> — червоніють усі
/// рядки, крім «1234.5».
/// </remarks>
public sealed class RegistryEntryImportCultureTests
{
    private const int RegistryDefId = 11;
    private const int LimitFieldId = 502;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public RegistryEntryImportCultureTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _clock.UtcNow.Returns(new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _user.CorrelationId.Returns("corr-culture");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(UpsertRegistryEntryHandler.Permission).Build());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("en", "12.5", "12.5")]
    [InlineData("en", "\"1,234.5\"", "1234.5")]
    [InlineData("en", "\"12,5\"", "12.5")]
    [InlineData("ru", "\"1 234,5\"", "1234.5")]
    [InlineData("ru", "\"1234,5\"", "1234.5")]
    [InlineData("ru", "\"1,234\"", "1.234")]
    [InlineData("kz", "\"1 234,5\"", "1234.5")]
    [InlineData("kz", "1234.5", "1234.5")]
    public async Task Число_з_CSV_читається_за_мовою_користувача(string language, string cell, string expected)
    {
        var (report, value) = await ImportAsync(language, cell);

        Assert.Empty(report.Errors);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value.ValueNumeric);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("en", "\"1,234\"")]
    [InlineData("en", "\"1.234,5\"")]
    [InlineData("ru", "\"1,234.5\"")]
    public async Task Неоднозначне_число_з_CSV_відхиляється_рядком(string language, string cell)
    {
        var (report, value) = await ImportAsync(language, cell);

        var error = Assert.Single(report.Errors);
        Assert.Equal("err.ECR-REG-0422.valueNotNumber", error.MessageKey);
        Assert.Equal(1m, value.ValueNumeric);
    }

    private async Task<(RegistryEntryImportReport Report, RegistryValue Value)> ImportAsync(string language, string cell)
    {
        _user.Language.Returns(language);

        var definition = new RegistryDef(EcrCode.Create("PERMITS"), Text("Permits"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryDefId);

        var field = new RegistryFieldDef(definition.Id, EcrCode.Create("LIMIT"), Text("LIMIT"), CellDataType.Decimal, ordinal: 1);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, LimitFieldId);
        definition.AddField(field);

        var entry = new RegistryEntry(definition.Id, EcrCode.Create("E0"), Text("E0"));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, 100L);

        var value = new RegistryValue(entry.Id, field.Id);
        value.Set(CellDataType.Decimal, 1m, null);

        _registries.FindDefinitionAsync("PERMITS", Arg.Any<CancellationToken>()).Returns(definition);
        _registries
            .FindEntriesByCodesAsync(definition.Id, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryEntry>)[entry]);
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[value]);

        var handler = new ImportRegistryEntriesHandler(
            _registries, _audit, _access, _user, _clock, new RegistryEntryWriter(_registries, _uow, _audit, _user, _clock));

        var content = $"code,LIMIT\r\nE0,{cell}\r\n";
        var report = await handler
            .HandleAsync(
                "PERMITS", content, Encoding.UTF8.GetByteCount(content), ImportRegistryEntriesHandler.DefaultMaxBytes,
                dryRun: false, CancellationToken.None)
            .ConfigureAwait(true);

        return (report, value);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
