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

        using (var baseApp = new EcrApiFactory(sql))
        using (var app = FailingSecurityEventAuditWriter.Install(baseApp, MigrateDocumentVersionHandler.EventType))
        using (var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true))
        {
            var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.InternalServerError, $"{response.StatusCode}: {baseApp.ErrorsText}");
            Assert.Contains(FailingSecurityEventAuditWriter.Marker, baseApp.ErrorsText, StringComparison.Ordinal);
        }

        // Б1: слід пишеться у T2 ДО перемикання версії, тож відмова журналу відкочує T2: версія проєкту, екземпляр
        // таблиці й склад аркушів — як були. Комірки (діапазони з комітом) уже на цільових колонках, а маркер «міграція
        // в процесі» лишився — це відновлюваний стан: повтор доводить перенос до кінця.
        Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.True(await MarkerPresentAsync(s).ConfigureAwait(true), "після відмови журналу маркер мусить лишитися");
        Assert.Equal(s.Doc.TableDefId, await ScalarAsync(
            "SELECT TableDefId FROM doc.TableInstance WHERE PeriodKey = @pk AND Id = @ti", s).ConfigureAwait(true));

        using var retryFactory = new EcrApiFactory(sql);
        using var retryClient = await SignedInAsync(retryFactory, s.UserName, retryFactory).ConfigureAwait(true);
        var retried = await PostAsync(retryClient, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(retried.StatusCode == HttpStatusCode.OK, $"{retried.StatusCode}: {retryFactory.ErrorsText}");
        Assert.Equal(s.TargetVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.False(await MarkerPresentAsync(s).ConfigureAwait(true), "після повтору маркер мусить бути знято");
    }
}
