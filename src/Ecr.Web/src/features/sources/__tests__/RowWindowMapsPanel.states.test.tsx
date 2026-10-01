import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RowWindowMapsPanel } from '@/features/sources/RowWindowMapsPanel';
import { testTheme } from '@/test/render';

/**
 * Прив'язки за вікном рядка: стани «помилка», «порожньо», «завантаження»
 * (`ФВ-14.22`). Панель гілкує за `isError`/`isPending`/`isSuccess`, тож
 * відмова чи запит у дорозі не мають давати «прив'язок немає».
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(maps: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/row-window-maps') return maps();

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
        correlationId: 'corr-row-window',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RowWindowMapsPanel sourceEntityId={42} entities={[]} documents={[]} canManage={false} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RowWindowMapsPanel — стани', () => {
  it('500: показує помилку з кодом, а не «прив\'язок немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦rowWindow.empty⟧')).toBeNull();
    expect(document.querySelector('[data-row-window-maps] .mantine-Loader-root')).toBeNull();
  });

  it('403: відмову видно кодом, а не «прив\'язок немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect(await screen.findByText('ECR-AUTH-0403', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦rowWindow.empty⟧')).toBeNull();
  });

  it('порожній перелік: «прив\'язок немає» і жодної таблиці', async () => {
    serve(() => Promise.resolve(new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })));
    show();

    expect(await screen.findByText('⟦rowWindow.empty⟧')).toBeTruthy();
    expect(document.querySelector('[data-row-window-list]')).toBeNull();
  });

  it('у дорозі: видно завантаження, а не «прив\'язок немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect(await screen.findByText('⟦rowWindow.title⟧')).toBeTruthy();
    expect(document.querySelector('[data-row-window-maps] .mantine-Loader-root')).toBeTruthy();
    expect(screen.queryByText('⟦rowWindow.empty⟧')).toBeNull();
  });
});
