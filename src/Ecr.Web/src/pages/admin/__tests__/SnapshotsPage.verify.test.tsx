import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * BE-17: дія «Verify» у рядку зрізу. Перевіряється, що саме відповідь
 * сервера — а не збережена сума з переліку — вирішує, що побачить людина, і
 * що при розбіжності показуються ОБИДВІ суми.
 */
const Strings: Record<string, string> = {
  'snapshots.verify': 'Verify',
  'snapshots.verifyMatch': 'unchanged',
  'snapshots.verifyMismatch': 'content changed',
  'snapshots.verifyStored': 'Stored: {hash}',
  'snapshots.verifyActual': 'Actual: {hash}',
};

const snapshot = {
  id: 7,
  builtAt: '2026-01-15T10:00:00Z',
  contentHash: 'AAAA',
  isCurrent: true,
  periodKey: 202601,
  projectId: 42,
  reportVersionId: 1,
  rowCount: 10,
  status: 'Approved',
};

const json = (body: unknown): Response =>
  new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });

function mockFetch(verifyResult: unknown): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);

    if (url.endsWith('/api/v1/reports/snapshots/7/verify') && init?.method === 'POST') {
      return json(verifyResult);
    }

    if (url.includes('/ui-strings/')) {
      return json({ languageCode: 'en', revision: 1, strings: Strings });
    }

    if (url.includes('/api/v1/reports/snapshots')) return json([snapshot]);
    if (url.includes('/api/v1/reports')) return json([]);

    if (url.includes('/api/v1/projects')) {
      return json({ items: [{ id: 42, code: 'KASH_2026' }], nextCursor: null, totalCount: 1 });
    }

    if (url.includes('/api/v1/me')) {
      return json({ denies: [], grants: {}, permissions: [], userId: 1, userName: 'tester' });
    }

    return json(null);
  });

  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

async function show(): Promise<void> {
  await loadCatalog('en', 'private');

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/snapshots']}>
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const SlowEnvTimeout = 400_000;

describe('SnapshotsPage: перевірка незмінності зрізу (BE-17)', () => {
  it(
    'збіг — бейдж «unchanged», сум розбіжності не показано',
    async () => {
      const fetchMock = mockFetch({ matches: true, stored: 'AAAA', actual: 'AAAA' });
      await show();

      fireEvent.click(
        await screen.findByRole('button', { name: 'Verify' }, { timeout: SlowEnvTimeout }),
      );

      expect(await screen.findByText('unchanged')).toBeDefined();
      expect(screen.queryByText(/^Actual:/)).toBeNull();

      const call = fetchMock.mock.calls.find(([url]) => String(url).endsWith('/7/verify'));
      expect((call?.[1] as RequestInit | undefined)?.method).toBe('POST');
    },
    SlowEnvTimeout,
  );

  it(
    'розбіжність — бейдж «content changed» і ОБИДВІ суми цілком',
    async () => {
      mockFetch({ matches: false, stored: 'AAAA', actual: 'BBBB' });
      await show();

      fireEvent.click(
        await screen.findByRole('button', { name: 'Verify' }, { timeout: SlowEnvTimeout }),
      );

      expect(await screen.findByText('content changed')).toBeDefined();
      expect(screen.getByText(/Stored: AAAA/)).toBeDefined();
      expect(screen.getByText(/Actual: BBBB/)).toBeDefined();
      expect(screen.queryByText('unchanged')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
