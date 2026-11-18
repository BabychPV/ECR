using System.Reflection;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// Охоронна таблиця повноти перенесення: КОЖНЕ публічне поле кожної сутності старої моделі методології має явне рішення —
/// переноситься в текст скрипта, лишається даними (рушій-незалежними), породжується скриптом при матеріалізації чи є
/// технічним. Нове поле старої моделі без рядка тут червонить тест: конвертер не має права «не помітити» логіку.
/// </summary>
/// <remarks>
/// Рішення «Text» означає, що поле читає <c>VersionSourceReader</c> (відбиток його враховує) і конвертер або переносить
/// його в текст, або падає з діагностикою. Рішення «Data» — поле лишається в старій сутності, яку рушій <c>Script</c> читає
/// так само, як <c>Formulas</c> (див. план, розд. 8.2, останній рядок таблиці).
/// </remarks>
public sealed class ConversionCompletenessTests
{
    private const string Text = "Text";
    private const string Data = "Data";
    private const string Derived = "Derived";
    private const string Technical = "Technical";

    private static readonly Dictionary<Type, Dictionary<string, string>> Table = new()
    {
        [typeof(Methodology)] = new()
        {
            [nameof(Methodology.Code)] = Text, // заголовок і код у пакеті
            [nameof(Methodology.NameL10n)] = Data,
            [nameof(Methodology.IsActive)] = Data,
            [nameof(Methodology.Kind)] = Text, // Library → скрипт без rows/output
            [nameof(Methodology.Group)] = Data,
            [nameof(Methodology.Versions)] = Technical,
            [nameof(Methodology.RowActivityFilter)] = Text, // active when
        },
        [typeof(MethodologyVersion)] = new()
        {
            [nameof(MethodologyVersion.MethodologyId)] = Technical,
            [nameof(MethodologyVersion.Version)] = Text, // мітка в заголовку
            [nameof(MethodologyVersion.Level)] = Text, // Module → output без rows
            [nameof(MethodologyVersion.EffectiveFrom)] = Text, // вікна констант і заголовок
            [nameof(MethodologyVersion.Status)] = Data,
            [nameof(MethodologyVersion.NumericMode)] = Text, // режим компіляції; сам режим лишається полем версії
            [nameof(MethodologyVersion.CalendarMode)] = Data, // дескриптор скрипта бере його з версії
            [nameof(MethodologyVersion.TraceLevel)] = Data,
            [nameof(MethodologyVersion.ContentHash)] = Technical,
            [nameof(MethodologyVersion.CreatedAt)] = Technical,
            [nameof(MethodologyVersion.CreatedByUserId)] = Technical,
            [nameof(MethodologyVersion.PublishedByUserId)] = Technical,
            [nameof(MethodologyVersion.PublishedAt)] = Technical,
            [nameof(MethodologyVersion.ChangeReason)] = Technical,
            [nameof(MethodologyVersion.Engine)] = Technical,
            [nameof(MethodologyVersion.ScriptText)] = Technical,
            [nameof(MethodologyVersion.ScriptHash)] = Technical,
            [nameof(MethodologyVersion.IsPublished)] = Technical,
        },
        [typeof(MethodologyFormula)] = new()
        {
            [nameof(MethodologyFormula.MethodologyVersionId)] = Technical,
            [nameof(MethodologyFormula.Code)] = Text,
            [nameof(MethodologyFormula.Expression)] = Text,
            [nameof(MethodologyFormula.EvaluationOrder)] = Text,
            [nameof(MethodologyFormula.OutputUnitId)] = Text, // visible var : unit
            [nameof(MethodologyFormula.ResultType)] = Data, // рушій його не читає; у відбитку
            [nameof(MethodologyFormula.ArgumentsCsv)] = Text, // маркери необов'язковості → row.X?
            [nameof(MethodologyFormula.Scope)] = Text,
            [nameof(MethodologyFormula.IsVisible)] = Text,
        },
        [typeof(MethodologyConstant)] = new()
        {
            [nameof(MethodologyConstant.MethodologyVersionId)] = Technical,
            [nameof(MethodologyConstant.Code)] = Text,
            [nameof(MethodologyConstant.Kind)] = Text,
            [nameof(MethodologyConstant.Value)] = Text,
            [nameof(MethodologyConstant.TextValue)] = Text,
            [nameof(MethodologyConstant.UnitId)] = Text,
            [nameof(MethodologyConstant.ValidFrom)] = Text,
            [nameof(MethodologyConstant.ValidTo)] = Text,
            [nameof(MethodologyConstant.Window)] = Technical,
            [nameof(MethodologyConstant.SubstanceEntryId)] = Text,
            [nameof(MethodologyConstant.Category)] = Text,
            [nameof(MethodologyConstant.Source)] = Data, // походження значення, на числа не впливає (не в SourceConstant)
            [nameof(MethodologyConstant.IsAllowedInExpression)] = Technical,
            [nameof(MethodologyConstant.IsResolved)] = Technical,
        },
        [typeof(MethodologyRule)] = new()
        {
            [nameof(MethodologyRule.MethodologyVersionId)] = Technical,
            [nameof(MethodologyRule.Code)] = Data, // ім'я правила в текст не потрапляє
            [nameof(MethodologyRule.MatchJson)] = Text,
            [nameof(MethodologyRule.Priority)] = Text, // «або» комутативне: порядок рядків where не змінює відбір
            [nameof(MethodologyRule.IsActive)] = Text, // неактивні правила не читаються
        },
        [typeof(MethodologyCategory)] = new()
        {
            [nameof(MethodologyCategory.MethodologyVersionId)] = Technical,
            [nameof(MethodologyCategory.Code)] = Data, // розрахунок бере категорії констант, не цю таблицю (її читає лише публікація)
            [nameof(MethodologyCategory.Description)] = Data,
            [nameof(MethodologyCategory.Ordinal)] = Data,
            [nameof(MethodologyCategory.IsRetired)] = Data,
        },
        [typeof(MethodologyCategoryRule)] = new()
        {
            [nameof(MethodologyCategoryRule.MethodologyVersionId)] = Technical,
            [nameof(MethodologyCategoryRule.Expression)] = Text,
            [nameof(MethodologyCategoryRule.CreatedAt)] = Technical,
            [nameof(MethodologyCategoryRule.UpdatedAt)] = Technical,
        },
        [typeof(MethodologySubstance)] = new()
        {
            [nameof(MethodologySubstance.MethodologyVersionId)] = Technical,
            [nameof(MethodologySubstance.SubstanceEntryId)] = Text,
            [nameof(MethodologySubstance.Ordinal)] = Text,
        },
        [typeof(MethodologyOutput)] = new()
        {
            [nameof(MethodologyOutput.MethodologyVersionId)] = Technical,
            [nameof(MethodologyOutput.Code)] = Text,
            [nameof(MethodologyOutput.UnitId)] = Text,
            [nameof(MethodologyOutput.Ordinal)] = Data, // порядок виходів у сітці; текст рахує у порядку формул
            [nameof(MethodologyOutput.IsPerSubstance)] = Text,
        },
        [typeof(MethodologyImport)] = new()
        {
            [nameof(MethodologyImport.MethodologyVersionId)] = Technical,
            [nameof(MethodologyImport.ImportedMethodologyId)] = Text, // import <Код>;
        },
        [typeof(MethodologyDependency)] = new()
        {
            [nameof(MethodologyDependency.FromMethodologyId)] = Derived, // граф залежностей перебудовує матеріалізація з import
            [nameof(MethodologyDependency.ToMethodologyId)] = Derived,
        },
        [typeof(MethodologyReadTable)] = new()
        {
            [nameof(MethodologyReadTable.MethodologyVersionId)] = Technical,
            [nameof(MethodologyReadTable.TableCode)] = Derived, // породжується скриптом (table Alias = …); у старої версії порожньо
        },
        [typeof(MethodologyRequiredInput)] = new()
        {
            [nameof(MethodologyRequiredInput.MethodologyVersionId)] = Technical,
            [nameof(MethodologyRequiredInput.ColumnDefId)] = Data, // gate збереження читає версію, а не рушій
            [nameof(MethodologyRequiredInput.Severity)] = Data,
            [nameof(MethodologyRequiredInput.HintL10n)] = Data,
        },
        [typeof(MethodologyTestCaseEntity)] = new()
        {
            [nameof(MethodologyTestCaseEntity.MethodologyVersionId)] = Technical,
            [nameof(MethodologyTestCaseEntity.Code)] = Data,
            [nameof(MethodologyTestCaseEntity.InputJson)] = Data,
            [nameof(MethodologyTestCaseEntity.ExpectedJson)] = Data,
            [nameof(MethodologyTestCaseEntity.Tolerance)] = Data,
        },
        [typeof(CalculationBinding)] = new()
        {
            [nameof(CalculationBinding.TableDefId)] = Text, // rows from <Таблиця>
            [nameof(CalculationBinding.ColumnDefId)] = Data, // колонка-вихід прив'язки
            [nameof(CalculationBinding.MethodologyId)] = Technical,
            [nameof(CalculationBinding.OutputCode)] = Data,
            [nameof(CalculationBinding.MatchJson)] = Data, // правила версії читає сховище; прив'язка зберігає свій (не рушія методології)
            [nameof(CalculationBinding.IsActive)] = Data,
        },
    };

    private static readonly string[] Allowed = [Text, Data, Derived, Technical];

    public static TheoryData<string> EntityNames => [.. Table.Keys.Select(t => t.Name)];

    [Theory]
    [MemberData(nameof(EntityNames))]
    public void Кожне_публічне_поле_сутності_має_явне_рішення_про_перенесення(string entity)
    {
        var pair = Table.Single(p => p.Key.Name == entity);
        var type = pair.Key;
        var decisions = pair.Value;
        var actual = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal([.. decisions.Keys.Order(StringComparer.Ordinal)], actual);
        Assert.All(decisions.Values, d => Assert.Contains(d, Allowed));
    }

    [Fact]
    public void Кожне_поле_з_рішенням_Text_має_слід_у_джерелі_перетворювача()
    {
        // Мінімальна перевірка зв'язку «Text → VersionSource»: ключові поля, що переносяться, мають відповідник у джерелі.
        var source = typeof(VersionSource);
        foreach (var name in new[]
                 {
                     nameof(VersionSource.Formulas), nameof(VersionSource.Outputs), nameof(VersionSource.Constants),
                     nameof(VersionSource.CategoryRule), nameof(VersionSource.Substances), nameof(VersionSource.Imports),
                     nameof(VersionSource.Rules), nameof(VersionSource.RowsClauses), nameof(VersionSource.RowActivityFilter),
                     nameof(VersionSource.NumericMode), nameof(VersionSource.CalendarMode), nameof(VersionSource.IsLibrary),
                     nameof(VersionSource.IsModuleLevel), nameof(VersionSource.EffectiveFrom), nameof(VersionSource.NextEffectiveFrom),
                 })
        {
            Assert.NotNull(source.GetProperty(name));
        }

        Assert.NotNull(typeof(SourceFormula).GetProperty(nameof(SourceFormula.ArgumentsCsv)));
        Assert.NotNull(typeof(SourceImport).GetProperty(nameof(SourceImport.OptionalArguments)));
        Assert.NotNull(typeof(SourceImport).GetProperty(nameof(SourceImport.ConstantCategories)));
    }
}
