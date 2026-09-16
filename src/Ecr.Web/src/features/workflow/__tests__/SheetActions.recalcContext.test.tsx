import type { JSX } from 'react';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * Аудит 2026-09-16, §10.6: стан перерахунку «витікав» між перемиканнями
 * аркуша/періоду.
 *
 * ⛔ Компонент не перемонтовується при перемиканні аркуша/періоду (`DocumentPage.tsx`
 * рендерить `<SheetActions>` без `key`), а ефект завершення замикався на
 * ПОТОЧНИХ пропах `documentId`/`periodKey` — не на тих, що були активні при
 * постановці задачі в чергу.
 *
 * **Сценарій:** оператор на періоді 202401 тисне «Перерахувати» і перемикається
 * на 202402 до завершення. Наслідків три, і всі троє тихі:
 *   1. кнопка на 202402 показує «виконується», хоча там нічого не поставлено;
 *   2. тост «перерахунок завершено» з'являється, поки оператор дивиться на
 *      202402 — без жодної згадки, ЯКИЙ період завершився;
 *   3. інвалідується кеш `['document', id, 202402]` замість `202401` — період,
 *      що справді перерахувався, ніколи не отримує оновлення.
 */
const CurrentUser = {
  denies: [],
  grants: {},
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Calculation.Recalculate'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'tester',
};

/** Стан, який віддає `GET /jobs/{id}` — тест міняє його посеред прогону. */
let jobState = 'Running';

/** Періоди, з якими прийшли запити на перерахунок. */
const enqueued: number[] = [];

/** Показані тости. */
const shown: string[] = [];

vi.mock('@/shared/ui/notify', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/shared/ui/notify')>();

  return {
    ...actual,
    showDone: (message: string) => {
      shown.push(message);
    },
  };
});

function mockFetch(): void {
  jobState = 'Running';
  enqueued.length = 0;
  shown.length = 0;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.includes('/api/v1/me')) {
        return new Response(JSON.stringify(CurrentUser), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/recalculate')) {
        const body = JSON.parse(String(init?.body ?? '{}')) as { periodKey?: number };
        enqueued.push(body.periodKey ?? 0);

        return new Response(JSON.stringify({ jobId: 'job-202401' }), {
          status: 202,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (url.includes('/api/v1/jobs/')) {
        return new Response(
          JSON.stringify({
            jobId: 'job-202401',
            state: jobState,
            percent: jobState === 'Running' ? 10 : 100,
            message: null,
            error: null,
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      throw new Error(`неочікуваний запит у тесті: ${url}`);
    }),
  );
}

interface Harness {
  switchPeriod: (periodKey: number) => void;
  invalidated: unknown[][];
}

function show(): Harness {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  // ⚠ Ключі інвалідації читаються зі СПРАВЖНЬОГО `QueryClient` — саме вони й
  // визначають, чиї дані перечитаються.
  const invalidated: unknown[][] = [];
  const original = client.invalidateQueries.bind(client);
  client.invalidateQueries = ((filters?: { queryKey?: unknown[] }) => {
    if (filters?.queryKey !== undefined) invalidated.push(filters.queryKey);

    return original(filters as Parameters<typeof original>[0]);
  }) as typeof client.invalidateQueries;

  function Harnessed({ periodKey }: { periodKey: number }): JSX.Element {
    return (
      <MantineProvider>
        <QueryClientProvider client={client}>
          <SheetActions documentId={1} sheetDefId={2} periodKey={periodKey} state="Draft" />
        </QueryClientProvider>
      </MantineProvider>
    );
  }

  const view = render(<Harnessed periodKey={202401} />);

  // ⛔ Саме `rerender`: зміна періоду в заголовку документа МІНЯЄ ПРОП того
  // самого змонтованого `SheetActions` — новий `render` дефект не відтворив би.
  return {
    switchPeriod: (periodKey: number) => view.rerender(<Harnessed periodKey={periodKey} />),
    invalidated,
  };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetActions: перерахунок належить своєму періоду (§10.6)', () => {
  it('кнопка на іншому періоді не показує «виконується» чужої задачі', async () => {
    mockFetch();
    const { switchPeriod } = show();

    fireEvent.click(await screen.findByRole('button', { name: /recalculate/i }));

    // Задача в дорозі — на СВОЄМУ періоді кнопка справді «виконується».
    await waitFor(() => expect(screen.getByRole('button', { name: /recalcRunning/i })).toBeTruthy());
    expect(enqueued).toEqual([202401]);

    switchPeriod(202402);

    // ⛔ Мутаційний доказ (RED до фіксу): `recalcJobId` не залежав від
    // періоду, тож кнопка на 202402 крутилася на задачі, якої там ніхто не
    // ставив, — і повторний перерахунок цього періоду був недоступний.
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: /recalcRunning/i })).toBeNull(),
    );
    expect(screen.getByRole('button', { name: /recalculate/i })).toBeTruthy();
  });

  it('завершення інвалідує період, який перерахувався, а не той, що на екрані', async () => {
    mockFetch();
    const { switchPeriod, invalidated } = show();

    fireEvent.click(await screen.findByRole('button', { name: /recalculate/i }));
    await waitFor(() => expect(enqueued).toEqual([202401]));

    switchPeriod(202402);
    invalidated.length = 0;

    jobState = 'Succeeded';

    await waitFor(() => expect(shown.some((message) => message.includes('recalcDone'))).toBe(true), {
      timeout: 10_000,
    });

    // ⛔ Мутаційний доказ (RED до фіксу): ефект замикався на ПОТОЧНОМУ
    // `periodKey`, тож інвалідувався `202402` — період, який не перераховували,
    // — а `202401` лишався зі старими числами під написом «перераховано».
    expect(invalidated).toContainEqual(['document', 1, 202401]);
    expect(invalidated).not.toContainEqual(['document', 1, 202402]);
  });

  it('тост завершення називає період, до якого він стосується', async () => {
    mockFetch();
    const { switchPeriod } = show();

    fireEvent.click(await screen.findByRole('button', { name: /recalculate/i }));
    await waitFor(() => expect(enqueued).toEqual([202401]));

    switchPeriod(202402);
    jobState = 'Succeeded';

    await waitFor(() => expect(shown).toHaveLength(2), { timeout: 10_000 });

    // ⚠ Тост «перерахунок завершено» без періоду, показаний над ІНШИМ
    // періодом, стверджує неправду про те, на що дивиться оператор. Нового
    // рядка каталогу тут не заводиться (каталог живе в сіді БД, поза цим
    // пакетом) — період дописується до наявного рядка тим самим `·`, що вже
    // використано в підписах діалогів.
    expect(shown[1]).toContain('202401');
  });
});
