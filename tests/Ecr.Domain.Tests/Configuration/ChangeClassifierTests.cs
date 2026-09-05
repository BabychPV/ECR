using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Чотири класи змін (ФВ-7.4). Від класу залежить, чи дозволена зміна взагалі:
/// <c>Breaking</c> у версії з документами — **відмова операції**, а не
/// попередження.
/// </summary>
public sealed class ChangeClassifierTests
{
    private static readonly ChangeClassifier Classifier = new();

    [Theory]
    [InlineData("HeaderL10n")]
    [InlineData("Ordinal")]
    [InlineData("DisplayFormat")]
    [InlineData("IsHidden")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.4")]
    public void Презентаційні_поля_класифікуються_як_Presentation(string field)
    {
        // Презентація лишається презентацією і тоді, коли документи вже є —
        // саме тому такі правки дозволені в опублікованій версії.
        Assert.Equal(ChangeClass.Presentation, Classifier.Classify("ColumnDef", field, hasDocuments: false));
        Assert.Equal(ChangeClass.Presentation, Classifier.Classify("ColumnDef", field, hasDocuments: true));
    }

    [Theory]
    [InlineData("DataType")]
    [InlineData("Precision")]
    [InlineData("UnitId")]
    [InlineData("LookupRegistryDefId")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_типу_точності_або_одиниці_класифікується_як_Guarded(string field)
    {
        // Дані лишаються на місці, але їхнє ТЛУМАЧЕННЯ змінюється: ті самі
        // 12500 в іншій одиниці — інше число у звіті. Тому потрібна стратегія
        // міграції, а не проста заборона і не мовчазний дозвіл.
        Assert.Equal(ChangeClass.Guarded, Classifier.Classify("ColumnDef", field, hasDocuments: true));
        Assert.Equal(ChangeClass.Guarded, Classifier.Classify("ColumnDef", field, hasDocuments: false));

        Assert.NotEqual(ChangeClass.Presentation, Classifier.Classify("ColumnDef", field, hasDocuments: true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.6")]
    [Trait("Requirement", "ФВ-7.3")]
    public void Зміна_коду_колонки_за_наявності_документів_це_Breaking()
    {
        // Комірка посилається на ColumnDef.Code. Перейменування розриває
        // зв'язок наявних даних з описом, і жодна стратегія міграції цього
        // не рятує — дані просто перестають знаходитися.
        Assert.Equal(ChangeClass.Breaking, Classifier.Classify("ColumnDef", "Code", hasDocuments: true));

        // Поки документів немає, посилатися на код ще нічому.
        Assert.Equal(ChangeClass.Safe, Classifier.Classify("ColumnDef", "Code", hasDocuments: false));

        // Те саме для ідентичності рядка і таблиці.
        Assert.Equal(ChangeClass.Breaking, Classifier.Classify("RowDef", "RowKeyValue", hasDocuments: true));
        Assert.Equal(ChangeClass.Breaking, Classifier.Classify("TableDef", "Code", hasDocuments: true));

        // Видалення з документами — теж Breaking, попри soft delete (ФВ-7.6):
        // комірки лишаються, а опису до них уже немає.
        Assert.Equal(ChangeClass.Breaking, Classifier.ClassifyDeletion("ColumnDef", hasDocuments: true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.6")]
    public void Додавання_нової_колонки_це_Safe()
    {
        // У нової колонки просто немає комірок — наявних даних це не зачіпає
        // незалежно від того, чи є документи.
        Assert.Equal(ChangeClass.Safe, Classifier.ClassifyAddition("ColumnDef"));
        Assert.Equal(ChangeClass.Safe, Classifier.ClassifyAddition("RowDef"));
        Assert.Equal(ChangeClass.Safe, Classifier.ClassifyAddition("SheetDef"));
    }
}
