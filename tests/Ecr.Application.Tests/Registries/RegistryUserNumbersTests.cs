// tests/Ecr.Application.Tests/Registries/RegistryUserNumbersTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Число текстом у записі довідника — за мовою користувача на межі Application
/// (<see cref="RegistryUserNumbers"/>); writer і синк лишаються інваріантними.
/// </summary>
public sealed class RegistryUserNumbersTests
{
    private const int RegistryId = 31;
    private const int QtyId = 311;
    private const int NameId = 312;

    [Theory]
    [InlineData("ru", "12,5", "12.5")]
    [InlineData("en", "12,5", "12.5")]
    [InlineData("ru", "1 234,5", "1234.5")]
    [InlineData("en", "1,234.5", "1234.5")]
    [InlineData("ru", "1,234", "1.234")]
    [InlineData("en", "1234.5", "1234.5")]
    public void Текст_числового_поля_читається_за_мовою(string language, string text, string expected)
    {
        var values = RegistryUserNumbers.Parse(
            Registry(), new Dictionary<string, object?> { ["QTY"] = text }, NumberCulture.ForLanguage(language));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), Assert.IsType<decimal>(values["QTY"]));
    }

    [Fact]
    public void Неоднозначне_в_en_відмова_з_обома_прочитаннями()
    {
        var error = Assert.Throws<Ecr.Application.Errors.BusinessRuleException>(() => RegistryUserNumbers.Parse(
            Registry(), new Dictionary<string, object?> { ["QTY"] = "1,234" }, NumberCulture.ForLanguage("en")));

        Assert.Equal("ECR-REG-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REG-0422.valueNotNumber", error.Details!["messageKey"]);
        Assert.Equal("QTY", error.Details["fieldCode"]);
        Assert.Equal("ambiguousSeparator", error.Details["reason"]);
        Assert.Equal("1234", error.Details["asGroup"]);
        Assert.Equal("1.234", error.Details["asDecimal"]);
    }

    [Fact]
    public void Не_текст_і_нечислові_поля_без_змін()
    {
        using var json = JsonDocument.Parse("{\"n\":12.5,\"s\":\"12,5\"}");
        var number = json.RootElement.GetProperty("n");
        var raw = new Dictionary<string, object?>
        {
            ["QTY"] = number,
            ["NAME"] = json.RootElement.GetProperty("s"),
            ["UNKNOWN"] = "12,5",
        };

        var values = RegistryUserNumbers.Parse(Registry(), raw, NumberCulture.ForLanguage("ru"));

        Assert.Equal(number, values["QTY"]);
        Assert.Equal(raw["NAME"], values["NAME"]);
        Assert.Equal("12,5", values["UNKNOWN"]);
    }

    /// <summary>
    /// Регресія: синк (S7) пише через writer машинний запис — під користувачем із ru «12.5»
    /// лишається 12.5, культура користувача writer'а не стосується.
    /// </summary>
    [Fact]
    public async Task Синк_через_writer_під_ru_лишається_інваріантним()
    {
        var registries = Substitute.For<IRegistryStore>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(5);
        user.Language.Returns("ru");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc));
        var uow = Substitute.For<IUnitOfWork>();
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var definition = Registry();
        registries.FindDefinitionByIdAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(definition);
        var entry = new RegistryEntry(RegistryId, EcrCode.Create("E1"), Text("E1"), 5, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        SetId(entry, 77L);
        registries.FindEntryAsync(77L, Arg.Any<CancellationToken>()).Returns(entry);
        registries.ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryValue>());
        RegistryValue? written = null;
        registries.When(r => r.AddValue(Arg.Any<RegistryValue>())).Do(c => written = c.Arg<RegistryValue>());

        var writer = new RegistryEntryWriter(registries, uow, Substitute.For<IAuditWriter>(), user, clock);
        var result = await writer.UpdateAsync(
            new RegistryEntryUpdateBatch(RegistryId, [new RegistryEntryUpdate(77L, new Dictionary<string, object?> { ["QTY"] = "12.5" })]),
            CancellationToken.None);

        Assert.True(result.Applied, string.Join(", ", result.Errors));
        Assert.Equal(12.5m, written!.ValueNumeric);
    }

    private static RegistryDef Registry()
    {
        var definition = new RegistryDef(EcrCode.Create("NUM"), Text("Numbers"), isTemporal: false);
        SetId(definition, RegistryId);

        var qty = new RegistryFieldDef(RegistryId, EcrCode.Create("QTY"), Text("QTY"), CellDataType.Decimal, 1);
        SetId(qty, QtyId);
        var name = new RegistryFieldDef(RegistryId, EcrCode.Create("NAME"), Text("NAME"), CellDataType.String, 2);
        SetId(name, NameId);
        definition.AddField(qty);
        definition.AddField(name);
        return definition;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static void SetId(RegistryEntry entity, long id)
        => typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entity, id);
}
