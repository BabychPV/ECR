import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import type { PeriodCalendarDto } from '@/api/types';
import { DocumentYearStrip, DocumentYearStripLegend } from '@/features/documents/DocumentYearStrip';
import { stripCell } from '@/features/documents/yearStripModel';
import { testTheme } from '@/test/render';

/**
 * ✎ g3 (макет 08.11): смуга 12 місяців «справді малюється» — клітинки розрізняють відкритий, поданий,
 * затверджений, заблокований і ще не відкритий місяць, кожна веде на свій місяць, є легенда.
 *
 * ⛔ Корінь дефекту (доказ — перша перевірка): клітинка брала лише стан погодження, а відкритий, заблокований і
 * майбутній місяці там однаково `Draft` — 12 однакових рамок. Мутація «повернути `stripCell` до самого
 * `documentState`» (тобто ігнорувати `periodState`) робить `locked`/`notOpen` нижче `unknown` — червоний.
 */

const period = (sequence: number, state: string, isCurrent = false) => ({
  id: sequence,
  periodKey: 202600 + sequence,
  year: 2026,
  sequence,
  state,
  startsAt: '2026-01-01T00:00:00Z',
  endsAt: '2026-12-31T00:00:00Z',
  graceEndsAt: '2026-12-15T00:00:00Z',
  reopenedUntil: null,
  isCurrent,
});

const calendar = {
  projectId: 1,
  periodKind: 'Monthly',
  timeZoneId: 'UTC',
  currentPeriodMode: 'Auto',
  policy: { id: 1, code: 'STD', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 30, yearGraceOffsetDays: 0 },
  periods: [
    ...[1, 2, 3, 4, 5, 6, 7, 8, 9].map((m) => period(m, 'Closed')),
    period(10, 'Open'),
    period(11, 'Grace', true),
    period(12, 'Scheduled'),
  ],
} as unknown as PeriodCalendarDto;

describe('yearStripModel.stripCell', () => {
  it('стан погодження важить більше; неподаний місяць — за станом періоду', () => {
    expect(stripCell('Approved', 'Closed')).toBe('approved');
    expect(stripCell('PartlySubmitted', 'Open')).toBe('submitted');
    expect(stripCell('Rejected', 'Open')).toBe('returned');
    expect(stripCell('Draft', 'Closed')).toBe('locked');
    expect(stripCell('Draft', 'Scheduled')).toBe('notOpen');
    expect(stripCell(null, 'Open')).toBe('open');
    // ✎ F13 g3: Grace — окремий «Late (flagged)», як на вкладці Periods (доти смуга казала «Open»).
    expect(stripCell(null, 'Grace')).toBe('late');
    expect(stripCell('Draft', 'Grace')).toBe('late');
    expect(stripCell('Draft', null)).toBe('unknown');
  });
});

describe('DocumentYearStrip (g3)', () => {
  it('12 клітинок різних станів, кожна — посилання на свій місяць; поточний місяць позначено', () => {
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <DocumentYearStrip
            documentId={5}
            businessKey="LAND-1"
            year={2026}
            periodStates={[
              { periodKey: 202601, state: 'Approved' },
              { periodKey: 202602, state: 'Draft' },
              { periodKey: 202610, state: 'Submitted' },
              { periodKey: 202611, state: 'Draft' },
              { periodKey: 202612, state: 'Draft' },
            ]}
            calendar={calendar}
          />
        </MemoryRouter>
      </MantineProvider>,
    );

    const links = within(screen.getByRole('list')).getAllByRole('link');
    expect(links).toHaveLength(12);
    expect(links.map((link) => link.getAttribute('data-cell'))).toEqual([
      'approved', 'locked', 'locked', 'locked', 'locked', 'locked', 'locked', 'locked', 'locked',
      'submitted', 'late', 'notOpen',
    ]);
    expect(links[10]!.getAttribute('href')).toBe('/documents/5?periodKey=202611');
    expect(links[10]!.getAttribute('data-current')).toBe('true');
    expect(links[10]!.getAttribute('aria-label')).toContain('documents.strip.late');
    expect(links[10]!.textContent).toBe('!');
    // Стан не лише кольором: читалка чує місяць і стан.
    expect(links[0]!.getAttribute('aria-label')).toContain('documents.strip.approved');
  });

  it('легенда називає всі стани клітинок', () => {
    render(
      <MantineProvider theme={testTheme}>
        <DocumentYearStripLegend />
      </MantineProvider>,
    );

    const legend = screen.getByRole('group');
    for (const key of ['open', 'late', 'submitted', 'approved', 'locked', 'notOpen', 'returned', 'current']) {
      expect(legend.textContent).toContain(`documents.strip.${key}`);
    }
  });
});
