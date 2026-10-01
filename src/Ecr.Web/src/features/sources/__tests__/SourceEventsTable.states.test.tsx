import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { SourceEventsTable } from '@/features/sources/SourceEventsTable';
import { testTheme } from '@/test/render';

/**
 * Таблиця подій сутності: стани «помилка», «порожньо», «завантаження»
 * (`ФВ-14.22`, `ФВ-14.25`).
 *
 * ⛔ Дефект, який закрив тест «у дорозі»: поки перша сторінка подій летіла,
 * під фільтрами не було НІЧОГО — ні таблиці, ні «подій немає», ні ознаки
 * завантаження. Порожнє місце під фільтрами читалося як «подій немає», а на
 * повільному PI Web API запит триває секунди.
 */
configure({ asyncUtilTimeout: 10_000 });

const Events = '/api/v1/sources/42/source-events';

function serve(events: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === Events) return events();

      // Решта (каталог рядків тощо) таблиці не потрібна.
      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-events',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <SourceEventsTable sourceEntityId={42} maps={[]} documents={[]} canManage={false} onCreateMap={() => undefined} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourceEventsTable — стани', () => {
  it('500: показує помилку з кодом, а не «подій немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦sourceEvents.empty⟧')).toBeNull();
    expect(document.querySelector('[data-source-events-table]')).toBeNull();
  });

  it('403: відмову видно кодом, а не «подій немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect(await screen.findByText('ECR-AUTH-0403', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦sourceEvents.empty⟧')).toBeNull();
  });

  it('порожня сторінка: «подій немає» і жодної таблиці', async () => {
    serve(() =>
      Promise.resolve(
        new Response(JSON.stringify({ items: [], nextCursor: null, totalCount: 0 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
      ),
    );
    show();

    expect(await screen.findByText('⟦sourceEvents.empty⟧')).toBeTruthy();
    expect(document.querySelector('[data-source-events-table]')).toBeNull();
  });

  it('у дорозі: видно завантаження, а не порожнє місце й не «подій немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    // Фільтри вже намальовано — отже рендер відбувся, а запит подій висить.
    expect(await screen.findByText('⟦sourceEvents.title⟧')).toBeTruthy();
    expect(document.querySelector('[data-source-events] .mantine-Loader-root')).toBeTruthy();
    expect(screen.queryByText('⟦sourceEvents.empty⟧')).toBeNull();
  });
});
