import { describe, expect, it } from 'vitest';
import { provenanceFrom, provenanceLines, type ColumnMethodology } from '../cellProvenance';

/**
 * ✎ DOCUX (доповнення координатора 26.11): Info інспектора — «Calculated by: <методологія> · step <крок>» для обчисленої
 * комірки, «Read by: <методології>» (з «required») для вхідної. Дані — `ColumnDto.calculatedBy/readBy` зрізу (D-577).
 */
const m = (methodologyCode: string, methodologyName: string, step: string | null = null, required = false): ColumnMethodology => ({
  methodologyCode,
  methodologyName,
  step,
  required,
});

describe('cellProvenance', () => {
  it('обчислена колонка — методологія (назва, без неї — код) і крок; кілька — через «;»', () => {
    const lines = provenanceLines(provenanceFrom({ calculatedBy: [m('ECW_C05', 'Fuel combustion', 'S3'), m('ECW_C06', '')] }));
    expect(lines).toHaveLength(1);
    expect(lines[0]?.label).toContain('inspector.info.calculatedBy');
    expect(lines[0]?.value).toContain('methodology=Fuel combustion');
    expect(lines[0]?.value).toContain('step=S3');
    expect(lines[0]?.value).toMatch(/; ECW_C06$/);
  });

  it('вхідна колонка — усі методології, що її читають; обов’язкова — з позначкою «required»', () => {
    const lines = provenanceLines(provenanceFrom({ readBy: [m('A', 'Boilers', null, true), m('B', '')] }));
    expect(lines[0]?.label).toContain('inspector.info.readBy');
    expect(lines[0]?.value).toContain('inspector.info.readByRequired (methodology=Boilers)');
    expect(lines[0]?.value).toContain(', B');
  });

  it('нічого сказати — рядків немає', () => {
    expect(provenanceLines(provenanceFrom(null))).toEqual([]);
    expect(provenanceLines(provenanceFrom({ calculatedBy: [], readBy: [] }))).toEqual([]);
  });
});
