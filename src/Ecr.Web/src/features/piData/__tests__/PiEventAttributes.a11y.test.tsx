import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { PiEventAttributes } from '../PiEventAttributes';
import type { PiEventAttributeView } from '../eventsApi';

/**
 * D-465: таблиця атрибутів події PI в обох темах — без блокуючих порушень axe; порівняння — і кольором, і текстом
 * (бейдж + причина словами), таблиця має доступне ім'я.
 */
const Attributes: PiEventAttributeView[] = [
  {
    columnDefId: 2,
    columnCode: 'VOL',
    attribute: 'Volume',
    scope: 'Event',
    piValueNumeric: '3.5',
    piValueText: null,
    piValueTruncated: false,
    piUnit: 'Sm3',
    piChangedAt: '2026-10-01T00:00:00Z',
    expectedValue: '3.5',
    cellValue: '1',
    cellOrigin: 'Integration',
    comparison: 'NotWritten',
    reason: 'SheetLocked',
  },
  {
    columnDefId: null,
    columnCode: null,
    attribute: 'Category',
    scope: 'PrimaryElement',
    piValueNumeric: null,
    piValueText: 'Sweet',
    piValueTruncated: true,
    piUnit: null,
    piChangedAt: null,
    expectedValue: null,
    cellValue: null,
    cellOrigin: null,
    comparison: null,
    reason: null,
  },
];

afterEach(cleanup);

describe('PiEventAttributes — доступність', () => {
  it.each(Themes)('тема %s: axe без порушень, порівняння текстом', async (scheme) => {
    const { container } = render(
      <Shell colorScheme={scheme}>
        <PiEventAttributes attributes={Attributes} caption="Attributes of event MAY" />
      </Shell>,
    );

    expect(container.querySelector('table[aria-label="Attributes of event MAY"]')).not.toBeNull();
    expect(container.querySelector('[data-pi-event-reason="SheetLocked"]')?.textContent).toContain('piEvents.reasonSheetLocked');
    expect(container.textContent).toContain('piEvents.notMapped');

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
