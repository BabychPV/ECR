using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Audit;

/// <summary>
/// ent4 P3-2: CSV журналу структурних змін віддає причину зміни налаштувань збору ТЕКСТОМ
/// мовою того, хто вивантажує, а не сирим конвертом <c>{"k":…,"p":{…}}</c>.
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати розгортання в <c>ExportStructureChangesHandler.RowsAsync</c> —
/// у колонці причини приїде JSON із ключем <c>integrationAudit.*</c>.
/// </remarks>
public sealed class StructureExportReasonTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Directive", "BE-16")]
    public async Task Конверт_причини_розгортається_мовою_читача_а_звичайний_текст_лишається()
    {
        var envelope = IntegrationAuditReason.Encode("integrationAudit.dataSourceChanged", ("connection", "PI-MAIN"));
        var rows = await ExportAsync("ru", envelope, "Ручна причина", null);

        Assert.Equal("Соединение «PI-MAIN» изменено.", rows[0].ChangeReason);
        Assert.Equal("Ручна причина", rows[1].ChangeReason);
        Assert.Null(rows[2].ChangeReason);
        Assert.DoesNotContain("integrationAudit", ExportStructureChangesHandler.ToCsv(rows[0]), StringComparison.Ordinal);
    }

    private static async Task<List<StructureChangeView>> ExportAsync(string language, params string?[] reasons)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        user.Language.Returns(language);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(GetCellChangesHandler.Permission).Build());

        var reader = Substitute.For<IAuditReader>();
        reader.ReadStructureJournalAsync(Arg.Any<StructureChangeFilter>(), Arg.Any<CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<StructureChangeView>(
                [.. reasons.Select((reason, i) => new StructureChangeView(
                    Now, "ext.DataSource", i + 1, "Update", null, null, reason, 9))],
                NextCursor: null,
                TotalCount: null));

        var catalog = Substitute.For<IUiStringCatalog>();
        catalog.GetScopedAsync(language, UiStringScope.Private, Arg.Any<CancellationToken>())
            .Returns(new UiStringCatalog(language, 1, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["integrationAudit.dataSourceChanged"] = "Соединение «{connection}» изменено.",
            }));

        var handler = new ExportStructureChangesHandler(
            new GetStructureChangesHandler(reader, access, user), reader, Substitute.For<IAuditWriter>(),
            access, user, new TestClock(Now), catalog);

        var export = await handler.PrepareAsync(
            new StructureChangeFilter(Now.AddDays(-1), Now), ExportStructureChangesHandler.DefaultMaxRows, default);

        var result = new List<StructureChangeView>();
        await foreach (var row in export.Rows)
        {
            result.Add(row);
        }

        return result;
    }
}
