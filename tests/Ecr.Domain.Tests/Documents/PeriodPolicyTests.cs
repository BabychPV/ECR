using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Offsets політики періодів (T6/#37). До CRUD-обробників (`ProjectQueryHandlers.cs`)
/// це були значення, які приймала лише база через <c>CK_PP_Order</c> — тепер
/// це і власне правило сутності, перевірене й у конструкторі, і в
/// <see cref="PeriodPolicy.UpdateOffsets"/>.
/// </summary>
public sealed class PeriodPolicyTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Пільговий_строк_довший_за_жорстке_закриття_відхиляється_у_конструкторі()
    {
        // ⛔ D-134: «неможливе значення» тут — пільговий строк, що триває
        // ДОВШЕ за жорстке закриття. Період закрився б остаточно раніше, ніж
        // скінчився б власний пільговий строк — те саме, що й `CK_PP_Order`
        // у базі (`GraceOffsetDays <= HardCloseOffsetDays`), але перевірене
        // ДО запису, з кодом і текстом, а не SQL-винятком.
        var error = Assert.Throws<DomainException>(
            () => new PeriodPolicy(EcrCode.Create("BAD"), 0, graceOffsetDays: 50, hardCloseOffsetDays: 45,
                yearGraceOffsetDays: 45));

        Assert.Equal("ECR-PRD-4225", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Рівні_значення_приймаються()
    {
        // Межа включна: грейс, що закінчується РІВНО коли настає жорстке
        // закриття, — законний стан, не «занадто пізно на день».
        var policy = new PeriodPolicy(EcrCode.Create("EDGE"), 0, graceOffsetDays: 45, hardCloseOffsetDays: 45,
            yearGraceOffsetDays: 45);

        Assert.Equal(45, policy.GraceOffsetDays);
        Assert.Equal(45, policy.HardCloseOffsetDays);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Відємний_річний_грейс_відхиляється()
    {
        var error = Assert.Throws<DomainException>(
            () => new PeriodPolicy(EcrCode.Create("BAD"), 0, 15, 45, yearGraceOffsetDays: -1));

        Assert.Equal("ECR-PRD-4225", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void OpenOffsetDays_може_бути_відємним()
    {
        // Документована властивість поля: період може відкриватися ДО
        // власного початку.
        var policy = new PeriodPolicy(EcrCode.Create("EARLY"), -5, 15, 45, 45);

        Assert.Equal(-5, policy.OpenOffsetDays);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void UpdateOffsets_застосовує_те_саме_правило_що_й_конструктор()
    {
        var policy = new PeriodPolicy(EcrCode.Create("STD"), 0, 15, 45, 45);

        // ⛔ D-134 для СПРАВЖНЬОГО шляху CRUD-редагування (`UpdatePeriodPolicyHandler`
        // викликає саме цей метод, не конструктор): та сама «неможлива»
        // комбінація має відхилятися і тут, а не лише при створенні.
        var error = Assert.Throws<DomainException>(
            () => policy.UpdateOffsets(0, graceOffsetDays: 100, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45));

        Assert.Equal("ECR-PRD-4225", error.ErrorCode);

        // ⚠ Відмова НЕ змінює стан: часткове застосування залишило б політику
        // в суперечливому вигляді, гіршому за незмінений стан.
        Assert.Equal(15, policy.GraceOffsetDays);
        Assert.Equal(45, policy.HardCloseOffsetDays);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void UpdateOffsets_зі_значеннями_що_проходять_перевірку_записує_їх()
    {
        var policy = new PeriodPolicy(EcrCode.Create("STD"), 0, 15, 45, 45);

        policy.UpdateOffsets(openOffsetDays: 2, graceOffsetDays: 20, hardCloseOffsetDays: 60,
            yearGraceOffsetDays: 90);

        Assert.Equal(2, policy.OpenOffsetDays);
        Assert.Equal(20, policy.GraceOffsetDays);
        Assert.Equal(60, policy.HardCloseOffsetDays);
        Assert.Equal(90, policy.YearGraceOffsetDays);
    }
}
