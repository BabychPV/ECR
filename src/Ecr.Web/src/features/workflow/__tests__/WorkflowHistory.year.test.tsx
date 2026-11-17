import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { WorkflowHistory, groupEventsByMonth } from '../WorkflowHistory';
import type { WorkflowEvent } from '../api';

/**
 * ✎ LAND-YEAR L7b (п. 8 плану): «More → History» документа «рік» — переходи всіх місяців року (`?year=true`, сервер L6),
 * згруповані за місяцем, найновіший місяць першим. Копія на місяць — один період, без груп, як і раніше.
 *
 * ⛔ Мутаційний доказ (READY): `year` у `useWorkflowHistory` → `false` — червоний «запит з year=true»; показ без
 * `YearHistory` (умова `year &&` → `false`) — червоний «групи за місяцем».
 */
const event = (periodKey: number | null, at: string, sheetCode: string): WorkflowEvent => ({
  action: 'Submit',
  approvedBySubmitter: false,
  at,
  byDisplayName: 'f13-operator',
  fromState: 'Draft',
  periodKey,
  reason: null,
  sheetCode,
  sheetCopyId: null,
  stepOrdinal: null,
  toState: 'Submitted',
});

const Events = [
  event(209910, '2099-11-02T10:00:00Z', 'OCT_A'),
  event(209909, '2099-10-05T10:00:00Z', 'SEP_A'),
  event(209910, '2099-11-01T10:00:00Z', 'OCT_B'),
];

const urls: string[] = [];

function mount(year: boolean | undefined): void {
  urls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));
      return new Response(JSON.stringify(Events), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <WorkflowHistory embedded documentId={1} periodKey={209901} {...(year === undefined ? {} : { year })} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('WorkflowHistory: документ «рік»', () => {
  it('рік: запит з year=true, події групами за місяцем (жовтень, потім вересень), заголовок — назва місяця', async () => {
    mount(true);

    await screen.findByText('SEP_A');
    expect(urls).toEqual(['/api/v1/documents/1/workflow/history?periodKey=209901&year=true']);

    const groups = Array.from(document.querySelectorAll('[data-workflow-history-month]'));
    expect(groups.map((group) => group.getAttribute('data-workflow-history-month'))).toEqual(['209910', '209909']);
    expect(groups[0]?.querySelector('h3')?.textContent).toBe(formatPeriodKey(209910));
    expect(groups[0]?.textContent).toContain('OCT_A');
    expect(groups[0]?.textContent).toContain('OCT_B');
    expect(groups[1]?.textContent).toContain('SEP_A');
    expect(groups[1]?.textContent).not.toContain('OCT_');
  });

  it('копія на місяць: один період без year=true і без груп', async () => {
    mount(undefined);

    await screen.findByText('SEP_A');
    expect(urls).toEqual(['/api/v1/documents/1/workflow/history?periodKey=209901']);
    expect(document.querySelector('[data-workflow-history-month]')).toBeNull();
    expect(screen.getAllByTestId('workflow-history-event')).toHaveLength(3);
  });

  it('групування: найновіший місяць першим, порядок подій усередині збережено, подія без місяця — наприкінці', () => {
    const grouped = groupEventsByMonth([...Events, event(null, '2099-01-01T00:00:00Z', 'OLD')]);
    expect(grouped.map((group) => group.periodKey)).toEqual([209910, 209909, null]);
    expect(grouped[0]?.events.map((e) => e.sheetCode)).toEqual(['OCT_A', 'OCT_B']);
  });
});
