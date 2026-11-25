import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, configure, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { testTheme } from '@/test/render';
import type { GasStreamDetail, GasVersion } from '../api';
import StreamDetailBody from '../StreamDetailBody';

/**
 * Шторка потоку (D-NEW): шкала версій «з місяця — по місяць», видалення версії з підтвердженням (діалог каже, що попередня
 * версія продовжиться), замкнена версія — лише перегляд.
 *
 * ⛔ Доказ: прибрати ``setDeleting`` у кнопці «Delete» рядка — «видалення версії…» червоний (діалогу немає).
 */
configure({ asyncUtilTimeout: 10_000 });

const version = (id: number, from: string, to: string | null, locks: GasVersion['locks'] = []): GasVersion => ({
  id,
  validFrom: from,
  validTo: to,
  temperatureC: '20',
  densityOp: null,
  mwHysys: null,
  z: null,
  ruleParse: null,
  legacyTypeId: null,
  legacyCompositionId: null,
  flertAliases: null,
  sum: '100',
  noComposition: false,
  components: [{ code: 'Methane', molPct: '100' }],
  locks,
});

const stream = (versions: GasVersion[]): GasStreamDetail => ({
  id: 11,
  streamId: 317,
  number: '416',
  name: 'Flare 416',
  isOnshore: true,
  isSour: false,
  isHmb: false,
  hmbDoc: null,
  group: 1,
  hidden: false,
  canEdit: true,
  cases: [{ name: 'SOR', versions }],
});

beforeAll(async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ languageCode: 'en', revision: 1, strings: Catalog }))));
  await loadCatalog('en', 'private');
  vi.unstubAllGlobals();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function show(detail: GasStreamDetail, onChanged = vi.fn()): ReturnType<typeof vi.fn> {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient()}>
        <StreamDetailBody stream={detail} components={[]} onChanged={onChanged} />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return onChanged;
}

describe('D-NEW: шторка потоку', () => {
  it('видалення версії: діалог каже, що попередня продовжиться, і шле DELETE', async () => {
    const calls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
        calls.push(`${init?.method ?? 'GET'} ${String(url)}`);
        return new Response(JSON.stringify({ stream: null, queuedYears: [], failedYears: [] }), {
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );
    const onChanged = show(stream([version(1, '2022-01-01', '2026-03-01'), version(2, '2026-03-01', null)]));

    const row = screen.getAllByTestId('gas-version-row').find((r) => r.getAttribute('data-from') === '2026-03-01')!;
    fireEvent.click(within(row).getByRole('button', { name: Catalog['gasComposition.deleteVersion']! }));

    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('January 2022');
    fireEvent.click(within(dialog).getByRole('button', { name: Catalog['common.delete']! }));

    await waitFor(() => expect(onChanged).toHaveBeenCalled());
    expect(calls).toContain('DELETE /api/v1/gas-composition/versions/2');
  });

  it('замкнена версія: позначка з періодами, «Open» замість «Edit», без «Delete»', () => {
    show(stream([version(1, '2022-01-01', null, [{ period: '2025-03', reason: 'approved' }])]));

    const row = screen.getByTestId('gas-version-row');
    expect(within(row).getByTestId('gas-version-lock').textContent).toContain('2025-03');
    expect(within(row).getByRole('button', { name: Catalog['gasComposition.view']! })).toBeTruthy();
    expect(within(row).queryByRole('button', { name: Catalog['gasComposition.deleteVersion']! })).toBeNull();
  });
});
