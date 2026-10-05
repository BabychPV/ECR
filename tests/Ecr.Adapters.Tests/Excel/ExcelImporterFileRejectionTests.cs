using System.Text;
using Ecr.Adapters.Excel;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// `F-07`, `F-24` (UX-PASS, четвертий раунд): файл, що не є книгою, і
/// прострочений перегляд — зрозуміла відмова з ключем каталогу, а не 500 і не
/// українське речення під англійським заголовком.
/// </summary>
public sealed class ExcelImporterFileRejectionTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F-07")]
    [InlineData("plain text, not a workbook")]
    [InlineData("%PDF-1.7 not really")]
    public async Task Не_xlsx_відхиляється_ECR_IMP_0422_з_ключем(string content)
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes(content));

        // ⛔ Мутація: прибрати `OpenXmlPackageException` з фільтра `Open` — тут
        // вилетить саме вона, тобто 500 на живому стенді.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer().PreviewAsync(1, file, CancellationToken.None));

        Assert.Equal("ECR-IMP-0422", error.ErrorCode);
        Assert.Equal("err.ECR-IMP-0422.notAWorkbook", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F-07")]
    public async Task Zip_без_пакета_OOXML_теж_відхиляється_з_ключем()
    {
        using var file = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("readme.txt");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("not a workbook");
        }

        file.Position = 0;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer().PreviewAsync(1, file, CancellationToken.None));

        Assert.Equal("err.ECR-IMP-0422.notAWorkbook", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "F-24")]
    public async Task Прострочений_перегляд_відмовляє_з_ключем_а_не_українською_деталлю()
    {
        var previews = Substitute.For<IImportPreviewStore>();
        previews.FindAsync("gone", Arg.Any<CancellationToken>()).Returns((string?)null);

        // ⛔ Мутація: прибрати `messageKey` у `LoadPlanAsync` — деталь знову
        // стане українським реченням (обробник помилок бере `Message`).
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer(previews).ApplyAsync(1, "gone", CancellationToken.None));

        Assert.Equal("ECR-IMP-0422", error.ErrorCode);
        Assert.Equal("err.ECR-IMP-0422.previewExpired", error.Details!["messageKey"]);
    }

    private static readonly System.Text.Json.JsonSerializerOptions PlanJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// L1-20: токен перегляду, збудований іншим користувачем, не застосовується (і не рахується) — той самий
    /// «перегляду немає». До виправлення токен не був прив'язаний до автора: хто знав токен, застосовував чужий diff.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Чужий_токен_перегляду_відмовляє_як_відсутній()
    {
        var plan = System.Text.Json.JsonSerializer.Serialize(
            new ImportPlan(1, 202601, [], UserId: 5),
            PlanJson);
        var previews = Substitute.For<IImportPreviewStore>();
        previews.FindAsync("tok", Arg.Any<CancellationToken>()).Returns(plan);
        var other = Substitute.For<ICurrentUser>();
        other.UserId.Returns(7);

        var apply = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer(previews, other).ApplyAsync(1, "tok", CancellationToken.None));
        Assert.Equal("err.ECR-IMP-0422.previewExpired", apply.Details!["messageKey"]);

        var count = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer(previews, other).CountPendingChangesAsync("tok", CancellationToken.None));
        Assert.Equal("err.ECR-IMP-0422.previewExpired", count.Details!["messageKey"]);

        // Власник бачить свій перегляд: рахує зміни (їх 0), а не отримує відмову.
        var owner = Substitute.For<ICurrentUser>();
        owner.UserId.Returns(5);
        Assert.Equal(0, await Importer(previews, owner).CountPendingChangesAsync("tok", CancellationToken.None));
    }

    private static ExcelImporter Importer(IImportPreviewStore? previews = null, ICurrentUser? user = null)
        => new(
            Substitute.For<IMetadataCache>(),
            Substitute.For<IRegistryStore>(),
            Substitute.For<IAccessDecisionService>(),
            user ?? Substitute.For<ICurrentUser>(),
            previews ?? Substitute.For<IImportPreviewStore>(),
            patch: null!,
            new ImportDiffBuilder(),
            Substitute.For<ICellStore>(),
            Substitute.For<IRowStore>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<ISheetEditGate>());
}
