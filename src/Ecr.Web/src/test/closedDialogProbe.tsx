import type { ReactNode } from 'react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, type RenderResult } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import { keepMountedModalsTheme } from '@/test/keepMountedModals';

/**
 * B145b: спільне риштування тестів «діалог, що закрився, не шле запит удруге».
 *
 * ⚠ Без залежності від часу. Діалог СПРАВЖНІЙ; тема `keepMountedModalsTheme` лишає закритий `Modal` у DOM
 * назавжди (у продукті — ~200 мс анімації закриття), а відповідь на кожен запит запису віддає сам тест
 * (`release`). Клік по кнопці закритого діалогу робиться `fireEvent.click` після звірки `isConnected`.
 *
 * ⛔ Лише для тестів.
 */

/** Відповідь «сервера» на запит запису. `status` без тіла (204) — `body` не задавати. */
export interface HeldResponse {
  readonly status?: number;
  readonly body?: unknown;
}

export interface HeldWrites {
  /** Запити запису, що дійшли до «сервера»: `'POST /api/v1/users'`. */
  readonly sent: string[];
  /** Тіла тих самих запитів (розібраний JSON; `null`, якщо тіла не було чи воно не JSON). */
  readonly bodies: unknown[];
  /** Скільки запитів запису ще чекає відповіді. */
  readonly waiting: () => number;
  /** Відпускає відповідь на НАЙДАВНІШИЙ запит запису, що чекає. */
  readonly release: () => void;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function parse(body: unknown): unknown {
  if (typeof body !== 'string') return null;

  try {
    return JSON.parse(body) as unknown;
  } catch {
    return null;
  }
}

/**
 * Підміняє `fetch`: читання (`GET`) відповідає одразу (`read`, типово `{}`), запис чекає `release()`.
 *
 * ⚠ `write` викликається в мить ВІДПОВІДІ, а не запиту: так тест може змінити стан «сервера» між ними.
 */
export function holdWrites(
  options: {
    readonly read?: (url: string) => unknown;
    readonly write?: (method: string, url: string, index: number) => HeldResponse | undefined;
  } = {},
): HeldWrites {
  const sent: string[] = [];
  const bodies: unknown[] = [];
  const held: (() => void)[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? '';
      const method = String(init?.method ?? 'GET').toUpperCase();

      if (method === 'GET') return json(options.read?.(url) ?? {});

      const index = sent.length;
      sent.push(`${method} ${url}`);
      bodies.push(parse(init?.body));
      await new Promise<void>((resolve) => {
        held.push(resolve);
      });

      const response = options.write?.(method, url, index) ?? {};
      const status = response.status ?? 200;

      return status === 204 ? new Response(null, { status }) : json(response.body ?? {}, status);
    }),
  );

  return {
    sent,
    bodies,
    waiting: () => held.length,
    release: () => {
      held.shift()?.();
    },
  };
}

/** Рендер із темою, що лишає закриті `Modal` у DOM, власним `QueryClient` і маршрутизатором. */
export function renderKeepMounted(ui: ReactNode, route = '/'): RenderResult & { readonly client: QueryClient } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <MantineProvider theme={keepMountedModalsTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );

  return Object.assign(view, { client });
}

/**
 * Кілька тіків циклу подій: незахищена мутація стартує одразу, тож за цей час другий запит уже був би в
 * журналі `sent`.
 */
export async function settle(): Promise<void> {
  await new Promise<void>((resolve) => setImmediate(resolve));
  await new Promise<void>((resolve) => setImmediate(resolve));
}
