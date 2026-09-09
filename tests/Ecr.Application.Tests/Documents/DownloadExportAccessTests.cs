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
/// Q-180: <c>DownloadExportHandler</c> тепер перевіряє грант на документ, з
/// якого побудована книга — раніше єдиним захистом була непередбачуваність
/// 128-бітного <c>exportId</c>.
/// </summary>
public sealed class DownloadExportAccessTests
{
    private const string ExportId = "export-1";
    private const long DocumentId = 700;

    private readonly IExportStore _exports = Substitute.For<IExportStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public DownloadExportAccessTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.Export").Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Невідомий_exportId_дає_404_а_не_403()
    {
        _exports.FindAsync(ExportId, Arg.Any<CancellationToken>())
            .Returns((ExportedBook?)null);

        var notFound = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(ExportId, CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", notFound.ErrorCode);

        // ⚠ Грант перевіряється лише ПІСЛЯ того, як книга знайдена — інакше
        // прострочений/невідомий exportId завжди впав би на «немає гранта»,
        // ховаючи справжню причину за помилковим 403 (той самий порядок, що
        // й у Q-179).
        await _access.DidNotReceiveWithAnyArgs()
            .CanReadDocumentAsync(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_документ_завантаження_відхиляється()
    {
        _exports.FindAsync(ExportId, Arg.Any<CancellationToken>())
            .Returns(new ExportedBook(DocumentId, [1, 2, 3]));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(ExportId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task З_грантом_книга_віддається()
    {
        var content = new byte[] { 9, 8, 7 };
        _exports.FindAsync(ExportId, Arg.Any<CancellationToken>())
            .Returns(new ExportedBook(DocumentId, content));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        var result = await Handler().HandleAsync(ExportId, CancellationToken.None);

        Assert.Equal(content, result);
    }

    private DownloadExportHandler Handler() => new(_exports, _access, _user);
}
