import type { components } from '@/api/schema';

/** Граф залежностей документів (`GET /api/v1/dependency-graph`, `B179 L4`). */
export type DependencyGraph = components['schemas']['DependencyGraphDto'];

/** Ребро графа з доказами. */
export type DependencyEdge = components['schemas']['DependencyEdgeDto'];

/** Доказ ребра. */
export type DependencyEvidence = components['schemas']['DependencyEvidenceDto'];

/** Згорнуте ребро «документ-джерело → документ-споживач» зі зв'язкою. */
export type TemplateLink = components['schemas']['DependencyTemplateLinkDto'];

/** Зауваження побудови графа. */
export type DependencyDiagnostic = components['schemas']['DependencyDiagnosticDto'];

/**
 * Методологія, через яку споживач читає дані джерела.
 *
 * `direct` — методологія сама читає довідник зв'язки (`cfg.RegistryUse`); інакше вона читає РЕЗУЛЬТАТИ іншої методології
 * (`via` — її код), яка читає довідник, — транзитивно.
 */
export interface MethodologyVia {
  readonly code: string;
  readonly direct: boolean;
  readonly via: string | null;
  /** Коди формул/скрипта методології, що читають довідник (докази ребра довідник → методологія). */
  readonly formulas: readonly string[];
}

/** Один споживач документа-джерела: звідки читає, як це доведено. */
export interface ReadLink {
  readonly link: TemplateLink;
  readonly sourceCode: string;
  readonly registryCode: string;
  /** Докази читання формулами шаблону (з обмеженням сервера; скільки всього — `formulaTotal`). */
  readonly formulaEvidence: readonly DependencyEvidence[];
  readonly formulaTotal: number;
  readonly methodologies: readonly MethodologyVia[];
}

/** Зв'язка, яку цей шаблон-джерело наповнює (місце перемикачів). */
export interface FillsLink {
  readonly bindingId: number;
  readonly registryCode: string;
  readonly consumerCodes: readonly string[];
}

/** Шаблон на рівні графа. */
export interface TemplateView {
  readonly id: number;
  readonly code: string;
  readonly level: number;
  readonly onCycle: boolean;
  readonly fills: readonly FillsLink[];
  readonly reads: readonly ReadLink[];
}

/** Рівень порядку обчислення. */
export interface LevelView {
  readonly level: number;
  readonly templates: readonly TemplateView[];
}

/** Цикл шаблонів кодами. */
export interface CycleView {
  readonly codes: readonly string[];
}

/** Модель екрана «Граф залежностей». */
export interface GraphView {
  readonly levels: readonly LevelView[];
  readonly cycles: readonly CycleView[];
  readonly diagnostics: readonly DependencyDiagnostic[];
  readonly templateCount: number;
  readonly linkCount: number;
  readonly truncated: boolean;
}

const byCode = (a: { readonly code: string }, b: { readonly code: string }): number =>
  a.code.localeCompare(b.code, 'en', { sensitivity: 'base' });

/**
 * Методології, через які шаблон `consumerId` читає довідник `registryId`: прямі (читають довідник) і транзитивні
 * (читають результати методології, що читає довідник). Обхід ребер `registry → methodology` і
 * `methodology → methodology` (читання результатів) вшир; доказ прямого читання — коди формул ребра довідник → методологія.
 */
function methodologiesInto(
  graph: DependencyGraph,
  methodologyCodes: ReadonlyMap<number, string>,
  registryId: number,
  consumerId: number,
): MethodologyVia[] {
  const direct = new Map<number, string[]>();
  for (const edge of graph.edges) {
    if (edge.kind === 'methodology' && edge.fromKind === 'registry' && edge.fromId === registryId) {
      direct.set(
        edge.toId,
        edge.evidence.map((e) => e.detail).filter((d): d is string => typeof d === 'string' && d.length > 0),
      );
    }
  }

  const parent = new Map<number, number | null>([...direct.keys()].map((id) => [id, null]));
  const queue = [...direct.keys()];

  for (let at = 0; at < queue.length; at += 1) {
    const current = queue[at] as number;

    for (const edge of graph.edges) {
      if (
        edge.kind === 'methodologyResult' &&
        edge.fromKind === 'methodology' &&
        edge.fromId === current &&
        !parent.has(edge.toId)
      ) {
        parent.set(edge.toId, current);
        queue.push(edge.toId);
      }
    }
  }

  const writers = new Set(
    graph.edges
      .filter((e) => e.kind === 'writesInto' && e.toKind === 'template' && e.toId === consumerId && parent.has(e.fromId))
      .map((e) => e.fromId),
  );

  return [...writers]
    .map((id): MethodologyVia => {
      const from = parent.get(id) ?? null;

      return {
        code: methodologyCodes.get(id) ?? String(id),
        direct: from === null,
        via: from === null ? null : (methodologyCodes.get(from) ?? String(from)),
        formulas: direct.get(id) ?? [],
      };
    })
    .sort(byCode);
}

/**
 * Модель екрана з відповіді сервера.
 *
 * ⚠ Рівні — рівно ті, що віддав сервер (`TemplateGraphOrder`): клієнт порядку не переобчислює. Шаблон, якого немає в
 * жодному рівні (граф урізано), на екран не потрапляє — урізання показує окремий рядок (`truncated`).
 */
export function buildGraphView(graph: DependencyGraph): GraphView {
  const templates = new Map<number, string>();
  const registries = new Map<number, string>();
  const methodologies = new Map<number, string>();

  for (const node of graph.nodes) {
    if (node.kind === 'template') templates.set(node.id, node.code);
    else if (node.kind === 'registry') registries.set(node.id, node.code);
    else if (node.kind === 'methodology') methodologies.set(node.id, node.code);
  }

  const cycled = new Set(graph.cycles.flatMap((c) => c.templateIds));
  const codeOf = (id: number): string => templates.get(id) ?? `#${String(id)}`;

  const levels = [...graph.levels]
    .sort((a, b) => a.level - b.level)
    .map((level): LevelView => ({
      level: level.level,
      templates: level.templateIds
        .map((id): TemplateView => {
          const reads = graph.templateDependencies
            .filter((d) => d.consumerTemplateId === id)
            .map((link): ReadLink => {
              const formulaEdge = graph.edges.find(
                (e) =>
                  e.kind === 'templateFormula' &&
                  e.fromKind === 'registry' &&
                  e.fromId === link.registryDefId &&
                  e.toKind === 'template' &&
                  e.toId === id,
              );

              return {
                link,
                sourceCode: codeOf(link.sourceTemplateId),
                registryCode: registries.get(link.registryDefId) ?? `#${String(link.registryDefId)}`,
                formulaEvidence: formulaEdge?.evidence ?? [],
                formulaTotal: formulaEdge?.evidenceTotal ?? 0,
                methodologies: link.viaMethodology
                  ? methodologiesInto(graph, methodologies, link.registryDefId, id)
                  : [],
              };
            })
            .sort((a, b) => a.sourceCode.localeCompare(b.sourceCode, 'en', { sensitivity: 'base' }));

          const fills = fillsOf(graph, templates, registries, id);

          return { id, code: codeOf(id), level: level.level, onCycle: cycled.has(id), fills, reads };
        })
        .sort(byCode),
    }));

  return {
    levels,
    cycles: graph.cycles.map((c) => ({ codes: c.templateIds.map(codeOf) })),
    diagnostics: graph.diagnostics,
    templateCount: levels.reduce((n, l) => n + l.templates.length, 0),
    linkCount: graph.templateDependencies.length,
    truncated: graph.truncated,
  };
}

/** Зв'язки, які шаблон наповнює (ребра `fills`), із кодами споживачів. */
function fillsOf(
  graph: DependencyGraph,
  templates: ReadonlyMap<number, string>,
  registries: ReadonlyMap<number, string>,
  templateId: number,
): FillsLink[] {
  const result: FillsLink[] = [];

  for (const edge of graph.edges) {
    if (edge.kind !== 'fills' || edge.fromKind !== 'template' || edge.fromId !== templateId) continue;

    for (const evidence of edge.evidence) {
      if (evidence.source !== 'sourceBinding') continue;

      result.push({
        bindingId: evidence.id,
        registryCode: registries.get(edge.toId) ?? `#${String(edge.toId)}`,
        consumerCodes: graph.templateDependencies
          .filter((d) => d.bindingId === evidence.id)
          .map((d) => templates.get(d.consumerTemplateId) ?? `#${String(d.consumerTemplateId)}`)
          .sort((a, b) => a.localeCompare(b, 'en', { sensitivity: 'base' })),
      });
    }
  }

  return result.sort((a, b) => a.registryCode.localeCompare(b.registryCode, 'en', { sensitivity: 'base' }));
}
