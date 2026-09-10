using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Q-178: <c>PreviewImportHandler</c>/<c>ApplyImportHandler</c> тепер перевіряють
/// грант на документ ТАК САМО, як <see cref="ExportDocumentHandler"/> (`A7-55`)
/// — не тому, що без цього була доведена діра (глибший захист
/// `ExcelImporter` → `CanEditSliceAsync` уже відмовляв чужим коміркам), а
/// заради того самого дизайну входу: функціональне право каже «цей
/// користувач узагалі імпортує», грант — «У ЦЕЙ документ».
/// </summary>
public sealed class ImportAccessTests
{
    private const long DocumentId = 700;

    private readonly IExcelImporter _importer = Substitute.For<IExcelImporter>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ImportAccessTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.Import").Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_перегляд_імпорту_відхиляється()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => new PreviewImportHandler(_importer, _access, _user)
                .HandleAsync(DocumentId, Stream.Null, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _importer.DidNotReceiveWithAnyArgs().PreviewAsync(0, null!, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task З_грантом_перегляд_імпорту_дозволений()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        var preview = new ImportPreview("token-1", [], [], []);
        _importer.PreviewAsync(DocumentId, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(preview);

        var result = await new PreviewImportHandler(_importer, _access, _user)
            .HandleAsync(DocumentId, Stream.Null, CancellationToken.None);

        Assert.Equal("token-1", result.PreviewToken);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_застосування_імпорту_відхиляється()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => new ApplyImportHandler(_importer, _jobs, _access, _user)
                .HandleAsync(DocumentId, "token-1", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        await _importer.DidNotReceiveWithAnyArgs().ApplyAsync(0, null!, CancellationToken.None);
    }
}
