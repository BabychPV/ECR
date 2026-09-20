// tests/Ecr.Domain.Tests/Workflow/ApprovalStateTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Workflow;

/// <summary>
/// Стан робочого процесу на аркуш × період — **єдине джерело істини** про
/// статус (D-38, D-93).
/// </summary>
public sealed class ApprovalStateTests
{
    private const int User = 7;
    private static readonly DateTime Now = new(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);

    private static ApprovalState Sheet(int sheetDefId = 20)
        => new(documentId: 700, sheetDefId, periodKey: 202601);

    private static ApprovalState Submitted(int sheetDefId = 20)
    {
        var state = Sheet(sheetDefId);
        state.Submit(User, Now);
        return state;
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.13")]
    public void Reopen_без_причини_відхиляється()
    {
        var state = Submitted();

        var error = Assert.Throws<DomainException>(() => state.Reopen(User, "  ", Now));

        // Причина обов'язкова і тут, і в базі (D-67): без неї в журналі
        // лишиться сам факт повернення без відповіді на «чому».
        Assert.Equal("ECR-DOC-0422", error.ErrorCode);
        Assert.Equal(DocumentStatus.Submitted, state.Status);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.12")]
    public void Перехід_Approved_у_Draft_можливий_лише_через_Reopen()
    {
        var state = Submitted();
        state.Approve(User, Now);

        // Прямого переходу немає: подання із Approved відхиляється.
        Assert.Throws<DomainException>(() => state.Submit(User, Now));
        Assert.Equal(DocumentStatus.Approved, state.Status);

        state.Reopen(User, "помилка в рядку 7001003", Now);

        Assert.Equal(DocumentStatus.Draft, state.Status);
        Assert.Equal("помилка в рядку 7001003", state.ReopenReason);
        Assert.Equal(User, state.ReopenedByUserId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Reopen_із_Draft_відхиляється_бо_нема_чого_відкривати()
    {
        var state = Sheet();

        var error = Assert.Throws<DomainException>(() => state.Reopen(User, "причина", Now));

        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.16")]
    public void Стан_одного_аркуша_не_зачіпає_інші_аркуші_періоду()
    {
        // ⚠ Стан живе на АРКУШ × ПЕРІОД (D-38). 24 аркуші рідко готові
        // одночасно, і чекати найповільніший не має сенсу; скалярний статус на
        // документі був би другим джерелом істини, яке рано чи пізно покаже
        // Approved там, де половина аркушів у Draft.
        var water = Submitted(sheetDefId: 20);
        var waste = Sheet(sheetDefId: 21);

        water.Approve(User, Now);

        Assert.Equal(DocumentStatus.Approved, water.Status);
        Assert.Equal(DocumentStatus.Draft, waste.Status);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-5.15")]
    public void Reject_вимагає_коментаря()
    {
        var state = Submitted();

        var error = Assert.Throws<DomainException>(() => state.Reject(User, string.Empty, Now));
        Assert.Equal("ECR-DOC-0422", error.ErrorCode);
        Assert.Equal(DocumentStatus.Submitted, state.Status);

        // Відхилення без пояснення повертає роботу тому, хто не знає, що
        // виправляти, — і цикл повторюється.
        state.Reject(User, "не сходиться баланс за березень", Now);

        Assert.Equal(DocumentStatus.Rejected, state.Status);
        Assert.Equal("не сходиться баланс за березень", state.RejectedReason);
    }

    // ───────────────────────── BE-31: Recall ─────────────────────────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Recall_повертає_поданий_аркуш_у_чернетку_поки_крок_не_підписано()
    {
        var state = Sheet();
        state.Submit(User, Now, firstStepId: 11);

        state.Recall("подав не той місяць", firstStepId: 11);

        Assert.Equal(DocumentStatus.Draft, state.Status);
        Assert.Null(state.CurrentStepId);

        // Відкликаний аркуш подається знову — і знову з першого кроку.
        state.Submit(User, Now, firstStepId: 11);
        Assert.Equal(11, state.CurrentStepId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Recall_після_підписаного_кроку_відхиляється()
    {
        var state = Sheet();
        state.Submit(User, Now, firstStepId: 11);
        state.ApproveStep(userId: 8, Now, nextStepId: 12);

        var error = Assert.Throws<DomainException>(() => state.Recall("передумав", firstStepId: 11));

        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.recallStepSigned", error.Details!["messageKey"]);
        Assert.Equal(DocumentStatus.Submitted, state.Status);
        Assert.Equal(12, state.CurrentStepId);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(false)]
    [InlineData(true)]
    public void Recall_не_з_Submitted_відхиляється(bool approved)
    {
        var state = approved ? Submitted() : Sheet();
        if (approved)
        {
            state.Approve(User, Now);
        }

        var error = Assert.Throws<DomainException>(() => state.Recall("причина", firstStepId: null));

        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.recallWrongState", error.Details!["messageKey"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Recall_без_причини_відхиляється()
    {
        var state = Submitted();

        var error = Assert.Throws<DomainException>(() => state.Recall(" ", firstStepId: null));

        Assert.Equal("ECR-DOC-0422", error.ErrorCode);
        Assert.Equal(DocumentStatus.Submitted, state.Status);
    }
}
