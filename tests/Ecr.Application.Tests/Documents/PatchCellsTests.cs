using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Пакетна зміна комірок — найгарячіший шлях запису.</summary>
public sealed class PatchCellsTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Значення_записується_і_повертається_нова_версія_рядка()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Рядок_без_базової_версії_трактується_як_створення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Створення_рядка_з_наявним_ключем_відхиляється_ECR_ROW_0409()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Конфлікт_в_одному_рядку_відхиляє_весь_батч_із_переліком_конфліктів()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заборонена_комірка_відхиляє_батч_із_причиною()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Комірковий_Error_валідації_блокує_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рядковий_Error_валідації_НЕ_блокує_запис_а_повертається_у_відповіді()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Попередження_не_блокує_запис()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Відсутнє_поле_в_запиті_не_змінює_комірку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Після_запису_залежні_комірки_ставляться_в_чергу_перерахунку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Перерахунок_ставиться_в_чергу_ПОЗА_транзакцією_запису()
        => Assert.Fail("not implemented");
}
