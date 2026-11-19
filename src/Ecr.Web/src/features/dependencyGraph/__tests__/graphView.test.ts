import { describe, expect, it } from 'vitest';
import { buildGraphView, type DependencyGraph } from '@/features/dependencyGraph/graphView';

/**
 * `B179 L5`: модель екрана з відповіді `GET /api/v1/dependency-graph` — рівні як віддав сервер, зв'язки, які шаблон
 * наповнює, читання з доказами (формули шаблону, методології прямі й транзитивні), цикли.
 *
 * Світ: HSE400 (1) наповнює довідник COMP (10) зв'язкою 5. LAND (2) читає його формулою шаблону; REPORT (3) — через
 * методологію M1 (20), що читає довідник; TOTAL (4) — через M2 (21), що читає РЕЗУЛЬТАТИ M1 (транзитивно).
 */
function world(overrides: Partial<DependencyGraph> = {}): DependencyGraph {
  const link = (consumer: number, viaFormula: boolean, viaMethodology: boolean) => ({
    sourceTemplateId: 1,
    consumerTemplateId: consumer,
    bindingId: 5,
    registryDefId: 10,
    scope: 'month',
    monthMask: 0,
    viaTemplateFormula: viaFormula,
    viaMethodology,
  });

  return {
    revision: 'r1',
    truncated: false,
    nodes: [
      { kind: 'template', id: 1, code: 'HSE400' },
      { kind: 'template', id: 2, code: 'LAND' },
      { kind: 'template', id: 3, code: 'REPORT' },
      { kind: 'template', id: 4, code: 'TOTAL' },
      { kind: 'registry', id: 10, code: 'COMP' },
      { kind: 'methodology', id: 20, code: 'M1' },
      { kind: 'methodology', id: 21, code: 'M2' },
    ],
    edges: [
      {
        kind: 'fills', fromKind: 'template', fromId: 1, toKind: 'registry', toId: 10, scope: 'all', monthMask: 0, evidenceTotal: 1,
        evidence: [{ source: 'sourceBinding', id: 5, versionId: null, detail: 'Any' }],
      },
      {
        kind: 'templateFormula', fromKind: 'registry', fromId: 10, toKind: 'template', toId: 2, scope: 'month', monthMask: 0, evidenceTotal: 3,
        evidence: [{ source: 'ast', id: 7, versionId: 70, detail: 'T1.CNT' }],
      },
      {
        kind: 'methodology', fromKind: 'registry', fromId: 10, toKind: 'methodology', toId: 20, scope: 'month', monthMask: 0, evidenceTotal: 1,
        evidence: [{ source: 'registryUse', id: 200, versionId: null, detail: 'F1' }],
      },
      { kind: 'writesInto', fromKind: 'methodology', fromId: 20, toKind: 'template', toId: 3, scope: 'all', monthMask: 0, evidence: [], evidenceTotal: 1 },
      { kind: 'methodologyResult', fromKind: 'methodology', fromId: 20, toKind: 'methodology', toId: 21, scope: 'all', monthMask: 0, evidence: [], evidenceTotal: 1 },
      { kind: 'writesInto', fromKind: 'methodology', fromId: 21, toKind: 'template', toId: 4, scope: 'all', monthMask: 0, evidence: [], evidenceTotal: 1 },
    ],
    templateDependencies: [link(2, true, false), link(3, false, true), link(4, false, true)],
    levels: [
      { level: 0, templateIds: [1] },
      { level: 1, templateIds: [4, 3, 2] },
    ],
    cycles: [],
    diagnostics: [],
    ...overrides,
  };
}

describe('B179 L5: buildGraphView', () => {
  it('рівні — як віддав сервер; шаблони рівня за кодом; лічильники', () => {
    const view = buildGraphView(world());

    expect(view.levels.map((l) => l.templates.map((t) => t.code))).toEqual([['HSE400'], ['LAND', 'REPORT', 'TOTAL']]);
    expect(view.templateCount).toBe(4);
    expect(view.linkCount).toBe(3);
  });

  it('джерело наповнює зв’язку 5 довідника COMP зі споживачами за кодом', () => {
    const [source] = buildGraphView(world()).levels[0]?.templates ?? [];

    expect(source?.fills).toEqual([{ bindingId: 5, registryCode: 'COMP', consumerCodes: ['LAND', 'REPORT', 'TOTAL'] }]);
    expect(source?.reads).toEqual([]);
  });

  it('споживач за формулою шаблону: докази й число всіх доказів', () => {
    const land = buildGraphView(world()).levels[1]?.templates.find((t) => t.code === 'LAND');
    const read = land?.reads[0];

    expect(read?.sourceCode).toBe('HSE400');
    expect(read?.registryCode).toBe('COMP');
    expect(read?.formulaEvidence).toHaveLength(1);
    expect(read?.formulaTotal).toBe(3);
    expect(read?.methodologies).toEqual([]);
  });

  it('споживач за методологією: пряма читає довідник, транзитивна — результати прямої', () => {
    const level = buildGraphView(world()).levels[1];
    const report = level?.templates.find((t) => t.code === 'REPORT')?.reads[0];
    const total = level?.templates.find((t) => t.code === 'TOTAL')?.reads[0];

    expect(report?.methodologies).toEqual([{ code: 'M1', direct: true, via: null, formulas: ['F1'] }]);
    expect(total?.methodologies).toEqual([{ code: 'M2', direct: false, via: 'M1', formulas: [] }]);
  });

  it('цикл: коди шаблонів, позначка на шаблонах циклу', () => {
    const view = buildGraphView(world({ cycles: [{ templateIds: [2, 3] }] }));
    const marks = Object.fromEntries((view.levels[1]?.templates ?? []).map((t) => [t.code, t.onCycle]));

    expect(view.cycles).toEqual([{ codes: ['LAND', 'REPORT'] }]);
    expect(marks).toEqual({ LAND: true, REPORT: true, TOTAL: false });
  });

  it('невідомий шаблон у рівні — запасний код #id, без падіння; урізання й зауваження переносяться', () => {
    const view = buildGraphView(
      world({
        levels: [{ level: 0, templateIds: [99] }],
        templateDependencies: [],
        truncated: true,
        diagnostics: [{ key: 'astFallback', detail: 'template version 7' }],
      }),
    );

    expect(view.levels[0]?.templates[0]?.code).toBe('#99');
    expect(view.truncated).toBe(true);
    expect(view.diagnostics).toEqual([{ key: 'astFallback', detail: 'template version 7' }]);
  });

  it('F13: зв’язка джерела без жодного споживача лишається на екрані (перемикачі доступні), навіть коли споживачів у графі немає взагалі', () => {
    const view = buildGraphView(
      world({
        templateDependencies: [],
        levels: [{ level: 0, templateIds: [1, 2, 3, 4] }],
      }),
    );
    const source = view.levels[0]?.templates.find((t) => t.code === 'HSE400');

    expect(source?.fills).toEqual([{ bindingId: 5, registryCode: 'COMP', consumerCodes: [] }]);
    expect(view.linkCount).toBe(0);
  });
});
