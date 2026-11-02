import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { DocumentTableDto } from '@/api/types';
import { mantineProviderProps } from '@/shared/theme/provider';
import { Catalog, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { useSheetLiveRefresh } from '../SheetLiveRefresh';

/**
 * LIVE-REFRESH B2/B3: axe without blocking violations in both themes — the toolbar "Refresh" and the polite banner
 * "Updated by …" with its Refresh. Texts are the real catalog (`⟦key⟧` fails). ⚠ jsdom has no layout: contrast of
 * the real colours is checked by the e2e spec `liveRefresh.spec.ts` (axe in a real browser, light and dark).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

const Tables: DocumentTableDto[] = [
  {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 5,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: 'T1',
    tableDefId: 11,
    tableInstanceId: 11,
    tableNameL10n: { values: { en: 'T1' } },
    tableOrdinal: 1,
  },
];

let changed = true;

function install(): void {
  let calls = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(() => {
      calls += 1;
      const body = {
        marker: `m${String(calls)}`,
        changed: changed && calls > 1,
        lastChangedByUserId: 3,
        lastChangedBy: 'Aigerim Sadykova',
        lastChangedAt: '2026-10-30T09:05:00Z',
        changedTables: changed && calls > 1 ? [{ tableCode: 'T1', tableInstanceId: 11 }] : [],
      };

      return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    }),
  );
}

function Probe(): JSX.Element {
  const live = useSheetLiveRefresh({ documentId: 7, periodKey: 202610, tables: Tables });

  return (
    <main>
      {live.banner}
      {live.button}
    </main>
  );
}

function mount(scheme: 'light' | 'dark'): HTMLElement {
  const { container } = render(
    <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Probe />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return container;
}

async function expectClean(root: HTMLElement): Promise<void> {
  expect(root.textContent).not.toContain('⟦');
  const violations = await findViolations(root);

  expect(violations, describeViolations(violations)).toHaveLength(0);
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('Live refresh — axe', () => {
  it.each(Themes)('%s: toolbar Refresh and the banner about another user', async (scheme) => {
    changed = true;
    install();
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const container = mount(scheme);
    await expectClean(container);

    await vi.advanceTimersByTimeAsync(30_100);
    await screen.findByTestId('sheet-live-banner');
    await expectClean(container);
  });
});
