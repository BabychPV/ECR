using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>Експорт та імпорт перекладу в CSV (<c>BE-13</c> ч.2).</summary>
public sealed class UiStringCsvTests
{
    private const int Editor = 5;

    private readonly FakeUiStringCatalog _catalog = new FakeUiStringCatalog()
        .Add("en", "a.formula", "=SUM(A1)")
        .Add("en", "a.quote", "Say \"hi\", then go", UiStringScope.Public)
        .Add("en", "a.window", "From {from} to {to}")
        .Add("ru", "a.window", "С {from} по {to}");

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public UiStringCsvTests()
    {
        _user.UserId.Returns(Editor);
        Grant(SetUiStringHandler.Permission);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Експорт_має_колонки_екранує_й_нейтралізує_формули()
    {
        var csv = await Export("ru");

        Assert.Equal(
            "key,scope,en,ru,updatedAt\r\n"
            + "a.formula,private,'=SUM(A1),,\r\n"
            + "a.quote,public,\"Say \"\"hi\"\", then go\",,\r\n"
            + "a.window,private,From {from} to {to},С {from} по {to},\r\n",
            csv);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task DryRun_звітує_але_нічого_не_пише()
    {
        var before = _catalog.Revision;

        var report = await Import("key,ru\r\na.quote,Скажи\r\na.window,От {from} до {to}\r\n", dryRun: true);

        Assert.Equal((1, 1, 0, false), (report.Added, report.Updated, report.Unchanged, report.Applied));
        Assert.Equal(before, _catalog.Revision);
        Assert.Equal("С {from} по {to}", (await _catalog.GetAsync("ru", default)).Strings["a.window"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Помилки_рядків_називають_рядок_ключ_і_messageKey_і_блокують_увесь_файл()
    {
        var before = _catalog.Revision;

        var report = await Import(
            "key,ru\r\na.quote,Скажи\r\na.window,С {from}\r\nno.such,X\r\na.formula,\"  \"\r\na.quote,Ещё\r\n",
            dryRun: false);

        Assert.Equal(
            [
                (3, "a.window", "err.ECR-REQ-0422.placeholderMismatch"),
                (4, "no.such", "err.ECR-REQ-0422.uiStringUnknownKey"),
                (5, "a.formula", "err.ECR-REQ-0422.uiStringEmptyValue"),
                (6, "a.quote", "err.ECR-REQ-0422.uiStringDuplicateKey"),
            ],
            report.Errors.Select(e => (e.Row, e.Key, e.MessageKey)));
        Assert.False(report.Applied);
        Assert.Equal(before, _catalog.Revision);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Застосування_пише_все_одним_кроком_версії_а_повтор_нічого_не_змінює()
    {
        var before = _catalog.Revision;
        const string file = "key,ru\r\na.formula,'=СУММ(A1)\r\na.window,От {to} до {from}\r\n";

        var first = await Import(file, dryRun: false);

        Assert.Equal((1, 1, 0, true), (first.Added, first.Updated, first.Unchanged, first.Applied));
        Assert.Equal(before + 1, _catalog.Revision);
        var ru = (await _catalog.GetAsync("ru", default)).Strings;
        Assert.Equal("=СУММ(A1)", ru["a.formula"]); // префікс ' знято
        Assert.Equal("От {to} до {from}", ru["a.window"]);
        await _audit.Received(1).WriteSecurityEventAsync(
            Arg.Is<SecurityEventRecord>(e => e.EventType == "UiStringsImported"), Arg.Any<CancellationToken>());

        var again = await Import(file, dryRun: false);

        Assert.Equal((0, 0, 2, false), (again.Added, again.Updated, again.Unchanged, again.Applied));
        Assert.Equal(before + 1, _catalog.Revision);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Власний_експорт_імпортується_назад_без_змін()
    {
        await Import("key,ru\r\na.formula,-1\r\n", dryRun: false);
        var revision = _catalog.Revision;

        var report = await Import(await Export("ru"), dryRun: false);

        Assert.Equal((0, 0, 2), (report.Added, report.Updated, report.Unchanged));
        Assert.Empty(report.Errors);
        Assert.Equal(revision, _catalog.Revision);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("en", "key,en\r\na.quote,Hi\r\n", "err.ECR-REQ-0422.uiStringCsvLanguage")]
    [InlineData("xx", "key,xx\r\na.quote,Hi\r\n", "err.ECR-REQ-0422.uiStringCsvLanguage")]
    [InlineData("ru", "key,kk\r\na.quote,Hi\r\n", "err.ECR-REQ-0422.uiStringCsvHeader")]
    public async Task Еталон_і_файл_без_колонки_мови_відхиляються_цілком(string lang, string file, string messageKey)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(lang, file, file.Length, 1000, dryRun: false, default));

        Assert.Equal(("ECR-REQ-0422", messageKey), (error.ErrorCode, error.Details!["messageKey"]));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Файл_понад_стелю_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync("ru", string.Empty, 1_048_577, 1_048_576, dryRun: true, default));

        Assert.Equal("err.ECR-REQ-0422.uiStringCsvTooLarge", error.Details!["messageKey"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_локалізації_ні_експорту_ні_імпорту()
    {
        Grant("Template.Edit");

        var export = await Assert.ThrowsAsync<AccessDeniedException>(() => Export("ru"));
        var import = await Assert.ThrowsAsync<AccessDeniedException>(() => Import("key,ru\r\n", dryRun: true));

        Assert.Equal(("ECR-AUTH-0403", "ECR-AUTH-0403"), (export.ErrorCode, import.ErrorCode));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Розбір_CSV_дзеркальний_до_запису()
    {
        var line = CsvFormat.Row("a,b", "x \"y\"\r\nz", null, "=1");

        Assert.Equal(["a,b", "x \"y\"\r\nz", string.Empty, "'=1"], Assert.Single(CsvReader.Parse("﻿" + line)));
    }

    private Task<string> Export(string lang)
        => new ExportUiStringsCsvHandler(_catalog, _access, _user).HandleAsync(lang, default);

    private Task<UiStringImportReport> Import(string csv, bool dryRun)
        => Handler().HandleAsync("ru", csv, csv.Length, UiStringImportHandler.DefaultMaxBytes, dryRun, default);

    private UiStringImportHandler Handler()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc));
        return new UiStringImportHandler(_catalog, _access, Substitute.For<IUnitOfWork>(), _audit, _user, clock);
    }

    private void Grant(string permission)
        => _access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Editor }.Permission(permission).Build());
}
