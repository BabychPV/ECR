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

  it('✎ 10.11: виняток «місяць відкрито для документа» замінює стан ПЕРІОДУ, але не стан погодження', () => {
    expect(stripCell('Draft', 'Closed', true)).toBe('reopened');
    expect(stripCell(null, 'Closed', true)).toBe('reopened');
    expect(stripCell('Draft', 'Closed', false)).toBe('locked');
    expect(stripCell('Approved', 'Closed', true)).toBe('approved');
    expect(stripCell('Submitted', 'Closed', true)).toBe('submitted');
    expect(stripCell('Rejected', 'Closed', true)).toBe('returned');
  });
});

describe('DocumentYearStrip: місяць, відкритий для одного документа (✎ 10.11)', () => {
  /**
   * Приймальна: січень закрито, відкрито лише для A — у рядку A січень «Reopened for this document until …» з
   * відкритим замком, у рядку B той самий січень «Locked». Строк — у поясі МАЙДАНЧИКА (Asia/Atyrau, UTC+5), а не браузера.
   * ⛔ Червоний без фіксу: `reopenedPeriods` не читається — січень A «locked».
   */
  const atyrau = { ...calendar, timeZoneId: 'Asia/Atyrau' } as PeriodCalendarDto;
  const strip = (documentId: number, reopened: { periodKey: number; openUntil: string }[] | null) => (
    <DocumentYearStrip
      documentId={documentId}
      businessKey={`DOC-${String(documentId)}`}
      year={2026}
      periodStates={[{ periodKey: 202601, state: 'Draft' }]}
      calendar={atyrau}
      reopenedPeriods={reopened}
    />
  );

  it('A — «reopened» з відкритим замком і строком у поясі майданчика; B — «locked»', () => {
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          {strip(1, [{ periodKey: 202601, openUntil: '2026-11-14T13:00:00Z' }])}
          {strip(2, [])}
        </MemoryRouter>
      </MantineProvider>,
    );

    const [a, b] = screen.getAllByRole('list');
    const janA = within(a!).getAllByRole('link')[0]!;
    const janB = within(b!).getAllByRole('link')[0]!;

    expect(janA.getAttribute('data-cell')).toBe('reopened');
    expect(janA.querySelector('[data-mark="open-lock"]')).not.toBeNull();
    const label = janA.getAttribute('aria-label') ?? '';
    expect(label).toContain('documents.strip.reopenedUntil');
    // 13:00Z у Атирау (UTC+5) — 6:00 PM, а не час браузера.
    expect(label).toMatch(/6:00\s?PM|18:00/);
    expect(janA.getAttribute('title')).toBe(label);

    expect(janB.getAttribute('data-cell')).toBe('locked');
    expect(janB.querySelector('[data-mark="open-lock"]')).toBeNull();
    // Лютий A без винятку — «locked», як і був.
    expect(within(a!).getAllByRole('link')[1]!.getAttribute('data-cell')).toBe('locked');
  });

  /*
   * ✎ AN-169: строк за замовчуванням — північ майданчика (19:00Z = 00:00 12 листопада в Атирау). Підпис — кінець
   * 11-го («Nov 11, 2026, 11:59 PM»), а не двозначне «Nov 12, 2026, 12:00 AM».
   * ⛔ Червоне без фіксу: `siteMomentText` у `reopenedUntilText`.
   */
  it('строк на північ майданчика — кінець попередньої доби, не «12:00 AM»', () => {
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>{strip(1, [{ periodKey: 202601, openUntil: '2026-11-11T19:00:00Z' }])}</MemoryRouter>
      </MantineProvider>,
    );

    const jan = screen.getAllByRole('link')[0]!;
    const label = (jan.getAttribute('aria-label') ?? '').replace(/[  ]/gu, ' ');
    expect(label).toContain('Nov 11, 2026, 11:59 PM');
    expect(label).not.toContain('12:00 AM');
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
    for (const key of ['open', 'late', 'submitted', 'approved', 'locked', 'reopened', 'notOpen', 'returned', 'current']) {
      expect(legend.textContent).toContain(`documents.strip.${key}`);
    }
    // ✎ 10.11: зразок «reopened» у легенді — та сама коробка з відкритим замком.
    expect(legend.querySelector('.ecr-year-strip-box[data-cell="reopened"] [data-mark="open-lock"]')).not.toBeNull();
  });
});
