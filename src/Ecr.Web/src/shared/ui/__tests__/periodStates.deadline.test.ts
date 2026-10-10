import { createElement, type ReactElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { PeriodsStatStrip, periodNote } from '@/features/projects/PeriodsOverview';
import { siteMomentText } from '@/shared/siteMoment';
import {
  MaxConcurrentCalendars,
  PeriodStatesLoader,
  deadlineText,
  summarizePeriodStates,
  type CalendarPeriod,
  type PeriodStateSummary,
} from '@/shared/ui/periodStates';

/**
 * Приймальна RC8 №6, P2-1: дедлайн періоду розходився між екранами. Documents
 * («Open · closes in N days · дата», `PeriodPicker`) брала `graceEndsAt` —
 * НАЧАЛО пільги (`PeriodEnd + GraceOffsetDays`, `PeriodDto`), а Periods —
 * `endsAt`, жорстке закриття (`PeriodEnd + HardCloseOffsetDays`): вересень
 * «Oct 14» проти «Nov 13». Картка UI-13, приймання 4: підпис береться з
 * `endsAt`. Тут обидва екрани дають ту саму дату й те саме число днів.
 *
 * Вхідні — межі проєкту на `Asia/Atyrau` (+05:00) за політикою +14/+44.
 */
const Zone = 'Asia/Atyrau';
const Now = Date.parse('2026-10-07T09:00:00+05:00');

const september: CalendarPeriod = {
  periodKey: 202609,
  state: 'Open',
  graceEndsAt: '2026-10-15T00:00:00+05:00',
  endsAt: '2026-11-14T00:00:00+05:00',
};

const october: CalendarPeriod = {
  periodKey: 202610,
  state: 'Open',
  graceEndsAt: '2026-11-15T00:00:00+05:00',
  endsAt: '2026-12-15T00:00:00+05:00',
};

function documentsDeadline(period: CalendarPeriod): string {
  const summary = summarizePeriodStates([[period]], [Zone]).get(period.periodKey);
  if (summary === undefined) throw new Error('немає зведення');

  return deadlineText(summary, Now) ?? '';
}

function periodsNote(period: CalendarPeriod): string {
  const full = {
    ...period,
    startsAt: '2026-09-01T00:00:00+05:00',
    reopenedUntil: null,
    isCurrent: true,
  };
  const calendar = { timeZoneId: Zone, policy: { openOffsetDays: 0 }, periods: [full] };

  // Так сторінка Periods віддає `siteDate` у `PeriodsOverview` (дата без часу).
  const siteDate = (value: string, zone: string, inclusiveEnd: boolean): string | null =>
    siteMomentText(value, zone, true, inclusiveEnd);

  return periodNote(full as never, calendar as never, siteDate, Now);
}

describe('дедлайн періоду: Documents і Periods показують одне', () => {
  it('вересень: жорстке закриття — endsAt (13 листопада включно), а не початок пільги', () => {
    expect(documentsDeadline(september)).toContain('Nov 13, 2026');
    expect(periodsNote(september)).toContain('Nov 13, 2026');
  });

  it('жовтень: 14 грудня включно на обох екранах', () => {
    expect(documentsDeadline(october)).toContain('Dec 14, 2026');
    expect(periodsNote(october)).toContain('Dec 14, 2026');
  });

  it.each([september, october])('число днів збігається на обох екранах (%#)', (period) => {
    const count = (text: string): string | undefined => /count=(\d+)/.exec(text)?.[1];

    expect(count(documentsDeadline(period))).toBeDefined();
    expect(count(documentsDeadline(period))).toBe(count(periodsNote(period)));
  });

  it('вересень: 37 повних діб до 14.11 00:00 +05 (було 8 до початку пільги)', () => {
    expect(documentsDeadline(september)).toContain('count=37');
  });

  it('Grace: підпис теж за endsAt', () => {
    const grace: CalendarPeriod = { ...september, state: 'Grace' };

    expect(documentsDeadline(grace)).toContain('Nov 13, 2026');
  });

  it('проєкти розходяться: береться найраніше жорстке закриття', () => {
    const earlier: CalendarPeriod = { ...september, endsAt: '2026-11-01T00:00:00+05:00' };
    const summary = summarizePeriodStates([[september], [earlier]], [Zone, Zone]).get(202609);

    expect(summary?.closesAt).toBe(earlier.endsAt);
  });
});

/**
 * N4-06: відмова календаря одного проєкту не ховає зведення станів решти.
 * N4-08: календарі проєктів не летять усі одразу — одночасно не більше `MaxConcurrentCalendars`.
 *
 * ⛔ Мутаційні докази: поверни `calendars.every((c) => c.data !== undefined)` — падає «збій одного»;
 * прибери `enabled: allowed` — падає «не більше межі одночасно» (обидва тести).
 */
const period = {
  periodKey: 202610,
  state: 'Open',
  startsAt: '2026-10-01T00:00:00+05:00',
  endsAt: '2026-12-15T00:00:00+05:00',
  graceEndsAt: '2026-11-15T00:00:00+05:00',
};

function calendarBody(projectId: number) {
  return { projectId, timeZoneId: 'Asia/Atyrau', periods: [period] };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function projectOf(url: string): number {
  return Number(/projects\/(\d+)\/periods/.exec(url)?.[1]);
}

function renderWithClient(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    createElement(
      MantineProvider,
      { theme },
      createElement(QueryClientProvider, { client }, createElement(MemoryRouter, null, ui)),
    ),
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('PeriodStatesLoader: збій одного календаря (N4-06)', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const id = projectOf(String(input));
        return id === 2 ? json({ title: 'Boom', status: 500 }, 500) : json(calendarBody(id));
      }),
    );
  });

  it('зведення будується за календарями, що завантажились', async () => {
    const onChange = vi.fn<(states: ReadonlyMap<number, PeriodStateSummary> | undefined) => void>();
    renderWithClient(createElement(PeriodStatesLoader, { projectIds: [1, 2, 3], onChange }));

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toBeDefined();
      expect(last?.get(202610)?.state).toBe('Open');
      expect(last?.get(202610)?.total).toBe(2);
    });
  });
});

describe('одночасність календарів (N4-08)', () => {
  const ProjectCount = MaxConcurrentCalendars * 2 + 5;
  let inFlight = 0;
  let maxInFlight = 0;
  let requested = new Set<number>();
  let pending: (() => void)[] = [];

  beforeEach(() => {
    inFlight = 0;
    maxInFlight = 0;
    requested = new Set();
    pending = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(
        (input: RequestInfo | URL) =>
          new Promise<Response>((resolve) => {
            const id = projectOf(String(input));
            requested.add(id);
            inFlight += 1;
            maxInFlight = Math.max(maxInFlight, inFlight);
            pending.push(() => {
              inFlight -= 1;
              resolve(json(calendarBody(id)));
            });
          }),
      ),
    );
  });

  /** Відповідає на все, що зараз летить, доки не отримано календарі всіх проєктів. */
  async function drainUntilAllRequested(): Promise<void> {
    for (let round = 0; round < 100 && (requested.size < ProjectCount || pending.length > 0); round += 1) {
      await waitFor(() => expect(pending.length > 0 || requested.size === ProjectCount).toBe(true));
      const batch = pending.splice(0);
      await act(async () => {
        for (const answer of batch) answer();
        await Promise.resolve();
      });
    }
  }

  it('завантажувач станів: не більше межі одночасно, але опитано всі проєкти', async () => {
    const ids = Array.from({ length: ProjectCount }, (_, index) => index + 1);
    renderWithClient(createElement(PeriodStatesLoader, { projectIds: ids, onChange: () => undefined }));

    await drainUntilAllRequested();

    expect(requested.size).toBe(ProjectCount);
    expect(maxInFlight).toBeLessThanOrEqual(MaxConcurrentCalendars);
  });

  it('огляд періодів: не більше межі одночасно', async () => {
    const projects = Array.from({ length: ProjectCount }, (_, index) => ({
      id: index + 1,
      code: `P${String(index + 1)}`,
      status: 'Active',
      periodKind: 'Month',
    }));
    renderWithClient(createElement(PeriodsStatStrip, { projects: projects as never }));

    await drainUntilAllRequested();

    expect(requested.size).toBe(ProjectCount);
    expect(maxInFlight).toBeLessThanOrEqual(MaxConcurrentCalendars);
  });
});
