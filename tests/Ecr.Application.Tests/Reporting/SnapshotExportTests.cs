// tests/Ecr.Application.Tests/Reporting/SnapshotExportTests.cs
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
/// R7: вивантаження зрізу в <c>.xlsx</c> закрите правом <c>Report.Export</c> і
/// грантом на проєкт зрізу, а рядки в книгу приходять усі й у порядку опису.
/// </summary>
/// <remarks>
/// ⛔ Що цей файл НЕ перевіряє: сам вміст книги. Він дивиться на те, ЩО
/// обробник віддає порту, а не на байти <c>.xlsx</c> — книга розбирається назад
/// у <c>Ecr.Adapters.Tests/Excel/SnapshotWorkbookWriterTests</c>, тим самим
/// пакетом, яким її склали.
/// </remarks>
public sealed class SnapshotExportTests
{
    private const long SnapshotId = 77;
    private const int ProjectId = 4;

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_права_Report_Export_книга_не_будується()
    {
        // ⚠ Право є для ПЕРЕЛІКУ зрізів, і цього навмисно не досить: книга
        // виходить із системи, рядки на екрані — ні.
        var world = new World(
            permissions: [ListReportSnapshotsHandler.Permission],
            grants: new() { [$"{ResourceKind.Project}:{ProjectId}"] = GrantLevel.Read });

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => world.Handler.HandleAsync(SnapshotId, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.Equal(ExportSnapshotHandler.Permission, error.Details!["permission"]);
        await world.Workbooks.DidNotReceive()
            .WriteAsync(Arg.Any<SnapshotWorkbook>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зріз_чужого_проєкту_дає_404_і_рядків_не_читає()
    {
        // ⛔ Мутація, якою перевірено цей тест: прибрати з обробника перевірку
        // `profile.LevelFor(...) < GrantLevel.Read`. Падає рівно він.
        var world = new World(
            permissions: [ExportSnapshotHandler.Permission],
            grants: []);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => world.Handler.HandleAsync(SnapshotId, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportNotFound, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0404.snapshot", error.Details!["messageKey"]);

        // ⚠ Не «віддав 404», а «НЕ ЧИТАВ»: відмова після читання чужих рядків
        // лишилася б витоком у журналі запитів і в часі відповіді.
        await world.Snapshots.DidNotReceive().RowsAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await world.Workbooks.DidNotReceive()
            .WriteAsync(Arg.Any<SnapshotWorkbook>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task У_книгу_йдуть_усі_сторінки_рядків_у_порядку_опису()
    {
        var world = Allowed();
        world.Pages(rowCount: GetSnapshotRowsHandler.MaxLimit + 3);

        await world.Handler.HandleAsync(SnapshotId, CancellationToken.None);

        var book = world.Written();

        // Порядок колонок — саме той, що в описі версії, а не алфавітний і не
        // той, у якому ключі лягли у словник комірок.
        Assert.Equal(["OutputCode", "Value", "RowKey"], book.Columns.Select(c => c.Code));

        // ⚠ Рядків більше за одну сторінку: обробник, який узяв лише першу,
        // віддав би книгу, що виглядає цілою.
        Assert.Equal(GetSnapshotRowsHandler.MaxLimit + 3, book.Rows.Count);
        Assert.Equal(1, book.Rows[0].RowNo);
        Assert.Equal(GetSnapshotRowsHandler.MaxLimit + 3, book.Rows[^1].RowNo);
        Assert.Equal(SnapshotId, book.SnapshotId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Зріз_понад_стелю_рядків_відмовляє_замість_обрізаної_книги()
    {
        var world = Allowed();

        // ⚠ Число в тесті — ЛІТЕРАЛ, а не `ExportSnapshotHandler.MaxRows`:
        // твердження проти константи того самого модуля рухається разом із нею
        // і лишилося б зеленим, якби стелю підняли до мільйона.
        world.Pages(rowCount: 50_000 + 1);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => world.Handler.HandleAsync(SnapshotId, CancellationToken.None));

        Assert.Equal(ErrorCodes.ReportInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-RPT-0422.exportTooLarge", error.Details!["messageKey"]);
        Assert.Equal("50000", error.Details["limit"]);

        // ⛔ Саме відмова, а не книга з «майже всіма» рядками: така книга
        // виглядає повною і ніде про це не каже.
        await world.Workbooks.DidNotReceive()
            .WriteAsync(Arg.Any<SnapshotWorkbook>(), Arg.Any<CancellationToken>());
    }

    private static World Allowed()
        => new(
            permissions: [ExportSnapshotHandler.Permission],
            grants: new() { [$"{ResourceKind.Project}:{ProjectId}"] = GrantLevel.Read });

    /// <summary>Обробник із заглушками порту зрізів і порту книги.</summary>
    private sealed class World
    {
        public World(string[] permissions, Dictionary<string, GrantLevel> grants)
        {
            Snapshots.FindProjectIdAsync(SnapshotId, Arg.Any<CancellationToken>()).Returns(ProjectId);

            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(9);

            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
            {
                CacheKey = "p",
                UserId = 9,
                SecurityStamp = "s",
                Permissions = new HashSet<string>(permissions, StringComparer.Ordinal),
                Grants = grants,
                Denies = new HashSet<string>(),
                RoleIds = new HashSet<int>(),
            });

            Workbooks
                .WriteAsync(Arg.Any<SnapshotWorkbook>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<Stream>(new MemoryStream()));

            Handler = new ExportSnapshotHandler(Snapshots, Workbooks, access, user);
        }

        public IReportSnapshotBuilder Snapshots { get; } = Substitute.For<IReportSnapshotBuilder>();

        public ISnapshotWorkbookWriter Workbooks { get; } = Substitute.For<ISnapshotWorkbookWriter>();

        public ExportSnapshotHandler Handler { get; }

        /// <summary>Розкладає <paramref name="rowCount"/> рядків на сторінки порту.</summary>
        public void Pages(int rowCount)
            => Snapshots
                .RowsAsync(SnapshotId, Arg.Any<int>(), GetSnapshotRowsHandler.MaxLimit, Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<SnapshotRowsPage?>(Page(call.ArgAt<int>(1), rowCount)));

        /// <summary>Книга, яку обробник віддав порту.</summary>
        public SnapshotWorkbook Written()
            => (SnapshotWorkbook)Workbooks.ReceivedCalls()
                .Single(c => c.GetMethodInfo().Name == nameof(ISnapshotWorkbookWriter.WriteAsync))
                .GetArguments()[0]!;

        private static SnapshotRowsPage Page(int afterRowNo, int rowCount)
        {
            var last = Math.Min(afterRowNo + GetSnapshotRowsHandler.MaxLimit, rowCount);

            var rows = Enumerable
                .Range(afterRowNo + 1, Math.Max(last - afterRowNo, 0))
                .Select(no => new SnapshotRow(
                    no,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["RowKey"] = $"row-{no}",
                        ["OutputCode"] = "CO2",
                        ["Value"] = (decimal)no,
                    }))
                .ToList();

            return new SnapshotRowsPage(Columns, rows, last < rowCount ? last : null);
        }

        /// <summary>Порядок навмисно НЕ алфавітний: перевіряється саме опис.</summary>
        private static readonly SnapshotColumn[] Columns =
        [
            new("OutputCode", ReportSourceColumns.Text),
            new("Value", ReportSourceColumns.Number),
            new("RowKey", ReportSourceColumns.Text),
        ];
    }
}
