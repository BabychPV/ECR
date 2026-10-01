import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import type { JSX } from 'react';
import { SourceEventMapsPanel } from '@/features/sources/SourceEventMapsPanel';
import { fetchSourceEventMaps } from '@/features/sources/sourceEventsApi';
import { testTheme } from '@/test/render';

/**
 * Мапінги подій сутності: стани «помилка», «порожньо», «завантаження»
 * (`ФВ-14.22`). Панель отримує ГОТОВИЙ результат запиту від вкладки, тож
 * обгортка нижче робить той самий запит, що й `SourceEventsTab`, а панель
 * перевіряється на справжньому `UseQueryResult`, а не на вигаданому об'єкті.
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(maps: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), 'http://localhost').pathname;

      if (path === '/api/v1/source-event-maps') return maps();

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
        correlationId: 'corr-event-maps',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function Harness(): JSX.Element {
  const maps = useQuery({ queryKey: ['state-test', 'event-maps', 42], queryFn: () => fetchSourceEventMaps(42) });

  return (
    <SourceEventMapsPanel
      maps={maps}
      documents={[]}
      canManage={false}
      sourceEntityId={42}
      onCreate={() => undefined}
      onEdit={() => undefined}
    />
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Harness />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourceEventMapsPanel — стани', () => {
  it('500: показує помилку з кодом, а не «мапінгів немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    show();

    expect(await screen.findByText('ECR-SYS-0500', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦sourceEvents.mapsEmpty⟧')).toBeNull();
    expect(document.querySelector('[data-source-event-maps] .mantine-Loader-root')).toBeNull();
  });

  it('403: відмову видно кодом, а не «мапінгів немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    show();

    expect(await screen.findByText('ECR-AUTH-0403', { exact: false })).toBeTruthy();
    expect(screen.queryByText('⟦sourceEvents.mapsEmpty⟧')).toBeNull();
  });

  it('порожній перелік: «мапінгів немає» і жодної таблиці', async () => {
    serve(() => Promise.resolve(new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })));
    show();

    expect(await screen.findByText('⟦sourceEvents.mapsEmpty⟧')).toBeTruthy();
    expect(document.querySelector('[data-source-event-map-list]')).toBeNull();
  });

  it('у дорозі: видно завантаження, а не «мапінгів немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect(await screen.findByText('⟦sourceEvents.mapsTitle⟧')).toBeTruthy();
    expect(document.querySelector('[data-source-event-maps] .mantine-Loader-root')).toBeTruthy();
    expect(screen.queryByText('⟦sourceEvents.mapsEmpty⟧')).toBeNull();
  });
});
