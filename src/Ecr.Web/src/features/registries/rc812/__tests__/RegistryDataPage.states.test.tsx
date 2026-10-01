import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { RegistryDataPage } from '../RegistryDataPage';
import { definition, registries, storedRows } from './fixtures';

/**
 * Редактор даних довідника (`ФВ-8.12`): стани «помилка», «порожньо»,
 * «завантаження» (`ФВ-14.22`) — і для опису довідника, і для записів.
 */
configure({ asyncUtilTimeout: 10_000 });

const Empty = '⟦registries.data.emptyTitle⟧';

const json = (body: unknown, status = 200): Response =>
  new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(json({ title: 'Error', status, errorCode, correlationId: 'corr-rows', detail: null }, status));

interface Replies {
  readonly definition?: () => Promise<Response>;
  readonly rows?: () => Promise<Response>;
}

/** Запити до опису довідника — для перевірки «повторити». */
let definitionCalls = 0;

function serve(replies: Replies): void {
  definitionCalls = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Registry.View', 'Registry.EditData'],
          simulatedForUserId: null,
          userId: 1,
          userName: 'tester',
        });
      }
      if (url.includes('/definition')) {
        definitionCalls += 1;
        return replies.definition?.() ?? json(definition);
      }
      if (url.includes('/api/v1/registries/STREAM_CASE/rows')) {
        return replies.rows?.() ?? json({ items: storedRows, nextCursor: null, totalCount: storedRows.length });
      }
      if (url.includes('/rows')) return json({ items: [], nextCursor: null, totalCount: 0 });
      if (url.endsWith('/api/v1/registries')) return json(registries);
      if (url.includes('/api/v1/units')) return json([]);

      return json(null);
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={['/admin/registries/STREAM_CASE/entries']}>
          <Routes>
            <Route path="/admin/registries/:code/entries" element={<RegistryDataPage />} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RegistryDataPage — стани', () => {
  it('500 записів: помилка з кодом, а не «записів немає»', async () => {
    serve({ rows: () => problem(500, 'ECR-SYS-0500') });
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    expect(screen.queryByText(Empty)).toBeNull();
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('403 записів: «немає права», а не «записів немає»', async () => {
    serve({ rows: () => problem(403, 'ECR-AUTH-0403') });
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText(Empty)).toBeNull();
  });

  it('500 опису довідника: помилка, а не вічне завантаження чи «записів немає»', async () => {
    serve({ definition: () => problem(500, 'ECR-SYS-0500') });
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByText(Empty)).toBeNull();
  });

  it('«повторити» після відмови опису довідника справді повторює опис і відкриває сітку', async () => {
    let fail = true;
    serve({ definition: () => (fail ? problem(500, 'ECR-SYS-0500') : Promise.resolve(json(definition))) });
    show();

    await screen.findByRole('alert');
    const before = definitionCalls;
    fail = false;
    fireEvent.click(screen.getByRole('button', { name: '⟦common.retry⟧' }));

    expect(await screen.findByRole('grid')).toBeTruthy();
    expect(definitionCalls).toBeGreaterThan(before);
  });

  it('порожньо: «записів немає» і жодної сітки', async () => {
    serve({ rows: () => Promise.resolve(json({ items: [], nextCursor: null, totalCount: 0 })) });
    show();

    expect(await screen.findByText(Empty)).toBeTruthy();
    expect(screen.queryByRole('grid')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('записи в дорозі: «завантаження», а не «записів немає»', async () => {
    serve({ rows: () => new Promise<Response>(() => undefined) });
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText(Empty)).toBeNull();
    expect(screen.queryByRole('grid')).toBeNull();
  });
});
