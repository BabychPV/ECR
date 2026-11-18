// src/Ecr.Application/DocumentDependencies/DependencyGraphModel.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Binding;

namespace Ecr.Application.DocumentDependencies;

/// <summary>B179 L1: вид вузла графа залежностей.</summary>
public enum DependencyNodeKind : byte
{
    /// <summary>Шаблон (рівень шаблону, не версії; версії — у доказах ребер).</summary>
    Template = 0,

    /// <summary>Довідник.</summary>
    Registry = 1,

    /// <summary>Методологія (версії — у доказах ребер).</summary>
    Methodology = 2,
}

/// <summary>B179 L1: вид ребра «читає/наповнює».</summary>
public enum DependencyEdgeKind : byte
{
    /// <summary>Шаблон-джерело наповнює довідник (<c>cfg.DocumentSourceBinding</c>).</summary>
    Fills = 0,

    /// <summary>Формула шаблону читає довідник (<c>cfg.FormulaDependency</c> вид 2 або AST).</summary>
    TemplateFormula = 1,

    /// <summary>Формула версії методології читає довідник (<c>cfg.RegistryUse</c> вид 1).</summary>
    Methodology = 2,

    /// <summary>Методологія пише в колонку шаблону (<c>cfg.CalculationBinding</c>).</summary>
    WritesInto = 3,

    /// <summary>Правило довідника читає інший довідник (<c>cfg.RegistryUse</c> вид 2).</summary>
    RegistryRule = 4,
}

/// <summary>Адреса вузла.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Id">Id шаблону / довідника / методології.</param>
public readonly record struct DependencyNodeRef(DependencyNodeKind Kind, int Id);

/// <summary>Вузол графа.</summary>
/// <param name="Ref">Адреса.</param>
/// <param name="Code">Код шаблону / довідника / методології.</param>
public sealed record DependencyNode(DependencyNodeRef Ref, string Code);

/// <summary>Звідки відоме ребро.</summary>
/// <param name="Source">
/// <c>formulaDependency</c>, <c>ast</c>, <c>lookupColumn</c> (формула шаблону; <paramref name="Id"/> — <c>FormulaDef</c>),
/// <c>registryUse</c> (методологія; <paramref name="Id"/> — версія), <c>calculationBinding</c>, <c>sourceBinding</c>,
/// <c>registryRule</c>.
/// </param>
/// <param name="Id">Id формули / версії методології / прив'язки / правила.</param>
/// <param name="VersionId">Версія шаблону (для формул шаблону); інакше <c>null</c>.</param>
/// <param name="Detail">Таблиця/колонка або код формули методології.</param>
public sealed record DependencyEvidence(string Source, int Id, int? VersionId, string? Detail);

/// <summary>Ребро «<paramref name="From"/> → <paramref name="To"/>» (дані течуть за стрілкою).</summary>
/// <param name="From">Звідки.</param>
/// <param name="To">Куди.</param>
/// <param name="Kind">Вид.</param>
/// <param name="Scope">Обсяг за періодом (об'єднання по доказах).</param>
/// <param name="Evidence">Докази.</param>
public sealed record DependencyEdge(
    DependencyNodeRef From,
    DependencyNodeRef To,
    DependencyEdgeKind Kind,
    PeriodScope Scope,
    IReadOnlyList<DependencyEvidence> Evidence);

/// <summary>Згорнуте ребро рівня шаблонів: документи <paramref name="ConsumerTemplateId"/> читають дані джерела.</summary>
/// <param name="SourceTemplateId">Шаблон-джерело (наповнює довідник зв'язки).</param>
/// <param name="ConsumerTemplateId">Шаблон-споживач.</param>
/// <param name="BindingId">Зв'язка <c>cfg.DocumentSourceBinding</c>.</param>
/// <param name="RegistryDefId">Довідник зв'язки.</param>
/// <param name="Scope">Обсяг за періодом (об'єднання формул і методологій споживача).</param>
/// <param name="ViaTemplateFormula">Споживача знайдено за формулою шаблону.</param>
/// <param name="ViaMethodology">Споживача знайдено за методологією, прив'язаною до нього.</param>
public sealed record TemplateDependency(
    int SourceTemplateId,
    int ConsumerTemplateId,
    int BindingId,
    int RegistryDefId,
    PeriodScope Scope,
    bool ViaTemplateFormula,
    bool ViaMethodology);

/// <summary>Зауваження побудови (ключ — для екрана L5).</summary>
/// <param name="Key"><c>cycle</c>, <c>astFallback</c>, <c>parseFailed</c>, <c>unknownRegistry</c>.</param>
/// <param name="Detail">Що саме.</param>
public sealed record DependencyGraphDiagnostic(string Key, string Detail);

/// <summary>B179 L1: граф залежностей рівня шаблонів (незмінний, спільний між запитами).</summary>
/// <param name="Revision">Ревізія даних, з яких побудовано (ключ кешу).</param>
/// <param name="Nodes">Вузли.</param>
/// <param name="Edges">Ребра з доказами.</param>
/// <param name="TemplateDependencies">Згорнуті ребра «джерело → споживач».</param>
/// <param name="Levels">Рівні Кана шаблонів (рівень 0 — без вхідних); компонента циклу — один рівень.</param>
/// <param name="Cycles">Сильно зв'язні компоненти з циклом (зокрема петля) — шаблони.</param>
/// <param name="Diagnostics">Зауваження побудови.</param>
public sealed record DependencyGraphSnapshot(
    string Revision,
    IReadOnlyList<DependencyNode> Nodes,
    IReadOnlyList<DependencyEdge> Edges,
    IReadOnlyList<TemplateDependency> TemplateDependencies,
    IReadOnlyList<IReadOnlyList<int>> Levels,
    IReadOnlyList<IReadOnlyList<int>> Cycles,
    IReadOnlyList<DependencyGraphDiagnostic> Diagnostics)
{
    /// <summary>Споживачі шаблону-джерела.</summary>
    /// <param name="sourceTemplateId">Шаблон-джерело.</param>
    public IReadOnlyList<TemplateDependency> ConsumersOf(int sourceTemplateId)
        => [.. TemplateDependencies.Where(d => d.SourceTemplateId == sourceTemplateId)];
}

/// <summary>Зв'язка «шаблон-джерело наповнює довідник» (лише ідентичність; споживачів зв'язки граф НЕ читає — дизайн §7).</summary>
/// <param name="BindingId">Зв'язка.</param>
/// <param name="SourceTemplateId">Шаблон-джерело.</param>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="SourceState">Стан-ворота.</param>
public sealed record SourceBindingRow(int BindingId, int SourceTemplateId, int RegistryDefId, DocumentSourceState SourceState);

/// <summary>Формула шаблону читає довідник.</summary>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="TemplateVersionId">Версія.</param>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="Scope">Обсяг.</param>
/// <param name="FormulaDefId">Формула.</param>
/// <param name="Source"><c>formulaDependency</c>, <c>ast</c> або <c>lookupColumn</c>.</param>
/// <param name="Detail">Таблиця/колонка формули.</param>
public sealed record TemplateRegistryRead(
    int TemplateId, int TemplateVersionId, int RegistryDefId, PeriodScope Scope, int FormulaDefId, string Source, string Detail);

/// <summary>Формула версії методології читає довідник (обсяг уже пораховано).</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyVersionId">Версія.</param>
/// <param name="RegistryDefId">Довідник.</param>
/// <param name="FormulaCode">Код формули версії.</param>
/// <param name="Scope">Обсяг.</param>
public sealed record MethodologyScopedRead(
    int MethodologyId, int MethodologyVersionId, int RegistryDefId, string FormulaCode, PeriodScope Scope);

/// <summary>Методологія пише в шаблон (версію таблиці прив'язки зведено до шаблону).</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="TemplateId">Шаблон таблиці прив'язки.</param>
/// <param name="CalculationBindingId">Прив'язка.</param>
/// <param name="Detail">Таблиця/колонка/вихід.</param>
public sealed record MethodologyTemplateWrite(int MethodologyId, int TemplateId, int CalculationBindingId, string Detail);

/// <summary>Правило довідника <paramref name="RuleRegistryDefId"/> читає довідник <paramref name="ReadRegistryDefId"/>.</summary>
/// <param name="RuleId">Правило.</param>
/// <param name="RuleRegistryDefId">Довідник правила.</param>
/// <param name="ReadRegistryDefId">Довідник, який правило читає.</param>
public sealed record RegistryRuleRead(int RuleId, int RuleRegistryDefId, int ReadRegistryDefId);

/// <summary>Сирі ребра, прочитані з бази, для <see cref="DependencyGraphAssembler"/>.</summary>
/// <param name="Revision">Ревізія даних.</param>
/// <param name="TemplateCodes">Шаблон → код.</param>
/// <param name="RegistryCodes">Довідник → код.</param>
/// <param name="MethodologyCodes">Методологія → код.</param>
/// <param name="Bindings">Зв'язки джерел.</param>
/// <param name="TemplateReads">Читання довідників формулами шаблонів.</param>
/// <param name="MethodologyReads">Читання довідників методологіями.</param>
/// <param name="MethodologyWrites">Методологія → шаблон.</param>
/// <param name="RuleReads">Правила довідників.</param>
/// <param name="Diagnostics">Зауваження читання.</param>
public sealed record DependencyGraphInputs(
    string Revision,
    IReadOnlyDictionary<int, string> TemplateCodes,
    IReadOnlyDictionary<int, string> RegistryCodes,
    IReadOnlyDictionary<int, string> MethodologyCodes,
    IReadOnlyList<SourceBindingRow> Bindings,
    IReadOnlyList<TemplateRegistryRead> TemplateReads,
    IReadOnlyList<MethodologyScopedRead> MethodologyReads,
    IReadOnlyList<MethodologyTemplateWrite> MethodologyWrites,
    IReadOnlyList<RegistryRuleRead> RuleReads,
    IReadOnlyList<DependencyGraphDiagnostic> Diagnostics);
