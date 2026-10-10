// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.AuditAtomicity.cs
using System.Net;
using Ecr.Application.Documents.VersionMigration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// F2-03 (аудит R11): слід переносу версії пишеться В ТІЙ САМІЙ транзакції.
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    /// <remarks>
    /// Збій запису журналу відкочує перенос: версія проєкту, комірки, рядки й склад аркушів — як були.
    /// Доти журнал ішов ПІСЛЯ коміту: перенос структури всіх документів проєкту лишався без сліду.
    /// Мутація: повернути запис журналу після `ExecuteInTransactionAsync` — версію перемкнено, червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F2-03")]
    public async Task Збій_запису_журналу_відкочує_перенос_версії()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var baseApp = new EcrApiFactory(sql);
        using var app = FailingSecurityEventAuditWriter.Install(baseApp, MigrateDocumentVersionHandler.EventType);
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, $"{response.StatusCode}: {baseApp.ErrorsText}");
        Assert.Contains(FailingSecurityEventAuditWriter.Marker, baseApp.ErrorsText, StringComparison.Ordinal);

        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }
}
