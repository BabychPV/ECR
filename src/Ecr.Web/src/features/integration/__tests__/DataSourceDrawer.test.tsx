import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * Шухляда з'єднання, вкладка Connection (директива №15 §3 `UI-09`).
 *
 * ⛔ Відкривається АДРЕСОЮ (`?panel=<code>`), а не кліком: стан шухляди живе в
 * адресі, і саме так нею діляться.
 */
function connection(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    catalog: 'ProdAF',
    code: 'PI-MAIN',
    collectionSchedules: 3,
    endpoint: 'https://pi.example.invalid/piwebapi',
    hasSecret: false,
    id: 7,
    isActive: true,
    maxParallel: 4,
    nameL10n: { en: 'Main PI server' },
    secondaryEndpoint: null,
    sourceEntities: 12,
    transport: 'PiWebApi',
    ...overrides,
  };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function respond(source: Record<string, unknown>, permissions: string[]): void {
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
          permissions,
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

/** Підписи пар `KeyValue` у шухляді. */
function labelsIn(drawer: HTMLElement): string[] {
  return Array.from(drawer.querySelectorAll('dt')).map((node) => node.textContent ?? '');
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("DataSourceDrawer: вкладка Connection", () => {
  it('показує поля з\'єднання парами «підпис → значення»', async () => {
    respond(connection(), ['Integration.Manage']);
    show();

    const drawer = await screen.findByRole('dialog');

    expect(within(drawer).getByText('https://pi.example.invalid/piwebapi')).toBeTruthy();
    expect(within(drawer).getByText('ProdAF')).toBeTruthy();
    expect(within(drawer).getByText('PiWebApi')).toBeTruthy();

    // ⛔ `secondaryEndpoint: null` — пари немає зовсім, а не прочерк.
    expect(labelsIn(drawer).some((label) => label.includes('sources.secondaryEndpoint'))).toBe(false);
    expect(labelsIn(drawer).some((label) => label.includes('sources.endpoint'))).toBe(true);
  });

  it('hasSecret: false — жодного рядка про секрет; поля секрету немає ніколи', async () => {
    respond(connection({ hasSecret: false }), ['Integration.Manage']);
    show();

    const drawer = await screen.findByRole('dialog');

    expect(labelsIn(drawer).some((label) => label.includes('sources.hasSecret'))).toBe(false);
    expect(drawer.querySelector('input[type="password"]')).toBeNull();
  });

  it('hasSecret: true — лише ознака, без значення й без поля', async () => {
    respond(connection({ hasSecret: true }), ['Integration.Manage']);
    show();

    const drawer = await screen.findByRole('dialog');

    expect(labelsIn(drawer).some((label) => label.includes('sources.hasSecret'))).toBe(true);
    expect(drawer.querySelector('input')).toBeNull();
  });

  it('Test connection — лише з Integration.Manage', async () => {
    respond(connection(), ['Integration.View']);
    show();

    const drawer = await screen.findByRole('dialog');

    // Шухляда вже намальована — отже, профіль і перелік доїхали.
    expect(within(drawer).getByText('ProdAF')).toBeTruthy();
    expect(drawer.querySelector('[data-test-connection]')).toBeNull();
  });

  it('Test connection є, коли право є', async () => {
    respond(connection(), ['Integration.Manage']);
    show();

    const drawer = await screen.findByRole('dialog');

    expect(await within(drawer).findByRole('button', { name: /sources\.testConnection/ })).toBeTruthy();
  });
});
