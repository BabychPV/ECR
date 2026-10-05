import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import type { JSX } from 'react';
import { SourceEventMapsPanel } from '@/features/sources/SourceEventMapsPanel';
import { fetchSourceEventMaps, type SourceEventMap } from '@/features/sources/sourceEventsApi';
import { SourceEventsKeys } from '@/features/sources/sourceEventsData';
import { testTheme } from '@/test/render';

/**
 * AN-40 / L9-06: «Пауза» в переліку мапінгів подій — повна заміна з КЕШОВАНОГО рядка. Тепер вона несе версію рядка,
 * і `409` (мапінг змінили після читання переліку) перечитує перелік, щоб наступне натискання пішло з чинною версією.
 *
 * Мутації (лише локально): прибрати `rowVersion` з `toUpdateRequest` — червоніє перша перевірка тіла; прибрати
 * `onError` з `toggle` — перелік не перечитується, червоніє друга половина.
 */
configure({ asyncUtilTimeout: 10_000 });

function map(rowVersion: string): SourceEventMap {
  return {
    id: 7,
    sourceEntityId: 42,
    documentId: 5,
    tableDefId: 10,
    volumeMode: 'None',
    filterAttribute: null,
    filterScope: null,
    filterValue: null,
    isActive: true,
    rowVersion,
    fields: [
      { id: 1, targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
      { id: 2, targetColumnDefId: 2, sourceAttribute: '$end', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
    ],
  };
}

const json = (body: unknown, status = 200, type = 'application/json'): Response =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

function Harness(): JSX.Element {
  const maps = useQuery({ queryKey: SourceEventsKeys.maps(42), queryFn: () => fetchSourceEventMaps(42) });

  return (
    <SourceEventMapsPanel
      maps={maps}
      documents={[]}
      canManage
      sourceEntityId={42}
      onCreate={() => undefined}
      onEdit={() => undefined}
    />
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SourceEventMapsPanel — версія мапінгу (L9-06)', () => {
  it('пауза несе rowVersion рядка; 409 перечитує перелік, і повтор іде з новою версією', async () => {
    let listed = 0;
    const puts: { rowVersion?: unknown }[] = [];

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const path = new URL(String(input), 'http://localhost').pathname;

        if (path === '/api/v1/source-event-maps' && (init?.method ?? 'GET') === 'GET') {
          listed += 1;
          // Перше читання — стара версія; після 409 сервер віддає чинну.
          return json([map(listed === 1 ? '00000000000007D1' : '00000000000007D9')]);
        }

        if (path === '/api/v1/source-event-maps/7' && init?.method === 'PUT') {
          puts.push(JSON.parse(String(init.body)) as { rowVersion?: unknown });

          return puts.length === 1
            ? json(
                {
                  title: 'Conflict',
                  status: 409,
                  errorCode: 'ECR-INT-0409',
                  messageKey: 'err.ECR-INT-0409.eventMapConcurrency',
                  correlationId: 'corr-l9-06',
                  detail: null,
                },
                409,
                'application/problem+json',
              )
            : json({ ...map('00000000000007DA'), isActive: false });
        }

        return json(null);
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Harness />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const toggle = await screen.findByRole('button', { name: /sourceEvents\.mapPause/ });
    fireEvent.click(toggle);

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(puts[0]?.rowVersion).toBe('00000000000007D1');

    // 409 — перелік перечитано (без цього наступне натискання знову пішло б зі старою версією).
    await waitFor(() => expect(listed).toBe(2));

    await waitFor(() =>
      expect((screen.getByRole('button', { name: /sourceEvents\.mapPause/ }) as HTMLButtonElement).disabled).toBe(false),
    );
    fireEvent.click(screen.getByRole('button', { name: /sourceEvents\.mapPause/ }));

    await waitFor(() => expect(puts).toHaveLength(2));
    expect(puts[1]?.rowVersion).toBe('00000000000007D9');
  });
});
