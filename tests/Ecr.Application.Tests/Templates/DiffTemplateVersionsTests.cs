using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Порівняння версій шаблону: напрям, межа одного шаблону і чесна кількість
/// документів (R-08, R-09, X-12, четвертий раунд UX).
/// </summary>
public sealed class DiffTemplateVersionsTests
{
    private const int Older = 10;
    private const int Newer = 11;
    private const int Foreign = 20;

    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public DiffTemplateVersionsTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        // Старша версія — одна колонка; новіша — та сама плюс `Added`.
        var older = new TemplateBuilder { TemplateVersionId = Older };
        var olderTable = older.Table(older.Sheet("Water"), "Main");
        older.Column(olderTable, "Kept");

        var newer = new TemplateBuilder { TemplateVersionId = Newer };
        var newerTable = newer.Table(newer.Sheet("Water"), "Main");
        newer.Column(newerTable, "Kept");
        newer.Column(newerTable, "Added");

        _metadata.GetAsync(Older, Arg.Any<CancellationToken>()).Returns(older.Build());
        _metadata.GetAsync(Newer, Arg.Any<CancellationToken>()).Returns(newer.Build());

        _store.FindTemplateOfVersionAsync(Older, Arg.Any<CancellationToken>()).Returns(Template(1));
        _store.FindTemplateOfVersionAsync(Newer, Arg.Any<CancellationToken>()).Returns(Template(1));
        _store.FindTemplateOfVersionAsync(Foreign, Arg.Any<CancellationToken>()).Returns(Template(2));

        _store.CountDocumentsAsync(Older, Arg.Any<CancellationToken>()).Returns(37);
        _store.CountDocumentsAsync(Newer, Arg.Any<CancellationToken>()).Returns(0);
    }

    private DiffTemplateVersionsHandler Handler()
        => new(_metadata, _store, new ChangeClassifier(), _access, _user);

    [Theory]
    [InlineData(Newer, Older)]
    [InlineData(Older, Newer)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.3")]
    public async Task Напрям_завжди_від_старшої_версії_до_новішої(int opened, int other)
    {
        // ⛔ R-08: відкрите з НОВОЇ версії порівняння зі старою показувало
        // додану колонку як «Removed» — відповідь навпаки на питання «що
        // зміниться при переході».
        var diff = await Handler().HandleAsync(opened, other, CancellationToken.None);

        Assert.Equal(Older, diff.FromVersionId);
        Assert.Equal(Newer, diff.ToVersionId);

        var change = Assert.Single(diff.Changes);
        Assert.Equal("Water.Main.Added", change.ElementPath);
        Assert.Equal("Added", change.Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Кількість_документів_справжня_і_рахується_від_старшої_версії()
    {
        // ⛔ X-12: тут стояло `hasDocuments ? 1 : 0`.
        var diff = await Handler().HandleAsync(Newer, Older, CancellationToken.None);

        Assert.Equal(37, diff.AffectedDocumentCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Версія_чужого_шаблону_відхиляється_422()
    {
        // ⛔ R-09: порівняння структур двох різних форм за кодами дає збіги
        // імен, а не зміни.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Older, Foreign, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.diffOtherTemplate", error.Details!["messageKey"]);
        await _metadata.DidNotReceive().GetAsync(Foreign, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Неіснуюча_версія_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Older, 999, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0404.templateVersion", error.Details!["messageKey"]);
    }

    private static Template Template(int id)
    {
        var template = new Template(
            EcrCode.Create($"T{id}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "T" }),
            createdByUserId: 1,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(template, id);
        return template;
    }
}
