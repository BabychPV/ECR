import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { CompositionEditorPage } from '../CompositionEditorPage';
import { mockServer } from './compositionServer';

/**
 * Редактор master-detail (`ФВ-8.16`): стани «помилка», «немає права»,
 * «завантаження» опису довідника й переліку довідників (`ФВ-14.22`).
 *
 * ⚠ «Порожньо» (довідник без частин — `noChain`) уже покрито в
 * `CompositionEditorPage.test.tsx` — тут лише перевіряється, що відмова чи
 * запит у дорозі НЕ виглядають як «частин немає».
 */
configure({ asyncUtilTimeout: 10_000 });

const NoParts = '[data-rc816-state="no-parts"]';

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(JSON.stringify({ title: 'Error', status, errorCode, correlationId: 'c', detail: null }), {
      status,
      headers: { 'Content-Type': 'application/problem+json' },
    }),
  );

/** Мережа стенда `compositionServer`, з підміною окремих адрес. */
function serve(override: (path: string) => Promise<Response> | null): void {
  mockServer(['Registry.View', 'Registry.EditData']);
  const base = globalThis.fetch;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = new URL(String(input), 'http://localhost').pathname;
      return override(path) ?? base(input, init);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/registries/STREAM_CASE/composition']}>
          <Routes>
            <Route path="/admin/registries/:code/composition" element={<CompositionEditorPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CompositionEditorPage — стани', () => {
  it('500 опису довідника: помилка з кодом, редактора й «частин немає» немає', async () => {
    serve((path) => (path.endsWith('/STREAM_CASE/definition') ? problem(500, 'ECR-SYS-0500') : null));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    expect(document.querySelector(NoParts)).toBeNull();
    expect(document.querySelector('[data-rc816-panel]')).toBeNull();
  });

  it('403 опису довідника: «немає права» з кодом', async () => {
    serve((path) => (path.endsWith('/STREAM_CASE/definition') ? problem(403, 'ECR-AUTH-0403') : null));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(document.querySelector(NoParts)).toBeNull();
  });

  it('опис у дорозі: «завантаження», а не порожній редактор', async () => {
    serve((path) => (path.endsWith('/STREAM_CASE/definition') ? new Promise<Response>(() => undefined) : null));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(document.querySelector(NoParts)).toBeNull();
    expect(document.querySelector('[data-rc816-panel]')).toBeNull();
  });

  it('500 переліку довідників: помилка з кодом, а не «частин немає»', async () => {
    serve((path) => (path.endsWith('/api/v1/registries') ? problem(500, 'ECR-SYS-0500') : null));
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    expect(document.querySelector(NoParts)).toBeNull();
  });

  it('перелік довідників у дорозі: «частин немає» не з\'являється передчасно', async () => {
    serve((path) => (path.endsWith('/api/v1/registries') ? new Promise<Response>(() => undefined) : null));
    show();

    // Опис уже приїхав — заголовок довідника на місці.
    await waitFor(() => expect(screen.getAllByText('STREAM_CASE').length).toBeGreaterThan(0));
    expect(document.querySelector(NoParts)).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
