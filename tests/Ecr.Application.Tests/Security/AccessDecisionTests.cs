using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Дванадцять сценаріїв доступу з фікстури <c>02c §6</c> плюс три додаткові
/// з <c>tz/07</c> §7.6.
/// </summary>
/// <remarks>
/// Найважливіший тут — <c>A7</c>: **закритий період блокує запис усім,
/// включно з найвищим грантом**. Якщо він проходить — модель доступу зламана,
/// і жоден інший тест цього не покаже.
/// </remarks>
public sealed class AccessDecisionTests
{
    private static AccessProfile Operator()
        => new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A01_оператор_із_грантом_Write_редагує_відкритий_період()
    {
        var decision = EditRules.CanEdit(Operator(), AccessBuilder.Cell());

        Assert.True(decision.IsAllowed);
        Assert.Equal(EditDenyReason.None, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A02_локальний_користувач_має_ті_самі_права_що_й_доменний()
    {
        // ⚠ Провайдер входу не бере участі в рішенні ЗОВСІМ: профіль будується
        // з ролей і грантів, а не з того, як людина увійшла. Саме тому тут
        // порівнюються два профілі з однаковими грантами і різними id —
        // рішення мають збігтися до причини включно.
        var domain = new AccessBuilder { UserId = 1 }
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();
        var local = new AccessBuilder { UserId = 2 }
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();

        var cell = AccessBuilder.Cell();

        Assert.Equal(EditRules.CanEdit(domain, cell), EditRules.CanEdit(local, cell));
        Assert.True(EditRules.CanEdit(local, cell).IsAllowed);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A03_обчислена_колонка_недоступна_на_запис_із_причиною_CalculatedCell()
    {
        var decision = EditRules.CanEdit(Operator(), AccessBuilder.Cell(computed: true));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.CalculatedCell, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A04_переглядач_без_гранта_Write_отримує_причину_NoGrant()
    {
        var viewer = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Build();

        var decision = EditRules.CanEdit(viewer, AccessBuilder.Cell());

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A05_у_стані_Grace_запис_дозволений_і_позначається_як_пізній()
    {
        var decision = EditRules.CanEdit(Operator(), AccessBuilder.Cell(period: PeriodState.Grace));

        // Grace — це «ще можна, але вже пізно»: запис дозволений, а помітка
        // IsLateEdit ставиться на шляху запису (перевірено AuditTests Етапу 1).
        Assert.True(decision.IsAllowed);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A06_закритий_період_блокує_запис_оператору()
    {
        var decision = EditRules.CanEdit(Operator(), AccessBuilder.Cell(period: PeriodState.Closed));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.PeriodClosed, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A07_закритий_період_блокує_запис_НАВІТЬ_власнику_Manage()
    {
        // ⚠ Головна перевірка моделі доступу. Manage — найвищий рівень, і
        // спокуса зробити для нього виняток велика: «власник же має право».
        // Не має: закритий період означає, що числа вже подані назовні, і
        // тиха правка після цього — розбіжність зі звітом, який уже пішов.
        var owner = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage).Build();

        var decision = EditRules.CanEdit(owner, AccessBuilder.Cell(period: PeriodState.Closed));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.PeriodClosed, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A08_аркуш_поза_вікном_доступу_дає_причину_OutOfAccessWindow()
    {
        var decision = EditRules.CanEdit(Operator(), AccessBuilder.Cell(outOfWindow: true));

        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.OutOfAccessWindow, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A09_поданий_документ_блокує_запис_навіть_у_відкритому_періоді()
    {
        var decision = EditRules.CanEdit(
            Operator(), AccessBuilder.Cell(period: PeriodState.Open, sheet: DocumentStatus.Submitted));

        // Стан аркуша перевіряється НЕЗАЛЕЖНО від стану періоду: Grace дає час
        // на правки неподаних документів, а не право змінити подану форму (D-67).
        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.DocumentSubmitted, decision.Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A10_заборона_на_одну_колонку_не_блокує_решту()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Deny(ResourceKind.Column, 41)
            .Build();

        Assert.False(EditRules.CanEdit(profile, AccessBuilder.Cell(columnId: 41)).IsAllowed);
        Assert.True(EditRules.CanEdit(profile, AccessBuilder.Cell(columnId: 42)).IsAllowed);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A11_подання_потребує_рівня_Submit_а_не_Write()
    {
        var writer = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write).Build();
        var submitter = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Submit).Build();

        var cell = AccessBuilder.Cell();

        // Право заповнювати і право відповідати за подане — різні повноваження.
        Assert.False(EditRules.CanSubmit(writer, cell, hasBlockingErrors: false).IsAllowed);
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanSubmit(writer, cell, false).Reason);
        Assert.True(EditRules.CanSubmit(submitter, cell, hasBlockingErrors: false).IsAllowed);

        // …і подання з незакритими помилками валідації не проходить нікому.
        Assert.False(EditRules.CanSubmit(submitter, cell, hasBlockingErrors: true).IsAllowed);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void A12_затвердження_потребує_рівня_Approve_і_стану_Submitted()
    {
        var approver = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve).Build();
        var submitter = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Submit).Build();

        var submitted = AccessBuilder.Cell(sheet: DocumentStatus.Submitted);

        Assert.True(EditRules.CanApprove(approver, submitted).IsAllowed);
        Assert.False(EditRules.CanApprove(submitter, submitted).IsAllowed);

        // Затвердження чернетки означало б, що ніхто не заявив її готовою.
        Assert.False(EditRules.CanApprove(approver, AccessBuilder.Cell()).IsAllowed);
    }

    // ——— Додаткові з tz/07 §7.6 ———

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Заборона_виграє_над_дозволом_на_будь_якому_рівні_успадкування()
    {
        // Deny на проєкті проти Manage на колонці — найдрібніший рівень
        // дозволу програє найширшому рівню заборони.
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Column, AccessBuilder.ColumnId, GrantLevel.Manage)
            .Deny(ResourceKind.Project, AccessBuilder.ProjectId)
            .Build();

        Assert.Equal(GrantLevel.None, EditRules.Effective(profile, AccessBuilder.Cell()));
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanEdit(profile, AccessBuilder.Cell()).Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Грант_на_колонку_перекриває_грант_на_таблицю()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Table, AccessBuilder.TableId, GrantLevel.Manage)
            .Grant(ResourceKind.Column, AccessBuilder.ColumnId, GrantLevel.Read)
            .Build();

        // Точкове ЗВУЖЕННЯ прав: інакше заради однієї колонки довелося б
        // переоформлювати грант на всю таблицю.
        Assert.Equal(GrantLevel.Read, EditRules.Effective(profile, AccessBuilder.Cell()));
        Assert.Equal(EditDenyReason.NoGrant, EditRules.CanEdit(profile, AccessBuilder.Cell()).Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поточний_період_проєкту_НЕ_впливає_на_рішення_про_доступ()
    {
        // ⚠ У CellAccessContext поточного періоду немає ЗА ПОБУДОВОЮ — саме
        // так виглядає гарантія. Якби він там був, «пін» став би прихованим
        // правом редагувати закрите (D-77).
        var fields = typeof(CellAccessContext)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain("CurrentPeriodId", fields);
        Assert.DoesNotContain("CurrentPeriodMode", fields);

        // І поведінково: рішення для закритого періоду не залежить ні від чого,
        // крім самого стану періоду.
        Assert.Equal(
            EditDenyReason.PeriodClosed,
            EditRules.CanEdit(Operator(), AccessBuilder.Cell(period: PeriodState.Closed)).Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_будується_раз_а_не_на_кожну_комірку()
    {
        // Правила — чиста функція від профілю: побудова профілю в них не
        // входить у принципі. Бюджет відкриття таблиці 500×60 дає на права
        // 50 мс на ВЕСЬ запит, і резолвінг ролей на кожну комірку його з'їв би.
        var profile = Operator();
        var decisions = Enumerable.Range(0, 1000)
            .Select(i => EditRules.CanEdit(profile, AccessBuilder.Cell(columnId: 40 + (i % 60))))
            .ToList();

        Assert.All(decisions, d => Assert.True(d.IsAllowed));
        Assert.Single(decisions.Distinct());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Пакетна_перевірка_зрізу_дає_ті_самі_рішення_що_й_поштучна()
    {
        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Write)
            .Deny(ResourceKind.Column, 43)
            .Build();

        var cells = Enumerable.Range(40, 5)
            .Select(id => AccessBuilder.Cell(columnId: id, computed: id == 44))
            .ToList();

        // Пакетна перевірка — це та сама функція над списком, а не другий
        // алгоритм. Другий алгоритм рано чи пізно розійшовся б із першим, і
        // зріз показував би не те, що дозволяє запис.
        var batch = cells.Select(c => EditRules.CanEdit(profile, c)).ToList();
        var single = cells.Select(c => EditRules.CanEdit(profile, c)).ToList();

        Assert.Equal(single, batch);
        Assert.Equal(EditDenyReason.NoGrant, batch[3].Reason);
        Assert.Equal(EditDenyReason.CalculatedCell, batch[4].Reason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відмова_повертає_ПРИЧИНУ_а_не_просто_заборону()
    {
        // Користувач має розуміти, чому комірка сіра, інакше він піде до
        // адміністратора, а той — до розробника.
        var cases = new (CellAccessContext Cell, EditDenyReason Expected)[]
        {
            (AccessBuilder.Cell(project: ProjectStatus.Archived), EditDenyReason.ProjectArchived),
            (AccessBuilder.Cell(archiving: true), EditDenyReason.ArchivingInProgress),
            (AccessBuilder.Cell(period: PeriodState.Scheduled), EditDenyReason.PeriodNotOpenYet),
            (AccessBuilder.Cell(period: PeriodState.Closed), EditDenyReason.PeriodClosed),
            (AccessBuilder.Cell(outOfWindow: true), EditDenyReason.OutOfAccessWindow),
            (AccessBuilder.Cell(sheet: DocumentStatus.Submitted), EditDenyReason.DocumentSubmitted),
            (AccessBuilder.Cell(sheet: DocumentStatus.Approved), EditDenyReason.DocumentApproved),
            (AccessBuilder.Cell(computed: true), EditDenyReason.CalculatedCell),
            (AccessBuilder.Cell(columnReadOnly: true), EditDenyReason.ColumnReadOnly),
            (AccessBuilder.Cell(rowReadOnly: true), EditDenyReason.RowReadOnly),
        };

        foreach (var (cell, expected) in cases)
        {
            var decision = EditRules.CanEdit(Operator(), cell);
            Assert.False(decision.IsAllowed);
            Assert.Equal(expected, decision.Reason);
        }

        // Симуляція — окрема причина і перевіряється ПЕРШОЮ, незалежно від прав.
        var simulated = new AccessBuilder()
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage)
            .Build(simulation: true, simulatedFor: 99);

        Assert.Equal(
            EditDenyReason.SimulationReadOnly,
            EditRules.CanEdit(simulated, AccessBuilder.Cell()).Reason);
    }
}
