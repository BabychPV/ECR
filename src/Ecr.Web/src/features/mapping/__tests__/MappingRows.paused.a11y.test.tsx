import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import type { MappingPreview } from '@/api/types';
import { MappingGaps } from '@/features/mapping/MappingGaps';
import { MappingRows } from '@/features/mapping/MappingRows';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * Доступність перегляду мапінгу з призупиненим мапінгом (`BE-27`) в ОБОХ
 * темах (`ФВ-14.16`).
 *
 * ⛔ Окремо від `accessibility.part3`: там сторінка сканується без `?entity=`,
 * тож перегляд не запитується взагалі, і позначку паузи axe там не побачив би
 * за жодної фікстури. Тут — самі компоненти з діючим і призупиненим поруч.
 */
const WithPaused: MappingPreview = {
  sourceEntityId: 1,
  code: 'FLARE_01',
  displayName: null,
  fromUtc: '2026-09-01T00:00:00Z',
  toUtc: '2026-09-08T00:00:00Z',
  pointsSeen: 9,
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
      foldedValue: '42.5',
      isActive: true,
    },
    {
      fieldMapId: 2,
      sourceField: 'Flare_01_NOx',
      outcome: 'Materialized',
      targetRowKey: 'Flare_01',
      targetColumnDefId: 101,
      targetColumnCode: 'NOX_MASS',
      aggregation: 'Sum',
      sourceUnitCode: 'kg',
      targetUnitCode: 't',
      pointCount: 7,
      foldedValue: '9.5',
      isActive: false,
    },
  ],
  rows: [],
  unmappedSourceFields: [
    { sourcePath: 'Flare_01_NOx', pointCount: 7, lastSeenUtc: '2026-09-02T02:00:00Z' },
  ],
  uncoveredColumns: [],
};

afterEach(cleanup);

describe('Перегляд мапінгу з паузою — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: діючий і призупинений поруч', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <MappingGaps preview={WithPaused} />
        <MappingRows preview={WithPaused} />
      </Shell>,
    );

    // Позначка справді на екрані — інакше зелений axe нічого б не доводив.
    expect(container.querySelector('[data-mapping-paused="true"]')).not.toBeNull();

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
