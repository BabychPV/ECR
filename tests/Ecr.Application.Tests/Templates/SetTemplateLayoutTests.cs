using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// D-474 (2): розкладка версії шаблону змінюється лише в чернетці й лише доки у версії немає документів;
/// після зміни оновлюється реєстр розкладок.
/// </summary>
public sealed class SetTemplateLayoutTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _cache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly ITemplateLayoutRegistry _layouts = Substitute.For<ITemplateLayoutRegistry>();

    private readonly TemplateVersion _draft = new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    public SetTemplateLayoutTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.LockVersionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(TemplateVersionStatus.Draft);
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
    }

    private SetTemplateLayoutHandler Handler()
        => new(_store, _cache, _audit, _uow, _clock, _access, _user, _layouts);

    [Fact]
    [Trait("Requirement", "D-474")]
    public void Нова_версія_має_розкладку_копія_на_місяць()
        => Assert.Equal(TemplateLayout.PerMonthCopies, _draft.Layout);

    [Fact]
    [Trait("Requirement", "D-474")]
    public async Task Чернетка_без_документів_змінює_розкладку_пише_аудит_і_оновлює_реєстр()
    {
        var result = await Handler().HandleAsync(1, TemplateLayout.YearWithMonthColumns, CancellationToken.None);

        Assert.Equal(TemplateLayout.YearWithMonthColumns, result.Layout);
        Assert.Equal(TemplateLayout.YearWithMonthColumns, _draft.Layout);
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.OldJson!.Contains("PerMonthCopies", StringComparison.Ordinal)
                && r.NewJson!.Contains("YearWithMonthColumns", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _cache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
        await _layouts.Received(1).RefreshAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public async Task Те_саме_значення_нічого_не_пише()
    {
        var result = await Handler().HandleAsync(1, TemplateLayout.PerMonthCopies, CancellationToken.None);

        Assert.Equal(TemplateLayout.PerMonthCopies, result.Layout);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(null!, default);
        await _layouts.DidNotReceiveWithAnyArgs().RefreshAsync(default);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public async Task Версія_з_документами_відмовляє_ECR_SCHM_0409()
    {
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(1, TemplateLayout.YearWithMonthColumns, CancellationToken.None));

        Assert.Equal(ErrorCodes.BreakingChange, error.ErrorCode);
        Assert.Equal("err.ECR-SCHM-0409.templateLayoutBreaking", error.Details!["messageKey"]);
        Assert.Equal(TemplateLayout.PerMonthCopies, _draft.Layout);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public async Task Опублікована_версія_відмовляє_і_розкладка_не_змінюється()
    {
        _draft.Publish(publishedByUserId: 7, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(1, TemplateLayout.YearWithMonthColumns, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Equal(TemplateLayout.PerMonthCopies, _draft.Layout);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public async Task Версію_опублікували_поки_правка_готувалась_відмова_під_блокуванням()
    {
        _store.LockVersionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(TemplateVersionStatus.Published);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Handler().HandleAsync(1, TemplateLayout.YearWithMonthColumns, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Equal(TemplateLayout.PerMonthCopies, _draft.Layout);
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public async Task Невідоме_значення_розкладки_це_422()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(1, (TemplateLayout)9, CancellationToken.None));

        Assert.Equal(ErrorCodes.TemplateInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.templateLayoutUnknown", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait("Requirement", "D-474")]
    public async Task Без_права_Template_Edit_відмова_і_нічого_не_читається()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(1, TemplateLayout.YearWithMonthColumns, CancellationToken.None));

        await _store.DidNotReceiveWithAnyArgs().GetWithStructureAsync(default, default);
        Assert.Equal(TemplateLayout.PerMonthCopies, _draft.Layout);
    }
}
