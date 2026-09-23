// tests/Ecr.Application.Tests/Reporting/ReportDefinitionDrivesBuildTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>
/// D-52a: опис, за яким зріз не побудувати, відмовляє при СТВОРЕННІ версії;
/// рядки зрізу закриті тим самим правилом, що й перевірка.
/// </summary>
public sealed class ReportDefinitionDrivesBuildTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Невідомий_код_колонки_відмовляє_з_ключем_каталогу()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.ColumnsJson([new("Value", "number"), new("Nonexistent", "text")]));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.unknownColumn", error.Details!["messageKey"]);
        Assert.Equal("Nonexistent", error.Details["columnCode"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Тип_колонки_не_той_що_в_джерела_відмовляє()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.ColumnsJson([new("Value", "text")]));

        Assert.Equal("err.ECR-RPT-0422.columnKindMismatch", error.Details!["messageKey"]);
        Assert.Equal("number", error.Details["expectedKind"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Версія_без_жодної_колонки_відмовляє_з_ключем_каталогу()
    {
        var error = Assert.Throws<BusinessRuleException>(() => ReportDefinitionSpec.ColumnsJson([]));

        Assert.Equal("err.ECR-RPT-0422.noColumns", error.Details!["messageKey"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Колонка_описана_двічі_відмовляє_з_ключем_каталогу()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.ColumnsJson([new("Value", "number"), new("Value", "number")]));

        Assert.Equal("err.ECR-RPT-0422.duplicateColumn", error.Details!["messageKey"]);
        Assert.Equal("Value", error.Details["code"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Назва_відсутня_жодною_мовою_відмовляє_з_ключем_каталогу()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.Name(new Dictionary<string, string> { ["en"] = "   " }));

        Assert.Equal("err.ECR-RPT-0422.nameRequired", error.Details!["messageKey"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Задовгий_номер_версії_відмовляє_з_ключем_каталогу()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.Version(new string('9', 21)));

        Assert.Equal("err.ECR-RPT-0422.version", error.Details!["messageKey"]);
        Assert.Equal("20", error.Details["maxLength"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Невідоме_джерело_рядків_відмовляє_з_ключем_каталогу()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.RulesJson(new ReportRulesCommand("Other")));

        Assert.Equal("err.ECR-RPT-0422.rowSource", error.Details!["messageKey"]);
        Assert.Equal("Other", error.Details["rowSource"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Правила_пишуться_з_версією_схеми_а_старі_без_неї_читаються_як_перша()
    {
        Assert.Equal("""{"rowSource":"CalculationResults","schema":1}""", ReportDefinitionSpec.RulesJson(null));
        Assert.Equal(
            new ReportRules("CalculationResults", 1), ReportRules.Parse("""{"rowSource":"CalculationResults"}"""));
        Assert.Equal(2, ReportRules.Parse("""{"rowSource":"CalculationResults","schema":2}""").Schema);

        Assert.Throws<BusinessRuleException>(
            () => ReportDefinitionSpec.RulesJson(new ReportRulesCommand("CalculationResults", 3)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядки_зрізу_чужого_проєкту_дають_404_і_не_читаються()
    {
        var (handler, snapshots) = RowsHandler(grants: []);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(77, null, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportNotFound, error.ErrorCode);
        await snapshots.DidNotReceive().RowsAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Розмір_сторінки_обрізається_до_стелі_а_курсор_не_буває_відʼємним()
    {
        var (handler, snapshots) = RowsHandler(new() { [$"{ResourceKind.Project}:4"] = GrantLevel.Read });
        snapshots.RowsAsync(77, 0, GetSnapshotRowsHandler.MaxLimit, Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(new SnapshotRowsPage([], [], null));

        var page = await handler.HandleAsync(77, -5, 100_000, CancellationToken.None);

        Assert.Empty(page.Rows);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Колонки_підписуються_мовою_запиту_а_не_мовою_за_замовчуванням()
    {
        // ⛔ R9: мову видачі бере сам обробник із `ICurrentUser`. Якби він її не
        // передавав, порт підписав би колонки мовою за замовчуванням — тобто
        // кожен користувач бачив би англійські заголовки незалежно від вибору.
        var (handler, snapshots) = RowsHandler(
            new() { [$"{ResourceKind.Project}:4"] = GrantLevel.Read }, language: "kz");

        snapshots.RowsAsync(77, 0, GetSnapshotRowsHandler.DefaultLimit, "kz", Arg.Any<CancellationToken>())
                 .Returns(new SnapshotRowsPage([new("Value", "number", "Мөлшері")], [], null));

        var page = await handler.HandleAsync(77, null, null, CancellationToken.None);

        Assert.Equal("Мөлшері", Assert.Single(page.Columns).Name);
    }

    private static (GetSnapshotRowsHandler Handler, IReportSnapshotBuilder Snapshots) RowsHandler(
        Dictionary<string, GrantLevel> grants, string language = "en")
    {
        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();

        user.UserId.Returns(9);
        user.Language.Returns(language);
        snapshots.FindProjectIdAsync(77, Arg.Any<CancellationToken>()).Returns(4);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = 9,
            SecurityStamp = "s",
            Permissions = new HashSet<string>([ListReportSnapshotsHandler.Permission], StringComparer.Ordinal),
            Grants = grants,
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });

        return (new GetSnapshotRowsHandler(snapshots, access, user), snapshots);
    }
}
