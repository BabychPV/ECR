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
/// Доступ до вивантаження документа (<c>ФВ-6.13</c>).
/// </summary>
/// <remarks>
/// ⛔ Експорт віддає документ ЦІЛКОМ. Функціональне право
/// <c>Document.Export</c> має роль <c>DataEntry</c> — тобто майже кожен, хто
/// працює з системою; воно каже «цей користувач узагалі вивантажує
/// документи», а не «саме цей документ». Другу відповідь дає грант, і до
/// <c>A7-55</c> її ніхто не питав.
/// </remarks>
// ⛔ Трейт `ФВ-6.13` знятий (директива №09 §8.2). Вимога каже про
// ВПОРЯДКОВАНІ рівні доступу (`None` → `Read` → … → `Manage`, вищий
// включає нижчі), а тут рішення про доступ віддає ЗАГЛУШКА
// (`Substitute.For<IAccessDecisionService>`), яка повертає те, що їй
// сказали. Гратчастку рівнів вона не виконує жодного разу, тож
// зелений результат тут не є доказом `ФВ-6.13`.
//
// ⚠ Самі перевірки ЛИШАЮТЬСЯ і корисні: вони доводять, що обробник
// ПИТАЄ службу рішень і шанує відмову (не ставить задачу, не віддає
// зріз). Змінилася НЕ поведінка, а ЗАЯВКА про те, що вони покривають.
// Саму `ФВ-6.13` доводять `Ecr.Domain.Tests/Security/ResourceGrantTests` (гратчастка
// рівнів без жодного мока) і `Ecr.Api.Tests/CellWriteRoundTripTests` (через HTTP на
// живій базі).
public sealed class ExportAccessTests
{
    private const long DocumentId = 700;

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ExportAccessTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.Export").Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_експорт_не_ставиться_в_чергу()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(DocumentId, Options(), CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);

        // ⚠ І задача НЕ поставлена. Відмова, після якої робота однаково
        // йде у фон, — це відмова лише на вигляд: файл усе одно з'явиться в
        // сховищі, а посилання на нього передбачуване.
        await _jobs.DidNotReceiveWithAnyArgs()
            .EnqueueAsync<IExcelExportJob>(null, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task З_грантом_експорт_ставиться_в_чергу()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _jobs.EnqueueAsync<IExcelExportJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");

        var jobId = await Handler().HandleAsync(DocumentId, Options(), CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    private ExportDocumentHandler Handler() => new(_jobs, _access, _user);

    private static ExcelExportOptions Options()
        => new(IncludeFormulas: false, IncludeStyles: false, Language: "en", PeriodKey: 202601);
}
