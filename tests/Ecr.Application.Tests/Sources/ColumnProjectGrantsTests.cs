// tests/Ecr.Application.Tests/Sources/ColumnProjectGrantsTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Грант <c>Manage</c> на кожен проєкт колонки (S3) без розкриття номерів невидимих проєктів (S18) —
/// спільна перевірка мапінгу поля на колонку й прив'язки вікна рядка.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати гілку <c>!SeesDocumentsOf</c> → <see cref="Невидимий_проєкт_не_називається_у_відмові"/>
/// червоний; кидати відмову про невидимі одразу, а не після видимих →
/// <see cref="Видимий_проєкт_без_гранта_називається_першим_навіть_за_меншого_невидимого"/> червоний; не кидати
/// відмову про невидимі → <see cref="Невидимий_проєкт_не_називається_у_відмові"/> червоний.
/// </remarks>
public sealed class ColumnProjectGrantsTests
{
    private const int Column = 100;

    private readonly ICollectionStore _sources = Substitute.For<ICollectionStore>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S18")]
    public async Task Невидимий_проєкт_не_називається_у_відмові()
    {
        Projects(42, 41);
        var profile = new AccessBuilder()
            .Permission("Integration.Manage")
            .Grant(ResourceKind.Project, 41, GrantLevel.Manage)
            .Build();

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => ColumnProjectGrants.RequireManageAsync(_sources, profile, Column, default));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-AUTH-0403.columnUsedInHiddenProjects", denied.Details!["messageKey"]);
        Assert.False(denied.Details.ContainsKey("projectId"));
        Assert.DoesNotContain("42", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S18")]
    public async Task Видимий_проєкт_без_гранта_називається_першим_навіть_за_меншого_невидимого()
    {
        Projects(43, 40, 41);
        var profile = new AccessBuilder()
            .Permission("Integration.Manage")
            .Grant(ResourceKind.Project, 41, GrantLevel.Manage)
            .Grant(ResourceKind.Project, 43, GrantLevel.Read)
            .Build();

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => ColumnProjectGrants.RequireManageAsync(_sources, profile, Column, default));

        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", denied.Details!["messageKey"]);
        Assert.Equal("43", denied.Details["projectId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S18")]
    public async Task Заборона_на_проєкт_робить_його_невидимим_навіть_із_грантом()
    {
        Projects(41);
        var profile = new AccessBuilder()
            .Permission("Integration.Manage")
            .Grant(ResourceKind.Project, 41, GrantLevel.Manage)
            .Deny(ResourceKind.Project, 41)
            .Build();

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => ColumnProjectGrants.RequireManageAsync(_sources, profile, Column, default));

        Assert.Equal("err.ECR-AUTH-0403.columnUsedInHiddenProjects", denied.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Manage_на_всі_проєкти_або_колонка_без_проєктів_пропускаються()
    {
        var profile = new AccessBuilder()
            .Permission("Integration.Manage")
            .Grant(ResourceKind.Project, 41, GrantLevel.Manage)
            .Build();

        Projects(41);
        await ColumnProjectGrants.RequireManageAsync(_sources, profile, Column, default);

        Projects();
        await ColumnProjectGrants.RequireManageAsync(_sources, profile, Column, default);
    }

    private void Projects(params int[] ids)
        => _sources.FindProjectIdsUsingColumnAsync(Column, Arg.Any<CancellationToken>()).Returns(ids);
}
