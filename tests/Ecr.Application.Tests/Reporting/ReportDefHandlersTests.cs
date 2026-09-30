// tests/Ecr.Application.Tests/Reporting/ReportDefHandlersTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// Борг локалізації (`contracts/localization-debt.md`): заведення й публікація
/// опису звіту та версій — <c>CreateReportDefHandler</c>,
/// <c>CreateReportVersionHandler</c>, <c>PublishReportVersionHandler</c> не
/// мали жодного тесту раніше.
/// </summary>
public sealed class ReportDefHandlersTests
{
    private readonly IReportDefinitionStore _definitions = Substitute.For<IReportDefinitionStore>();
    private readonly IRepository<ReportDef, int> _defs = Substitute.For<IRepository<ReportDef, int>>();
    private readonly IRepository<ReportVersion, int> _versions = Substitute.For<IRepository<ReportVersion, int>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IReportViewGenerator _views = Substitute.For<IReportViewGenerator>();

    public ReportDefHandlersTests()
    {
        _clock.UtcNow.Returns(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Report.EditDefinition").Build());
    }

    private static IReadOnlyList<ReportColumnCommand> Columns() => [new("Value", "number")];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зайнятий_код_звіту_дає_ключ_каталогу()
    {
        _definitions.ExistsAsync("RPT1", Arg.Any<CancellationToken>()).Returns(true);

        var command = new CreateReportDefCommand(
            "RPT1", new Dictionary<string, string> { ["en"] = "Report" }, IsRegulatory: false,
            "1", Columns(), Rules: null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new CreateReportDefHandler(_definitions, _defs, _versions, _uow, _clock, _access, _user)
                .HandleAsync(command, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportDefDuplicate, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-4091.code", error.Details!["messageKey"]);
        Assert.Equal("RPT1", error.Details["code"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Версія_для_відсутнього_опису_дає_ключ_каталогу()
    {
        _defs.FindAsync(77, Arg.Any<CancellationToken>()).Returns((ReportDef?)null);

        var command = new CreateReportVersionCommand("1", Columns(), Rules: null);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => new CreateReportVersionHandler(_defs, _versions, _uow, _clock, _access, _user)
                .HandleAsync(77, command, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportNotFound, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0404.def", error.Details!["messageKey"]);
        Assert.Equal("77", error.Details["reportDefId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Публікація_відсутньої_версії_дає_ключ_каталогу()
    {
        _versions.FindAsync(55, Arg.Any<CancellationToken>()).Returns((ReportVersion?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => new PublishReportVersionHandler(_versions, _uow, _access, _user, _views)
                .HandleAsync(reportDefId: 1, reportVersionId: 55, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportNotFound, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0404.version", error.Details!["messageKey"]);
        Assert.Equal("55", error.Details["reportVersionId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Публікація_версії_чужого_опису_дає_ключ_каталогу()
    {
        var version = new ReportVersion(1, "1", "[]", """{"rowSource":"CalculationResults"}""", _clock.UtcNow);
        _versions.FindAsync(55, Arg.Any<CancellationToken>()).Returns(version);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => new PublishReportVersionHandler(_versions, _uow, _access, _user, _views)
                .HandleAsync(reportDefId: 2, reportVersionId: 55, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportNotFound, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0404.versionWrongDef", error.Details!["messageKey"]);
        Assert.Equal("1", error.Details["versionDefId"]);
        Assert.Equal("2", error.Details["reportDefId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-10.2")]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Публікація_генерує_вʼюху_звіту_в_тій_самій_транзакції()
    {
        var version = new ReportVersion(7, "1.0", "[]", """{"rowSource":"CalculationResults"}""", _clock.UtcNow);
        _versions.FindAsync(55, Arg.Any<CancellationToken>()).Returns(version);

        var trace = new List<string>();
        var inTransaction = false;
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                inTransaction = true;
                await call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None);
                inTransaction = false;
            });
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                trace.Add($"save:{inTransaction}");
                return 1;
            });
        _views.GenerateAsync(Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                trace.Add($"views:{call.Arg<int?>()}:{inTransaction}");
                return Task.CompletedTask;
            });

        var result = await new PublishReportVersionHandler(_versions, _uow, _access, _user, _views)
            .HandleAsync(reportDefId: 7, reportVersionId: 55, CancellationToken.None);

        Assert.Equal("Published", result.Status);

        // ⛔ Вʼюха — лише цього звіту (не «всіх»), ПІСЛЯ збереження статусу
        // (процедура бере лише опубліковані версії) і в ТІЙ САМІЙ транзакції:
        // відмова генерації відкочує публікацію, а не лишає версію без вʼюхи.
        Assert.Equal(["save:True", "views:7:True"], trace);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Відмова_генерації_вʼюхи_не_ховається_від_того_хто_публікує()
    {
        var version = new ReportVersion(7, "1.0", "[]", """{"rowSource":"CalculationResults"}""", _clock.UtcNow);
        _versions.FindAsync(55, Arg.Any<CancellationToken>()).Returns(version);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
        _views.GenerateAsync(7, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("view")));

        // Публікація, яка «вдалася», а SSRS її не бачить, гірша за відмову.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PublishReportVersionHandler(_versions, _uow, _access, _user, _views)
                .HandleAsync(reportDefId: 7, reportVersionId: 55, CancellationToken.None));
    }
}
