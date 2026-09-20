import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnapshotsPage } from '@/pages/admin/SnapshotsPage';
import { testTheme } from '@/test/render';

/**
 * Відмова `GET /api/v1/reports` — не «опублікованих звітів немає».
 *
 * ⛔ Вікно побудови на відмові показувало порожній вибір і підпис
 * `snapshots.noPublished` — твердження про ДАНІ, яких ніхто не прочитав; вікно
 * описів — «описів немає», тобто запрошення завести дублікат.
 */
const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function mockFetch(reportsFail: boolean): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false,
          permissions: ['Report.BuildSnapshot', 'Report.EditDefinition'],
          simulatedForUserId: null, userId: 1, userName: 'tester',
        });
      }
      if (url.includes('/api/v1/reports/snapshots')) return json([]);
      if (url.includes('/api/v1/reports')) {
        return reportsFail
          ? new Response(
              JSON.stringify({ type: 'about:blank', title: 'Internal Server Error', status: 500, errorCode: 'ECR-SYS-0500', correlationId: 'cid-2' }),
              { status: 500, headers: { 'Content-Type': 'application/problem+json' } },
            )
          : json([]);
      }
      if (url.includes('/api/v1/projects')) {
        return json({ items: [{ id: 42, code: 'KASH_2026', status: 'Active' }], nextCursor: null, totalCount: 1 });
      }
      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={['/admin/snapshots?projectId=42']}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SnapshotsPage />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const Slow = 30_000;

// ⚠ Каталог рядків тут не завантажено: `t()` віддає позначений ключ.
const buildButton = (): Promise<HTMLElement> =>
  screen.findByRole('button', { name: /snapshots\.build/ }, { timeout: Slow });

describe('SnapshotsPage: відмова переліку звітів не виглядає як «звітів немає»', () => {
  it('на сторінці — причина з кодом; вікно описів не відкрити; у вікні побудови немає «noPublished»', async () => {
    mockFetch(true);
    show();

    const alert = await screen.findByRole('alert', {}, { timeout: Slow });
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');

    const manage = screen.getByRole('button', { name: /reportDefs\.manage/ }) as HTMLButtonElement;
    expect(manage.disabled).toBe(true);

    await userEvent.click(await buildButton());
    const dialog = await screen.findByRole('dialog', {}, { timeout: Slow });

    expect(within(dialog).getByRole('alert').textContent ?? '').toContain('ECR-SYS-0500');
    expect(within(dialog).queryByText(/snapshots\.noPublished/)).toBeNull();
  }, Slow);

  it('дзеркало: перелік приїхав порожнім — «noPublished» сказано, банера немає', async () => {
    mockFetch(false);
    show();

    await userEvent.click(await buildButton());
    const dialog = await screen.findByRole('dialog', {}, { timeout: Slow });

    expect(await within(dialog).findByText(/snapshots\.noPublished/)).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  }, Slow);
});
