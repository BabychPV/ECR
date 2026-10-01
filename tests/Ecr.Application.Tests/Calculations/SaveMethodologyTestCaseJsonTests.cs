// tests/Ecr.Application.Tests/Calculations/SaveMethodologyTestCaseJsonTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// P2 (walk-reg 2026-10-01): тест золотого набору з некоректним JSON не зберігається
/// з 200 (після чого publish/simulate падали 500), а відхиляється <c>ECR-CALC-0422</c>
/// з іменем тесту й поля.
/// </summary>
/// <remarks>
/// Аудит ent3: <c>"{}"</c> розбирається в не-null вхід з <c>Arguments = null</c> (симуляція — 500), тож вхід
/// перевіряється й за формою; причина відмови — лише шлях JSON, без тексту винятку з іменами типів .NET.
/// Мутаційні докази: прибрати <c>BrokenPath</c> → рядки <c>{}</c>, <c>[null]</c>, порожній і повторений код
/// червоні; повернути <c>error.Message</c> у <c>reason</c> → <see cref="Причина_відмови_без_внутрішніх_імен_типів"/> червоний.
/// </remarks>
public sealed class SaveMethodologyTestCaseJsonTests
{
    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public SaveMethodologyTestCaseJsonTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(SaveMethodologyTestCaseHandler.Permission).Build());
        _drafts.FindVersionAsync(5, Arg.Any<CancellationToken>()).Returns(
            new MethodologyVersion(1, "1.0.0", CalculationLevel.Configuration, 9, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    private Task<Ecr.Application.Calculations.Dto.MethodologyTestCaseDto> SaveAsync(string input, string expected) =>
        new SaveMethodologyTestCaseHandler(_drafts, _uow, _access, _user)
            .HandleAsync(5, "t1", input, expected, 0m, CancellationToken.None);

    /// <summary>Мінімальний вхід, з яким прогін тесту не впаде: період перевіряє публікація, не запис.</summary>
    private const string MinimalInput = "{\"periodKey\":{\"value\":0},\"arguments\":[]}";

    [Theory]
    [InlineData("{ not json", "{}", "inputJson")]
    [InlineData("[1]", "{}", "inputJson")]
    [InlineData("null", "{}", "inputJson")]
    [InlineData("{}", "{}", "inputJson")]
    [InlineData("{\"periodKey\":{\"value\":202601}}", "{}", "inputJson")]
    [InlineData("{\"arguments\":[null]}", "{}", "inputJson")]
    [InlineData("{\"arguments\":[{\"value\":1}]}", "{}", "inputJson")]
    [InlineData("{\"arguments\":[{\"argumentCode\":\"A\"},{\"argumentCode\":\"a\"}]}", "{}", "inputJson")]
    [InlineData(MinimalInput, "{\"tons\":\"abc\"}", "expectedJson")]
    [InlineData(MinimalInput, "null", "expectedJson")]
    public async Task Некоректний_json_дає_0422_з_тестом_і_полем_і_нічого_не_пише(string input, string expected, string field)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SaveAsync(input, expected));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.testCaseJsonInvalid", error.Details!["messageKey"]);
        Assert.Equal("t1", error.Details["testCode"]);
        Assert.Equal(field, error.Details["field"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _drafts.DidNotReceive().Add(Arg.Any<MethodologyTestCaseEntity>());
    }

    [Theory]
    [InlineData("[1]", "$")]
    [InlineData("{}", "$.arguments")]
    [InlineData("{\"arguments\":[null]}", "$.arguments[0]")]
    [InlineData("{\"arguments\":[{\"argumentCode\":\"A\"},{\"argumentCode\":\" \"}]}", "$.arguments[1].argumentCode")]
    [InlineData("{\"arguments\":[{\"argumentCode\":\"A\",\"value\":\"x\"}]}", "$.arguments[0].value")]
    public async Task Причина_відмови_без_внутрішніх_імен_типів(string input, string path)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SaveAsync(input, "{}"));

        // Лише шлях (і позиція для синтаксису), а не текст винятку System.Text.Json з іменами типів .NET.
        var reason = Assert.IsType<string>(error.Details!["reason"]);
        Assert.StartsWith(path, reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Ecr.", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Ecr.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MinimalInput)]
    [InlineData("{\"periodKey\":{\"value\":202601},\"arguments\":[{\"argumentCode\":\"A\",\"value\":1.5},{\"argumentCode\":\"B\",\"valueString\":\"x\"}]}")]
    public async Task Коректний_json_зберігається(string input)
    {
        await SaveAsync(input, "{\"tons\":1.5}");

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}