import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * P3 живого проходу екрана джерел: `Esc` у модалці поверх шторки з'єднання закривав
 * ОБИДВА діалоги — Mantine вішає `Escape` кожного з них на `window`. Має закриватись
 * лише верхній; наступний `Esc` — уже шторка.
 */
const source = {
  catalog: 'ProdAF',
  code: 'PI-MAIN',
  collectionSchedules: 0,
  endpoint: 'https://pi.example.invalid/piwebapi',
  hasSecret: false,
  id: 7,
  isActive: true,
  maxParallel: 4,
  nameL10n: { en: 'Main PI server' },
  rowVersion: 'AAAAAAAAB9E=',
  secondaryEndpoint: null,
  sourceEntities: 0,
  transport: 'PiWebApi',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function respond(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path.endsWith('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Integration.Manage'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (path.endsWith('/api/v1/data-sources')) return json([source]);
      if (path.endsWith('/api/v1/sources')) return json([]);

      return json(null);
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/admin/sources?panel=PI-MAIN']}>
          <SourcesPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function drawer(): HTMLElement | null {
  return document.querySelector<HTMLElement>('[data-panel="PI-MAIN"] [role="dialog"]');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('DataSourceDrawer: Esc закриває лише верхній діалог', () => {
  it('Esc у підтвердженні видалення закриває підтвердження, шторка лишається; другий Esc — шторку', async () => {
    respond();
    show();

    const panel = await screen.findByRole('dialog');
    fireEvent.click(await within(panel).findByRole('button', { name: /sources\.deleteConnection/ }));

    await waitFor(() => expect(screen.getAllByRole('dialog')).toHaveLength(2));

    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });

    await waitFor(() => expect(screen.getAllByRole('dialog')).toHaveLength(1));
    expect(drawer()).not.toBeNull();

    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });

    await waitFor(() => expect(drawer()).toBeNull());
  });
});
