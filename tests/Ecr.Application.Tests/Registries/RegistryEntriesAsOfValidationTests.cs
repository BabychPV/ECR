// tests/Ecr.Application.Tests/Registries/RegistryEntriesAsOfValidationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Обов'язковий <c>asOf</c> перевіряється, а не резолвиться тихо в
/// <c>0001-01-01</c> (аудит 2026-09-16, §9).
/// </summary>
/// <remarks>
/// ⛔ Параметр оголошений обов'язковим за ЗМІСТОМ: довідники темпоральні, і
/// «поточний» набір записів залежить від дати періоду, а не від «сьогодні»
/// (ФВ-8.5). Але <c>[FromQuery] DateOnly</c> без значення дає <c>default</c> —
/// дату, на яку не чинний жоден темпоральний запис. Клієнт отримував порожній
/// список і жодної підказки, чого саме він не надіслав: «довідник порожній» і
/// «ви забули дату» виглядали однаково.
/// </remarks>
public sealed class RegistryEntriesAsOfValidationTests
{
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryEntryCache _cache = Substitute.For<IRegistryEntryCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public RegistryEntriesAsOfValidationTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 9 }
                .Permission(GetRegistryEntriesHandler.Permission)
                .Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Відсутній_asOf_відхиляється_а_не_дає_порожній_перелік()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync("PERMIT", default, parentEntryId: null, default));

        Assert.Equal(Ecr.Domain.Errors.ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Contains("asOf", error.Message, StringComparison.Ordinal);
        Assert.Equal("err.ECR-REQ-0422.asOfRequired", error.Details!["messageKey"]);

        // ⛔ Перевірка йде ДО походу в базу: «довідника немає» було б іншою
        // відповіддю на те саме питання і послало б шукати не туди.
        await _registries.DidNotReceive().FindDefinitionAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private GetRegistryEntriesHandler Handler()
        => new(_registries, new RegistryResolver(), _cache, _access, _user);
}
