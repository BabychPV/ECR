// tests/Ecr.Api.Tests/MethodologyEffectiveDateClashErrorTests.cs
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Аудит L7-08 (ФВ-13.3): друга паралельна публікація версії методології на ту саму дату
/// (порушення <c>UQ_MV_Effective</c>) віддається клієнтові як 409 <c>ECR-CALC-0409</c>, а не голим 500.
/// </summary>
/// <remarks>
/// Мутація (2026-10-05): прибрати арм <c>UQ_MV_Effective</c> з <c>ExceptionHandlingMiddleware</c> -
/// червоний перший тест (500).
/// </remarks>
public sealed class MethodologyEffectiveDateClashErrorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.3")]
    public async Task Порушення_UQ_MV_Effective_віддається_як_409_ECR_CALC_0409_з_ключем_каталогу_і_датою()
    {
        var body = await ProblemReservedMembersTests.ProblemTextAsync(new DbUpdateException(
            "save failed",
            new InvalidOperationException(
                "Cannot insert duplicate key row in object 'calc.MethodologyVersion' with unique index "
                + "'UQ_MV_Effective'. The duplicate key value is (7, 2026-10-01).")));

        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("2026-10-01", problem.GetProperty("effectiveFrom").GetString());
        Assert.Equal("err.ECR-CALC-0409.effectiveDateTakenNoVersion", problem.GetProperty("messageKey").GetString());
        Assert.False(problem.TryGetProperty("version", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.3")]
    public async Task Інше_порушення_унікальності_не_маскується_під_0409()
    {
        var body = await ProblemReservedMembersTests.ProblemTextAsync(new DbUpdateException(
            "save failed", new InvalidOperationException("duplicate key 'UQ_MethodologyVersion'")));

        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.NotEqual("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
    }
}
