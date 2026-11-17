import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { formatPeriodKey } from '@/shared/format';
import { YearMonthReopen } from '../YearMonthReopen';

/**
 * ✎ LAND-YEAR L7a: «Reopen a month for this document…» документа «рік» — вибір із 12 місяців року (відкрити можна лише
 * закритий), далі той самий API (`GET/POST …/period-reopen`), те саме право (рішення сервера `canOpen`/`denial`) і той
 * самий діалог причини; ціль — місяць із вибору.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

const Year = 2099;
const Months = Array.from({ length: 12 }, (_, i) => ({
  periodKey: Year * 100 + i + 1,
  state: i === 9 || i === 8 ? ('Closed' as const) : ('Open' as const),
}));

interface Call {
  readonly url: string;
  readonly method: string;
  readonly body: unknown;
}
const calls: Call[] = [];

function stub(state: Record<string, unknown>): void {
  calls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      calls.push({ url: String(input), method, body: init?.body === undefined ? null : JSON.parse(String(init.body)) });
      const payload =
        method === 'POST'
          ? { id: 9, periodKey: 209910, openUntil: '2099-12-31T19:00:00Z', reason: 'Fix', openedByUserId: 3, openedByName: null, openedAt: '2099-12-01T00:00:00Z' }
          : state;
      return new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

const canOpen = { periodKey: 209910, active: null, canOpen: true, denial: null, defaultOpenUntil: '2099-12-31T19:00:00Z' };

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <YearMonthReopen documentId={5} months={Months} timeZoneId="UTC" />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function openMenu(): Promise<HTMLElement[]> {
  fireEvent.click(screen.getByTestId('year-month-reopen'));
  return waitFor(() => {
    const items = Array.from(document.querySelectorAll<HTMLElement>('[data-year-reopen-month]'));
    if (items.length === 0) throw new Error('menu not open');
    return items;
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('YearMonthReopen', () => {
  it('shows the 12 months of the year; only closed ones can be chosen', async () => {
    stub(canOpen);
    show();

    const items = await openMenu();
    expect(items.map((item) => Number(item.dataset['yearReopenMonth']))).toEqual(Months.map((m) => m.periodKey));
    expect(items[0]?.textContent).toBe(formatPeriodKey(209901));
    const enabled = items.filter((item) => !item.hasAttribute('disabled') && item.getAttribute('data-disabled') !== 'true');
    expect(enabled.map((item) => item.dataset['yearReopenMonth'])).toEqual(['209909', '209910']);
    expect(calls).toEqual([]);
  });

  it('the chosen month goes to the same API: state of that month, then POST with its periodKey and the reason', async () => {
    stub(canOpen);
    show();

    const items = await openMenu();
    fireEvent.click(items.find((item) => item.dataset['yearReopenMonth'] === '209910') as HTMLElement);

    const reason = await screen.findByTestId('document-period-reopen-reason', {}, { timeout: 20_000 });
    expect(calls[0]).toMatchObject({ method: 'GET', url: '/api/v1/documents/5/period-reopen?periodKey=209910' });
    fireEvent.change(reason, { target: { value: 'Fix October' } });
    fireEvent.click(screen.getByTestId('document-period-reopen-confirm'));

    await waitFor(() => expect(calls.some((call) => call.method === 'POST')).toBe(true));
    const post = calls.find((call) => call.method === 'POST');
    expect(post?.url).toBe('/api/v1/documents/5/period-reopen');
    expect(post?.body).toMatchObject({ periodKey: 209910, reason: 'Fix October' });
  });

  it('server refuses (no Period.Reopen in the project): the reason in words, no dialog', async () => {
    stub({ ...canOpen, canOpen: false, denial: 'NoPermission' });
    show();

    const items = await openMenu();
    fireEvent.click(items.find((item) => item.dataset['yearReopenMonth'] === '209909') as HTMLElement);

    expect(await screen.findByTestId('year-month-reopen-denied')).toBeTruthy();
    expect(screen.queryByTestId('document-period-reopen-reason')).toBeNull();
    expect(calls.some((call) => call.method === 'POST')).toBe(false);
  });
});
