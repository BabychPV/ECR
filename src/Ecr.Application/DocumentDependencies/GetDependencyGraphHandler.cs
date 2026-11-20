// src/Ecr.Application/DocumentDependencies/GetDependencyGraphHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Expressions.Binding;

namespace Ecr.Application.DocumentDependencies;

/// <summary>Вузол графа залежностей.</summary>
/// <param name="Kind"><c>template</c>, <c>registry</c> або <c>methodology</c>.</param>
/// <param name="Id">Id шаблону / довідника / методології.</param>
/// <param name="Code">Код.</param>
public sealed record DependencyNodeDto(string Kind, int Id, string Code);

/// <summary>Доказ ребра: звідки відомо.</summary>
/// <param name="Source">
/// <c>formulaDependency</c>, <c>ast</c>, <c>lookupColumn</c>, <c>registryUse</c>, <c>calculationBinding</c>,
/// <c>sourceBinding</c>, <c>registryRule</c>, <c>methodologyDependency</c>.
/// </param>
/// <param name="Id">Id формули / версії методології / прив'язки / правила.</param>
/// <param name="VersionId">Версія шаблону (для формул шаблону); інакше <c>null</c>.</param>
/// <param name="Detail">Таблиця/колонка або код формули методології.</param>
public sealed record DependencyEvidenceDto(string Source, int Id, int? VersionId, string? Detail);

/// <summary>Ребро графа: дані течуть від <c>From</c> до <c>To</c>.</summary>
/// <param name="Kind"><c>fills</c>, <c>templateFormula</c>, <c>methodology</c>, <c>writesInto</c>, <c>registryRule</c>, <c>methodologyResult</c>.</param>
/// <param name="FromKind">Вид вузла-початку.</param>
/// <param name="FromId">Id вузла-початку.</param>
/// <param name="ToKind">Вид вузла-кінця.</param>
/// <param name="ToId">Id вузла-кінця.</param>
/// <param name="Scope">Обсяг за періодом: <c>month</c>, <c>year</c> (з <paramref name="MonthMask"/>) або <c>all</c>.</param>
/// <param name="MonthMask">Для <c>year</c>: біти 1…12 літеральних місяців; 0 — увесь рік.</param>
/// <param name="Evidence">Докази (не більше 25).</param>
/// <param name="EvidenceTotal">Скільки доказів усього (більше за <paramref name="Evidence"/> — перелік урізано).</param>
public sealed record DependencyEdgeDto(
    string Kind,
    string FromKind,
    int FromId,
    string ToKind,
    int ToId,
    string Scope,
    int MonthMask,
    IReadOnlyList<DependencyEvidenceDto> Evidence,
    int EvidenceTotal);

/// <summary>Згорнуте ребро «документ шаблону-джерела → документи шаблону-споживача».</summary>
/// <param name="SourceTemplateId">Шаблон-джерело.</param>
/// <param name="ConsumerTemplateId">Шаблон-споживач.</param>
/// <param name="BindingId">Зв'язка джерела (<c>/document-source-bindings</c>).</param>
/// <param name="RegistryDefId">Довідник зв'язки.</param>
/// <param name="Scope">Обсяг за періодом: <c>month</c>, <c>year</c> або <c>all</c>.</param>
/// <param name="MonthMask">Для <c>year</c>: біти літеральних місяців; 0 — увесь рік.</param>
/// <param name="ViaTemplateFormula">Споживача знайдено за формулою шаблону.</param>
/// <param name="ViaMethodology">Споживача знайдено за методологією.</param>
public sealed record DependencyTemplateLinkDto(
    int SourceTemplateId,
    int ConsumerTemplateId,
    int BindingId,
    int RegistryDefId,
    string Scope,
    int MonthMask,
    bool ViaTemplateFormula,
    bool ViaMethodology);

/// <summary>Рівень графа шаблонів: усі шаблони рівня обчислюються після шаблонів нижчих рівнів.</summary>
/// <param name="Level">Номер рівня з 0 (без вхідних ребер).</param>
/// <param name="TemplateIds">Шаблони рівня.</param>
public sealed record DependencyLevelDto(int Level, IReadOnlyList<int> TemplateIds);

/// <summary>Цикл: сильно зв'язна компонента шаблонів.</summary>
/// <param name="TemplateIds">Шаблони циклу.</param>
public sealed record DependencyCycleDto(IReadOnlyList<int> TemplateIds);

/// <summary>Зауваження побудови графа.</summary>
/// <param name="Key"><c>cycle</c>, <c>astFallback</c>, <c>parseFailed</c>, <c>unknownRegistry</c>.</param>
/// <param name="Detail">Що саме.</param>
public sealed record DependencyDiagnosticDto(string Key, string Detail);

/// <summary>Граф залежностей документів для екрана адміністратора (B179 L4).</summary>
/// <param name="Revision">Ревізія даних, з яких побудовано граф.</param>
/// <param name="Nodes">Вузли: шаблони, довідники, методології.</param>
/// <param name="Edges">Ребра з доказами.</param>
/// <param name="TemplateDependencies">Згорнуті ребра «джерело → споживач» зі зв'язкою.</param>
/// <param name="Levels">Рівні шаблонів у порядку обчислення.</param>
/// <param name="Cycles">Цикли шаблонів (зауваження; публікація, що їх замикає, відхиляється).</param>
/// <param name="Diagnostics">Зауваження побудови.</param>
/// <param name="Truncated"><c>true</c> — ребер більше за стелю 5000, перелік урізано.</param>
public sealed record DependencyGraphDto(
    string Revision,
    IReadOnlyList<DependencyNodeDto> Nodes,
    IReadOnlyList<DependencyEdgeDto> Edges,
    IReadOnlyList<DependencyTemplateLinkDto> TemplateDependencies,
    IReadOnlyList<DependencyLevelDto> Levels,
    IReadOnlyList<DependencyCycleDto> Cycles,
    IReadOnlyList<DependencyDiagnosticDto> Diagnostics,
    bool Truncated);

/// <summary>
/// <c>GET /api/v1/dependency-graph</c> — граф залежностей документів (B179 L4): шаблони, довідники й методології,
/// ребра з видом і доказами, рівні, цикли й зауваження. Право <c>Calculation.ManageSourceBinding</c> (як у зв'язок).
/// </summary>
/// <remarks>
/// ⚠ Граф кешований під ключем ревізії (<see cref="IDependencyGraphProvider.GetAsync"/>, до 5 с несвіжості). Стелі
/// відповіді (правило 6): ребер — <see cref="MaxEdges"/>, доказів на ребро — <see cref="MaxEvidencePerEdge"/> (скільки
/// всього — у <c>evidenceTotal</c>).
/// </remarks>
public sealed class GetDependencyGraphHandler(
    IDependencyGraphProvider graph,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право (те саме, що <c>DocumentSourceBindings.Permission</c>).</summary>
    public const string Permission = "Calculation.ManageSourceBinding";

    /// <summary>Найбільше ребер у відповіді.</summary>
    public const int MaxEdges = 5000;

    /// <summary>Найбільше доказів на одне ребро.</summary>
    public const int MaxEvidencePerEdge = 25;

    /// <summary>Читає граф.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DependencyGraphDto> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        return ToDto(await graph.GetAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Складає DTO зі знімка графа.</summary>
    /// <param name="snapshot">Знімок.</param>
    public static DependencyGraphDto ToDto(DependencyGraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var edges = snapshot.Edges
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.From.Kind)
            .ThenBy(e => e.From.Id)
            .ThenBy(e => e.To.Kind)
            .ThenBy(e => e.To.Id)
            .Take(MaxEdges)
            .Select(e => new DependencyEdgeDto(
                EdgeKind(e.Kind),
                NodeKind(e.From.Kind),
                e.From.Id,
                NodeKind(e.To.Kind),
                e.To.Id,
                ScopeName(e.Scope),
                e.Scope.MonthMask,
                [.. e.Evidence.Take(MaxEvidencePerEdge).Select(v => new DependencyEvidenceDto(v.Source, v.Id, v.VersionId, v.Detail))],
                e.Evidence.Count))
            .ToList();

        return new DependencyGraphDto(
            snapshot.Revision,
            [.. snapshot.Nodes
                .OrderBy(n => n.Ref.Kind)
                .ThenBy(n => n.Code, StringComparer.OrdinalIgnoreCase)
                .Select(n => new DependencyNodeDto(NodeKind(n.Ref.Kind), n.Ref.Id, n.Code))],
            edges,
            [.. snapshot.TemplateDependencies
                .OrderBy(d => d.SourceTemplateId)
                .ThenBy(d => d.ConsumerTemplateId)
                .ThenBy(d => d.BindingId)
                .Select(d => new DependencyTemplateLinkDto(
                    d.SourceTemplateId,
                    d.ConsumerTemplateId,
                    d.BindingId,
                    d.RegistryDefId,
                    ScopeName(d.Scope),
                    d.Scope.MonthMask,
                    d.ViaTemplateFormula,
                    d.ViaMethodology))],
            [.. snapshot.Levels.Select((ids, i) => new DependencyLevelDto(i, ids))],
            [.. snapshot.Cycles.Select(ids => new DependencyCycleDto(ids))],
            [.. snapshot.Diagnostics.Select(d => new DependencyDiagnosticDto(d.Key, d.Detail))],
            snapshot.Edges.Count > MaxEdges);
    }

    private static string NodeKind(DependencyNodeKind kind)
        => kind switch
        {
            DependencyNodeKind.Template => "template",
            DependencyNodeKind.Registry => "registry",
            DependencyNodeKind.Methodology => "methodology",
            _ => kind.ToString().ToLower(CultureInfo.InvariantCulture),
        };

    private static string EdgeKind(DependencyEdgeKind kind)
        => kind switch
        {
            DependencyEdgeKind.Fills => "fills",
            DependencyEdgeKind.TemplateFormula => "templateFormula",
            DependencyEdgeKind.Methodology => "methodology",
            DependencyEdgeKind.WritesInto => "writesInto",
            DependencyEdgeKind.RegistryRule => "registryRule",
            DependencyEdgeKind.MethodologyResult => "methodologyResult",
            _ => kind.ToString(),
        };

    private static string ScopeName(PeriodScope scope)
        => scope.Kind switch
        {
            PeriodScopeKind.Month => "month",
            PeriodScopeKind.Year => "year",
            _ => "all",
        };
}
