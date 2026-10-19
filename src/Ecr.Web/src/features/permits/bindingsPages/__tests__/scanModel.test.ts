import { describe, expect, it } from 'vitest';
import {
  bindingDetailPath,
  buildScanPlan,
  collectOutcome,
  filterRows,
  parseBindingLocation,
  rowStats,
  templateEditorPath,
  type BindingRow,
  type ScanResult,
} from '../scanModel';
import { binding, envelope, template, version } from './bindingsFixture';

/**
 * Мутаційний доказ (вручну, відновлено): без `lastWithStatus(..., 'Draft')` чернетки зникають з плану (падає «чернетка»);
 * `slice(0, limit)` замість `slice(0, Math.max(0, limit))` валить «порція»; `settled += 1` перед `continue` помилки
 * прибрано - «помилка рахується прочитаною» падає; фільтр `matchesStat('rows')` без `Rows` валить показники.
 */
describe('buildScanPlan', () => {
  const templates = [template(2, 'B_TPL'), template(1, 'A_TPL', 'Alpha'), template(3, 'C_TPL')];
  const versions = new Map([
    [1, [version(11, 'Published', '1.0.0.0'), version(12, 'Published', '1.1.0.0'), version(13, 'Draft', '1.2.0.0')]],
    [2, [version(21, 'Deprecated', '0.9.0.0')]],
    [3, [version(31, 'Draft', '0.1.0.0')]],
  ]);

  it('бере останню опубліковану версію й чернетку, шаблони за кодом', () => {
    const plan = buildScanPlan(templates, versions, 10);

    expect(plan.sources.map((s) => [s.templateCode, s.versionId, s.kind, s.version])).toEqual([
      ['A_TPL', 12, 'Published', '1.1.0.0'],
      ['A_TPL', 13, 'Draft', '1.2.0.0'],
      ['C_TPL', 31, 'Draft', '0.1.0.0'],
    ]);
    expect(plan.sources[0]?.templateName).toBe('Alpha');
  });

  it('шаблон лише із застарілою версією не читається, але рахується окремо', () => {
    const plan = buildScanPlan(templates, versions, 10);

    expect(plan.templatesTotal).toBe(3);
    expect(plan.templatesChecked).toBe(3);
    expect(plan.templatesWithoutVersion).toBe(1);
  });

  it('порція: лише перші limit шаблонів за кодом', () => {
    const plan = buildScanPlan(templates, versions, 1);

    expect(plan.templatesChecked).toBe(1);
    expect(plan.templatesTotal).toBe(3);
    expect(new Set(plan.sources.map((s) => s.templateCode))).toEqual(new Set(['A_TPL']));
    expect(buildScanPlan(templates, versions, 0).sources).toEqual([]);
  });
});

describe('collectOutcome', () => {
  const plan = buildScanPlan(
    [template(1, 'A'), template(2, 'B')],
    new Map([
      [1, [version(11, 'Published')]],
      [2, [version(21, 'Published')]],
    ]),
    5,
  );

  it('рядки з прочитаних версій; помилка однієї не ховає решту й не рахується прив’язкою', () => {
    const results = new Map<string, ScanResult>([
      ['11', { status: 'success', data: envelope(binding('B1'), binding('B2')) }],
      ['21', { status: 'error', error: new Error('boom') }],
    ]);

    const outcome = collectOutcome(plan.sources, results);

    expect(outcome.rows.map((r) => r.key)).toEqual(['11:B1', '11:B2']);
    expect(outcome.failed.map((f) => f.source.versionId)).toEqual([21]);
    expect(outcome.settled).toBe(2);
    expect(outcome.done).toBe(true);
  });

  it('ще не прочитане не входить у підсумок і не робить його завершеним', () => {
    const outcome = collectOutcome(plan.sources, new Map([['11', { status: 'success', data: envelope(binding('B1')) }]]));

    expect(outcome.settled).toBe(1);
    expect(outcome.total).toBe(2);
    expect(outcome.done).toBe(false);
  });
});

describe('filterRows і rowStats', () => {
  const plan = buildScanPlan(
    [template(1, 'GEN07', 'General'), template(2, 'OIL')],
    new Map([
      [1, [version(11, 'Published')]],
      [2, [version(21, 'Draft')]],
    ]),
    5,
  );
  const [published, draft] = plan.sources;
  if (published === undefined || draft === undefined) throw new Error('fixture');
  const rows: BindingRow[] = [
    { key: 'a', source: published, binding: binding('B1') },
    { key: 'b', source: published, binding: binding('B2', { mode: 'Formulas', permitTypeCode: 'WastePermit' }) },
    { key: 'c', source: draft, binding: binding('B3', { permitTableCode: 'WasteLimits' }) },
  ];

  it('пошук по тому, що видно в рядку, без регістру', () => {
    expect(filterRows(rows, { needle: 'wastepermit', mode: '', stat: null }).map((r) => r.key)).toEqual(['b']);
    expect(filterRows(rows, { needle: 'general', mode: '', stat: null }).map((r) => r.key)).toEqual(['a', 'b']);
    expect(filterRows(rows, { needle: 'tbl_b3', mode: '', stat: null }).map((r) => r.key)).toEqual(['c']);
    expect(filterRows(rows, { needle: 'немає такого', mode: '', stat: null })).toEqual([]);
  });

  it('режим і показник смуги', () => {
    expect(filterRows(rows, { needle: '', mode: 'Formulas', stat: null }).map((r) => r.key)).toEqual(['b']);
    expect(filterRows(rows, { needle: '', mode: '', stat: 'draft' }).map((r) => r.key)).toEqual(['c']);
    expect(filterRows(rows, { needle: '', mode: '', stat: 'rows' }).map((r) => r.key)).toEqual(['a', 'c']);
  });

  it('показники рахуються по всьому прочитаному', () => {
    expect(rowStats(rows)).toEqual({ all: 3, rows: 2, draft: 1 });
    expect(rowStats([])).toEqual({ all: 0, rows: 0, draft: 0 });
  });
});

describe('адреси', () => {
  it('екран прив’язки: код кодується, версія й шаблон у параметрах', () => {
    const plan = buildScanPlan([template(7, 'T')], new Map([[7, [version(70, 'Published')]]]), 5);
    const source = plan.sources[0];
    if (source === undefined) throw new Error('fixture');

    expect(bindingDetailPath({ source, binding: binding('A B/1') })).toBe(
      '/admin/permits/bindings/A%20B%2F1?template=7&version=70',
    );
    expect(templateEditorPath(7, 70)).toBe('/admin/templates/7/versions/70?tab=permit');
  });

  it('параметри з адреси: лише додатні цілі', () => {
    expect(parseBindingLocation(new URLSearchParams('template=7&version=70'))).toEqual({ templateId: 7, versionId: 70 });
    expect(parseBindingLocation(new URLSearchParams('template=7'))).toBeNull();
    expect(parseBindingLocation(new URLSearchParams('template=0&version=70'))).toBeNull();
    expect(parseBindingLocation(new URLSearchParams('template=7&version=abc'))).toBeNull();
    expect(parseBindingLocation(new URLSearchParams('template=-1&version=1'))).toBeNull();
  });
});
