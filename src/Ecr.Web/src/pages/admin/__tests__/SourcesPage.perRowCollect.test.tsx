import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * Аудит 2026-09-16, §10.8: ОДИН спільний `useMutation` на весь перелік рядків
 * керував станом «завантаження» для всіх рядків одночасно.
 *
 * ⛔ `loading={collect.isPending}` стоїть у КОЖНОМУ рядку таблиці, а
 * `collect` — одна мутація на всю сторінку. Тож збір ОДНОГО джерела показував
 * спінер на всіх кнопках «Collect» і — через `disabled: disabled || loading`
 * (`Button.mjs` Mantine) — блокував збір решти джерел, хоча жодної технічної
 * причини серіалізувати їх немає: кожен збір — окрема фонова задача з власним
 * `jobId`.
 *
 * ⚠ Запит навмисно лишається в дорозі, доки тест його не завершить: саме в
 * цьому вікні й видно, чиї кнопки крутяться.
 */
const sources = [
  {
    id: 1,
    code: 'FLD-1',
    displayName: 'Field weather feed',
    entityPath: null,
    isActive: true,
    lastRun: null,
    oldestGap: null,
    transport: 'Rest',
  },
  {
    id: 2,
    code: 'FLD-2',
    displayName: 'Stack analyzer',
    entityPath: null,
    isActive: true,
    lastRun: null,
    oldestGap: null,
    transport: 'Rest',
  },
];

let settleCollect: (() => void) | null = null;

function mockFetch(): void {
  settleCollect = null;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/collect')) {
        return new Promise<Response>((resolve) => {
          settleCollect = () =>
            resolve(
              new Response(JSON.stringify({ jobId: 'job-1' }), {
                status: 202,
                headers: { 'Content-Type': 'application/json' },
              }),
            );
        });
      }

      if (url.includes('/api/v1/me')) {
        return Promise.resolve(
          new Response(
            JSON.stringify({
              denies: [],
              grants: {},
              isSimulation: false,
              language: 'en',
              mustChangePassword: false,
              permissions: ['Integration.Manage'],
              simulatedForUserId: null,
              userId: 1,
              userName: 'tester',
            }),
            { status: 200, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }

      if (url.includes('/api/v1/sources')) {
        return Promise.resolve(
          new Response(JSON.stringify(sources), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      return Promise.resolve(new Response(JSON.stringify(null), { status: 200 }));
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <SourcesPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Кнопка «Collect» у рядку названого джерела. */
function collectButtonOf(displayName: string): HTMLButtonElement {
  const row = screen.getByText(displayName).closest('tr');
  expect(row, `рядок джерела «${displayName}»`).not.toBeNull();

  const button = row?.querySelector('button');
  expect(button, `кнопка збору в рядку «${displayName}»`).not.toBeNull();

  return button as HTMLButtonElement;
}

afterEach(() => {
  vi.unstubAllGlobals();
  settleCollect = null;
});

describe('SourcesPage: «завантаження» належить рядку, а не сторінці (§10.8)', () => {
  it('збір одного джерела не блокує збір сусіднього', async () => {
    mockFetch();
    show();

    await screen.findByText('Field weather feed');

    fireEvent.click(collectButtonOf('Field weather feed'));

    // Своя кнопка справді крутиться і заблокована.
    await waitFor(() => expect(collectButtonOf('Field weather feed').disabled).toBe(true));

    // ⛔ Мутаційний доказ (RED до фіксу): `loading={collect.isPending}` стояв у
    // КОЖНОМУ рядку, тож кнопка сусіднього джерела теж крутилася й була
    // заблокована — збір другого джерела ставав недоступним без жодної
    // причини.
    expect(collectButtonOf('Stack analyzer').disabled).toBe(false);
    expect(collectButtonOf('Stack analyzer').getAttribute('data-loading')).toBeNull();

    settleCollect?.();

    await waitFor(() => expect(collectButtonOf('Field weather feed').disabled).toBe(false));
  });
});
