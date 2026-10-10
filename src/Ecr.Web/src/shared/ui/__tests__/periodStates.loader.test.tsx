import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { PeriodsStatStrip } from '@/features/projects/PeriodsOverview';
import { MaxConcurrentCalendars, PeriodStatesLoader, type PeriodStateSummary } from '../periodStates';

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

function renderWithClient(ui: React.ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>{ui}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
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
    renderWithClient(<PeriodStatesLoader projectIds={[1, 2, 3]} onChange={onChange} />);

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
    renderWithClient(<PeriodStatesLoader projectIds={ids} onChange={() => undefined} />);

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
    renderWithClient(<PeriodsStatStrip projects={projects as never} />);

    await drainUntilAllRequested();

    expect(requested.size).toBe(ProjectCount);
    expect(maxInFlight).toBeLessThanOrEqual(MaxConcurrentCalendars);
  });
});
