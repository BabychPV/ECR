import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import type { JSX } from 'react';
import { act, cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { summarizePeriodStates } from '@/shared/ui/periodStates';
import { mantineProviderProps } from '@/shared/theme/provider';
import { renderWithMantine } from '@/test/render';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * UI-13: PeriodPicker за макетом — сітка періодів року, стан місяця і
 * «closes in N days · дата» (`docs/design/hybrid`, `kit.js` PeriodPicker,
 * `48-period-picker-open.png`).
 *
 * Що доводиться: (1) кнопка-календар відкриває сітку з 12 місяців, фокус — на
 * обраному; стрілки рухають фокус, Enter обирає ОДНИМ `onChange`, сітка
 * закривається, фокус повертається в поле; (2) Esc закриває й повертає фокус;
 * ↓ у полі відкриває сітку; (3) стан із календарів проєктів: чип «Open»,
 * значки й імена місяців у сітці, підпис «closes in»; проєкти, що
 * розходяться, — «Open in 1 of 2 projects», без чипа; (4) axe без порушень.
 */
beforeAll(async () => {
  // Сітка вантажиться лінивим чанком; прогрів, щоб «є/немає» не залежало від часу.
  await import('@/shared/ui/PeriodMonthGrid');
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const field = (): HTMLInputElement => screen.getByRole<HTMLInputElement>('textbox', { name: '⟦documents.period⟧' });

const DayMs = 24 * 60 * 60 * 1000;

function calendar(projectId: number, periods: { key: number; state: string; closesInDays?: number }[]): unknown {
  return {
    projectId,
    periodKind: 'Monthly',
    currentPeriodMode: 'Auto',
    timeZoneId: 'Asia/Atyrau',
    policy: { code: 'P', graceOffsetDays: 0, hardCloseOffsetDays: 0 },
    periods: periods.map((period, index) => {
      const at = new Date(Date.now() + (period.closesInDays ?? -1) * DayMs).toISOString();
      return {
        id: index + 1,
        periodKey: period.key,
        state: period.state,
        isCurrent: false,
        sequence: period.key % 100,
        year: Math.trunc(period.key / 100),
        startsAt: '2026-01-01T00:00:00+05:00',
        endsAt: at,
        graceEndsAt: at,
        reopenedUntil: null,
      };
    }),
  };
}

function withCalendars(byProject: Record<number, unknown>, node: JSX.Element): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const match = /\/projects\/(\d+)\/periods/.exec(String(input));
      const body = match === null ? null : byProject[Number(match[1])];
      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider {...mantineProviderProps}>
      <QueryClientProvider client={client}>{node}</QueryClientProvider>
    </MantineProvider>,
  );
}

describe('PeriodPicker: сітка періодів (UI-13)', () => {
  it('кнопка-календар відкриває 12 місяців; стрілки й Enter обирають одним onChange; фокус повертається в поле', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithMantine(<PeriodPicker value={202609} onChange={onChange} />);

    await user.click(screen.getByRole('button', { name: '⟦period.choose⟧' }));
    const grid = await screen.findByRole('dialog', { name: '⟦period.choose⟧' });
    const months = within(grid).getAllByRole('button');
    expect(months).toHaveLength(12);
    // Обраний місяць позначений і отримує фокус.
    const current = months.find((month) => month.getAttribute('aria-current') === 'true');
    expect(current?.getAttribute('data-period-key')).toBe('202609');
    expect(document.activeElement).toBe(current);

    await user.keyboard('{ArrowRight}');
    expect(document.activeElement?.getAttribute('data-period-key')).toBe('202610');
    await user.keyboard('{ArrowUp}');
    expect(document.activeElement?.getAttribute('data-period-key')).toBe('202607');
    await user.keyboard('{Enter}');

    expect(onChange.mock.calls).toEqual([[202607]]);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(field());
    expect(field().value).toBe('July 2026');
  });

  it('Esc закриває сітку й повертає фокус у поле; ↓ у полі відкриває сітку', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithMantine(<PeriodPicker value={202609} onChange={onChange} />);

    field().focus();
    await user.keyboard('{ArrowDown}');
    expect(await screen.findByRole('dialog')).toBeTruthy();
    expect(document.activeElement?.getAttribute('data-period-key')).toBe('202609');

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(field());
    expect(onChange).not.toHaveBeenCalled();
  });

  it('стан з календарів: чип «Open», значок і стан у сітці, підпис «closes in N days · дата»', async () => {
    withCalendars(
      {
        1: calendar(1, [
          { key: 202608, state: 'Closed' },
          { key: 202609, state: 'Open', closesInDays: 12 },
        ]),
        2: calendar(2, [
          { key: 202608, state: 'Closed' },
          { key: 202609, state: 'Open', closesInDays: 20 },
        ]),
      },
      <PeriodPicker value={202609} onChange={vi.fn()} projectIds={[1, 2]} />,
    );

    const chip = await screen.findByTestId('period-state');
    expect(chip.textContent).toBe('⟦status.period.Open⟧');
    // ⚠ Найраніше закриття серед проєктів: 12 днів, а не 20.
    const deadline = screen.getByTestId('period-deadline').textContent ?? '';
    expect(deadline.startsWith('⟦status.period.Open⟧ · ⟦period.closesIn.other (count=12)⟧ · ')).toBe(true);

    await userEvent.setup().click(screen.getByRole('button', { name: '⟦period.choose⟧' }));
    const grid = await screen.findByRole('dialog');
    expect(within(grid).getByRole('button', { name: 'September 2026, ⟦status.period.Open⟧' })).toBeTruthy();
    expect(within(grid).getByRole('button', { name: 'August 2026, ⟦status.period.Closed⟧' })).toBeTruthy();
    // Місяця без календаря стан не вигадується.
    expect(within(grid).getByRole('button', { name: 'October 2026' })).toBeTruthy();
  });

  it('проєкти розходяться: без чипа, підпис «Open in 1 of 2 projects»; закритий період — без «closes in»', async () => {
    withCalendars(
      {
        1: calendar(1, [{ key: 202609, state: 'Open', closesInDays: 3 }]),
        2: calendar(2, [{ key: 202609, state: 'Closed' }]),
      },
      <PeriodPicker value={202609} onChange={vi.fn()} projectIds={[1, 2]} />,
    );

    const deadline = await screen.findByTestId('period-deadline');
    expect(deadline.textContent?.startsWith('⟦period.stateInProjects (state=⟦status.period.Open⟧, count=1, total=2)⟧')).toBe(true);
    expect(screen.queryByTestId('period-state')).toBeNull();
  });
});

describe('summarizePeriodStates', () => {
  const at = (days: number): string => new Date(Date.UTC(2026, 8, days)).toISOString();

  it('однаковий стан — uniform; найвідкритіший перемагає; Open закривається на початку пільги', () => {
    const states = summarizePeriodStates([
      [
        { periodKey: 202609, state: 'Open', endsAt: at(30), graceEndsAt: at(15) },
        { periodKey: 202608, state: 'Closed', endsAt: at(1), graceEndsAt: null },
      ],
      [{ periodKey: 202609, state: 'Grace', endsAt: at(20), graceEndsAt: at(10) }],
    ]);

    expect(states.get(202609)).toEqual({ state: 'Open', uniform: false, inState: 1, total: 2, closesAt: at(15) });
    expect(states.get(202608)).toEqual({ state: 'Closed', uniform: true, inState: 1, total: 1, closesAt: null });
  });

  it('невідомий стан пропускається, а не стає «Closed»', () => {
    const states = summarizePeriodStates([[{ periodKey: 202609, state: 'Archived', endsAt: at(1), graceEndsAt: null }]]);
    expect(states.has(202609)).toBe(false);
  });
});

describe('PeriodPicker: axe з відкритою сіткою й станом', { timeout: 30_000 }, () => {
  it.each(['light', 'dark'] as const)('тема %s', async (scheme) => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify(calendar(1, [{ key: 202609, state: 'Grace', closesInDays: 5 }])), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
        <QueryClientProvider client={client}>
          <PeriodPicker value={202609} onChange={vi.fn()} projectIds={[1]} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByTestId('period-state');
    await act(async () => {
      screen.getByRole('button', { name: '⟦period.choose⟧' }).click();
    });
    await waitFor(() => expect(screen.getByRole('dialog')).toBeTruthy());

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
