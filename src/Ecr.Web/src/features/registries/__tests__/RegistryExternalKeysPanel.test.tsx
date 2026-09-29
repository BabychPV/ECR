import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, within, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { RegistryExternalKeysPanel } from '../RegistryExternalKeysPanel';

/**
 * Споживач `GET/POST/DELETE /api/v1/registries/{code}/external-keys`
 * (`ФВ-8.10`, FEATURE-REGISTRY-SYNC S2): панель у формі запису довідника.
 */

const SeededStrings: Record<string, string> = {
  'registries.externalKeys': 'External identifiers',
  'registries.externalKeysEmpty': 'Not linked',
  'registries.externalKeySource': 'Source',
  'registries.externalKeyId': 'Identifier in the source',
  'registries.externalKeyPath': 'Path in the source',
  'registries.externalKeyAdd': 'Link',
  'registries.externalKeyRemove': 'Unlink',
  'registries.externalKeyRemoveTitle': 'Unlink identifier "{externalId}"?',
  'common.cancel': 'Cancel',
};

const link = {
  id: 7,
  registryEntryId: 42,
  entryCode: 'FL01',
  dataSourceId: 3,
  dataSourceCode: 'PI_MAIN',
  externalId: 'GUID-1',
  externalPath: '\\\\AF\\Db\\FL01',
  lastSyncedAt: null,
};

interface Call {
  url: string;
  method: string;
  body: unknown;
}

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Мережа: перелік зв'язків — `items`, з'єднання — одне. */
function mockFetch(items: unknown[]): Call[] {
  const calls: Call[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = (init?.method ?? 'GET').toUpperCase();
      calls.push({ url, method, body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/api/v1/data-sources')) return json([{ id: 3, code: 'PI_MAIN' }]);
      if (method === 'POST') return json(link, 201);
      if (method === 'DELETE') return new Response(null, { status: 204 });
      if (url.includes('/external-keys')) return json({ items, nextCursor: null, totalCount: null });

      return json(null);
    }),
  );

  return calls;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RegistryExternalKeysPanel registryCode="Flares" entryId={42} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Зовнішні ідентифікатори запису довідника', () => {
  it('показує зв\'язки саме цього запису', async () => {
    const calls = mockFetch([link]);
    await loadCatalog('en', 'private');
    show();

    expect(await screen.findByText('GUID-1')).toBeTruthy();
    expect(screen.getByText('PI_MAIN')).toBeTruthy();

    const list = calls.find((c) => c.method === 'GET' && c.url.includes('/external-keys'));
    expect(list?.url).toContain('/api/v1/registries/Flares/external-keys?entryId=42');
  });

  it('прив\'язує: джерело з переліку і ідентифікатор без країв', async () => {
    const calls = mockFetch([]);
    await loadCatalog('en', 'private');
    show();

    expect(await screen.findByText('Not linked')).toBeTruthy();

    const source = screen.getByRole('textbox', { name: 'Source' });
    fireEvent.click(source);
    fireEvent.click(await screen.findByRole('option', { name: 'PI_MAIN' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Identifier in the source' }), {
      target: { value: '  GUID-2 ' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Link' }));

    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    const post = calls.find((c) => c.method === 'POST');
    expect(post?.url).toMatch(/\/api\/v1\/registries\/Flares\/external-keys$/);
    expect(post?.body).toEqual({ entryId: 42, dataSourceId: 3, externalId: 'GUID-2' });
  });

  it('відв\'язує лише після підтвердження', async () => {
    const calls = mockFetch([link]);
    await loadCatalog('en', 'private');
    show();

    fireEvent.click(await screen.findByRole('button', { name: 'Unlink' }));

    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Unlink identifier "GUID-1"?')).toBeTruthy();
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Unlink' }));

    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE')).toBe(true));
    expect(calls.find((c) => c.method === 'DELETE')?.url).toMatch(/\/api\/v1\/registries\/Flares\/external-keys\/7$/);
  });
});
