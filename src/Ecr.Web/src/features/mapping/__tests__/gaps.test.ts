import { describe, it, expect } from 'vitest';
import type { MappedFieldPreview, MappingPreview } from '@/api/types';
import { brokenMaps, hasNoGaps, windowFrom } from '@/features/mapping/api';

/**
 * Визначення розриву з боку мапінгу (`ФВ-13.14`).
 *
 * ⛔ Перевіряється чиста функція, а не екран: саме вона вирішує, що потрапить
 * у перелік розривів. Якби це рішення жило в JSX, воно перевірялося б лише
 * оглядом — тобто не перевірялося б.
 */
function field(overrides: Partial<MappedFieldPreview>): MappedFieldPreview {
  return {
    fieldMapId: 1,
    sourceField: 'Flare_01_CO',
    outcome: 'Materialized',
    targetRowKey: 'Flare_01',
    targetColumnDefId: 100,
    targetColumnCode: 'CO_MASS',
    aggregation: 'Sum',
    sourceUnitCode: 'kg',
    targetUnitCode: 't',
    pointCount: 3,
    foldedValue: 42,
    ...overrides,
  };
}

function preview(overrides: Partial<MappingPreview>): MappingPreview {
  return {
    sourceEntityId: 1,
    code: 'FLARE_01',
    displayName: null,
    fromUtc: '2026-09-01T00:00:00Z',
    toUtc: '2026-09-08T00:00:00Z',
    pointsSeen: 3,
    isTruncated: false,
    fields: [],
    rows: [],
    unmappedSourceFields: [],
    uncoveredColumns: [],
    ...overrides,
  };
}

describe('Розриви мапінгу', () => {
  it('мапінг без жодного рядка джерела — розрив (`NoData`)', () => {
    // ⛔ Це друкарська помилка в шляху AF. Збір «успішний», точок нуль, і в
    // журналі прогонів він виглядає як справний.
    const broken = brokenMaps([
      field({ fieldMapId: 1 }),
      field({ fieldMapId: 2, outcome: 'NoData', pointCount: 0, foldedValue: null }),
    ]);

    expect(broken.map((f) => f.fieldMapId)).toEqual([2]);
  });

  it('мапінг на зниклу колонку — розрив (`TargetMissing`)', () => {
    const broken = brokenMaps([field({ outcome: 'TargetMissing', targetColumnCode: null })]);

    expect(broken).toHaveLength(1);
  });

  it('`RawOnly` розривом НЕ є: точки лишаються сирими навмисно (`D-118`)', () => {
    // ⚠ Найважливіший із чотирьох тестів. Назвати законний стан дефектом —
    // найшвидший спосіб зробити так, щоб перелік розривів перестали читати.
    expect(brokenMaps([field({ outcome: 'RawOnly', targetRowKey: null })])).toHaveLength(0);
  });

  it('«розривів немає» враховує всі три переліки, а не один', () => {
    expect(hasNoGaps(preview({ fields: [field({})] }))).toBe(true);

    expect(
      hasNoGaps(
        preview({
          unmappedSourceFields: [
            { sourcePath: 'Flare_01_NOx', pointCount: 2, lastSeenUtc: '2026-09-02T00:00:00Z' },
          ],
        }),
      ),
    ).toBe(false);

    expect(
      hasNoGaps(
        preview({
          uncoveredColumns: [
            {
              tableDefId: 50,
              columnDefId: 101,
              code: 'CH4_MASS',
              header: 'CH4',
              isRequired: true,
              isUnfillable: true,
            },
          ],
        }),
      ),
    ).toBe(false);
  });
});

describe('Вікно перегляду', () => {
  it('рахується назад від заданого моменту і не залежить від годинника', () => {
    // ⚠ Вікно, яке бралося б із `new Date()` усередині, зробило б тест
    // невідтворюваним, а сторінку — такою, що перезапитує сервер на кожен
    // рендер.
    const window = windowFrom(new Date('2026-09-08T00:00:00Z'));

    expect(window.toUtc).toBe('2026-09-08T00:00:00.000Z');
    expect(window.fromUtc).toBe('2026-09-01T00:00:00.000Z');
  });
});
