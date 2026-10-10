import { describe, it, expect, vi, afterEach } from 'vitest';
import { act, render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, focusManager } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import SnapshotRowsModal from '@/features/reports/SnapshotRowsModal';
import { testTheme } from '@/test/render';

/**
 * AN-120 / L1-02: зріз незмінний (D-53), тож повернення у вкладку не
 * перезапитує вже завантажені сторінки. Infinite query з дефолтами застосунку
 * (`staleTime` 30 с, `refetchOnWindowFocus`) на фокусі перечитував КОЖНУ
 * сторінку, а для розкладеного (`R8`) зрізу кожна з них — дорогий запит.
 */
const Strings: Record<string, string> = {
  'snapshots.rowsTitle': 'Snapshot rows',
  'snapshots.rowsMore': 'Show more',
  'snapshots.rowsEmpty': 'No rows',
};

const columns = [
  { code: 'OutputCode', kind: 'text', name: 'OutputCode' },
  { code: 'Value', kind: 'number', name: 'Value' },
];

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });

/** Клієнт із тими самими дефолтами, що й застосунок (`app/queryClient.ts`). */
function appLikeClient(): QueryClient {
  return new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: 30_000, refetchOnWindowFocus: true } },
  });
}

/** Вкладка пішла з фокуса й повернулася через `elapsedMs`. */
async function focusAfter(elapsedMs: number): Promise<void> {
  const later = Date.now() + elapsedMs;
  vi.spyOn(Date, 'now').mockReturnValue(later);

  act(() => {
    focusManager.setFocused(false);
    focusManager.setFocused(true);
  });

  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 50));
  });
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  focusManager.setFocused(undefined);
});

const SlowEnvTimeout = 400_000;

describe('SnapshotRowsModal — фокус вкладки (AN-120 / L1-02)', () => {
  it(
    'дві завантажені сторінки, повернення у вкладку через 31 с — жодного нового запиту рядків',
    async () => {
      const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);

        if (url.includes('/ui-strings/')) {
          return json({ languageCode: 'en', revision: 1, strings: Strings });
        }

        if (url.includes('/api/v1/reports/snapshots/7/rows')) {
          return url.includes('cursor=1')
            ? json({ columns, rows: [{ rowNo: 2, cells: { OutputCode: 'E_NOX', Value: 1 } }], nextCursor: null })
            : json({ columns, rows: [{ rowNo: 1, cells: { OutputCode: 'E_CO2', Value: 2 } }], nextCursor: 1 });
        }

        return json(null);
      });

      vi.stubGlobal('fetch', fetchMock);
      await loadCatalog('en', 'private');

      const rowsRequests = (): number =>
        fetchMock.mock.calls.map(([url]) => String(url)).filter((u) => u.includes('/rows')).length;

      render(
        <MantineProvider theme={testTheme}>
          <QueryClientProvider client={appLikeClient()}>
            <SnapshotRowsModal snapshotId={7} onClose={() => undefined} />
          </QueryClientProvider>
        </MantineProvider>,
      );

      expect(await screen.findByText('E_CO2', undefined, { timeout: SlowEnvTimeout })).toBeDefined();
      fireEvent.click(screen.getByRole('button', { name: 'Show more' }));
      expect(await screen.findByText('E_NOX')).toBeDefined();
      await waitFor(() => expect(rowsRequests()).toBe(2));

      await focusAfter(31_000);

      // ⛔ Мутаційний доказ: прибери `staleTime: Infinity` і
      // `refetchOnWindowFocus: false` у `SnapshotRowsModal.tsx` — тут стане 4
      // (обидві сторінки перезапитано).
      expect(rowsRequests()).toBe(2);
      expect(screen.getByText('E_NOX')).toBeDefined();
    },
    SlowEnvTimeout,
  );
});
