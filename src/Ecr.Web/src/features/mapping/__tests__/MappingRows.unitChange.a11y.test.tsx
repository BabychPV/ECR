import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import type { MappingPreview } from '@/api/types';
import { MappingRows } from '@/features/mapping/MappingRows';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Доступність банера «джерело змінило одиницю» (`ФВ-16.9`) в ОБОХ темах
 * (`ФВ-14.16`) — окремо від `MappingRows.paused.a11y.test.tsx`: там немає
 * жодного поля з `pendingSourceUnitChange`, тож axe там ніколи не бачив ні
 * банера, ні посилання на `/admin/units`.
 */
const WithPendingUnitChange: MappingPreview = {
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: null,
  fromUtc: '2026-09-01T00:00:00Z',
  toUtc: '2026-09-08T00:00:00Z',
  pointsSeen: 3,
  isTruncated: false,
  fields: [
    {
      fieldMapId: 1,
      sourceField: 'Flare_01_CO',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 100,
      targetColumnCode: 'CO_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 2,
      foldedValue: null,
      isActive: false,
      pendingSourceUnitChange: {
        actualUnitCode: 'm3',
        actualUnitId: null,
        detectedAt: '2026-09-20T08:00:00Z',
      },
    },
  ],
  rows: [],
  unmappedSourceFields: [],
  uncoveredColumns: [],
};

afterEach(cleanup);

describe('Перегляд мапінгу зі зміною одиниці джерела — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: банер із кнопками, право є', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <MappingRows preview={WithPendingUnitChange} allowed />
      </Shell>,
    );

    expect(container.querySelector('[data-mapping-state="pending-unit-change"]')).not.toBeNull();

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('тема %s: банер без права — без кнопок', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <MappingRows preview={WithPendingUnitChange} allowed={false} />
      </Shell>,
    );

    expect(container.querySelector('[data-mapping-state="pending-unit-change"]')).not.toBeNull();

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
