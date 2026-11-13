import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { DocumentHeaderPanel } from '@/features/documents/DocumentHeaderPanel';
import { RecalculationPollMs, isFollowedJob } from '@/features/grid/useCellPatch';
import { testTheme } from '@/test/render';

/**
 * B35 W-2 (аудит 2026-11): правка шапки ставить перерахунок кожного відкритого періоду (`HDR.*` читають формули),
 * а панель доти лише клала відповідь у кеш (`setQueryData`) — обчислені комірки лишалися старими до F9.
 *
 * Тепер відповідь `PATCH` несе `recalculationJobs`, і панель веде КОЖНУ задачу тим самим фоновим слідкувачем, що й
 * правка комірки (`followRecalculation`): по завершенні зрізи записаних періодів перечитуються; рядок статусу —
 * ключі `grid.recalculat…`, як у сітки.
 *
 * ⚠ Сервер — `fetch`-стаб; стан задач перемикає тест. Слідкувачі й «враховані задачі» живуть на рівні модуля,
 * тож кожен тест має власні `jobId`.
 */

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: vi.fn(),
}));

const DocumentId = 35;

interface Job {
  jobId: string;
  periodKey: number;
}

const jobStates = new Map<string, { state: string; writtenCount?: number; writtenPeriods?: number[] }>();
const jobRequests: string[] = [];

function field(value: string): Record<string, unknown> {
  return { code: 'A', dataType: 'String', headerFieldDefId: 1, isRequired: false, label: { values: { en: 'Field A' } }, value };
}

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(jobs: readonly Job[]): void {
  jobRequests.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');

      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`) && method === 'GET') {
        return json({ fields: [field('1')], version: 'V1' });
      }
      if (url.endsWith(`/api/v1/documents/${String(DocumentId)}/header`) && method === 'PATCH') {
        return json({ fields: [field('2')], version: 'V2', recalculationJobs: jobs });
      }
      if (url.includes('/api/v1/jobs/') && method === 'GET') {
        const id = decodeURIComponent(url.split('/api/v1/jobs/')[1] ?? '');
        jobRequests.push(id);
        return json(jobStates.get(id) ?? { state: 'Running' });
      }
      if (url.endsWith('/api/v1/me') && method === 'GET') {
        return json({ permissions: [] });
      }

      throw new Error(`неочікуваний запит у тесті: ${method} ${url}`);
    }),
  );
}

/** Зрізи трьох періодів документа в кеші — так, ніби їх показували сітки. */
const slices = {
  jan: queryKeys.slices.one(3501, 202601),
  feb: queryKeys.slices.one(3502, 202602),
  mar: queryKeys.slices.one(3503, 202603),
};

function show(jobs: readonly Job[], periodKey?: number): QueryClient {
  mockServer(jobs);
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  for (const key of Object.values(slices)) client.setQueryData(key, { rows: [] });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <DocumentHeaderPanel documentId={DocumentId} canEdit periodKey={periodKey} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

const stale = (client: QueryClient, key: readonly unknown[]): boolean =>
  client.getQueryState(key)?.isInvalidated === true;

/** Чи потрапив зріз у виклик `invalidateQueries`, що ПЕРЕЗАПИТУЄ (без `refetchType: 'none'`). */
function refetched(
  client: QueryClient,
  invalidate: { mock: { calls: Parameters<QueryClient['invalidateQueries']>[] } },
  key: readonly unknown[],
): boolean {
  const query = client.getQueryCache().find({ queryKey: key, exact: true });
  if (query === undefined) return false;

  return invalidate.mock.calls.some(([filters]) => {
    const predicate = filters?.predicate;
    return filters?.refetchType !== 'none' && typeof predicate === 'function' && predicate(query);
  });
}

async function editAndSave(): Promise<void> {
  const input = (await screen.findByLabelText('Field A')) as HTMLInputElement;
  fireEvent.change(input, { target: { value: '2' } });
  fireEvent.click(screen.getByRole('button', { name: '⟦common.save⟧' }));
}

function recalcStatus(): string | null {
  return document.querySelector('[data-recalc-status]')?.getAttribute('data-recalc-status') ?? null;
}

/**
 * B91: слідкувачі (`activeFollowers`) — стан МОДУЛЯ з ключем `документ:період`. Тест, що лишив задачу `Running`
 * (тест «статус… періоду сторінки»), лишає й живого слідкувача з ВЛАСНИМ `QueryClient`; наступний тест із тим самим
 * документом і періодом лише перемикав його на свою задачу, і зрізи перечитувались у чужому клієнті (червоне під
 * `--sequence.shuffle`, сіди 27060 і 3). Тому після кожного тесту всі опитані задачі завершуються, і чекаємо, доки
 * слідкувачі відпустять ключі.
 */
async function drainFollowers(): Promise<void> {
  const ids = [...new Set(jobRequests)];
  if (ids.length === 0) return;
  for (const id of ids) jobStates.set(id, { state: 'Succeeded', writtenCount: 0 });
  await waitFor(async () => {
    if (vi.isFakeTimers()) {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(RecalculationPollMs + 100);
      });
    }
    expect(ids.filter((id) => isFollowedJob(id))).toEqual([]);
  });
}

afterEach(async () => {
  await drainFollowers();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  jobStates.clear();
});

describe('B35 W-2: шапка стежить за перерахунком, який поставила', () => {
  it('правка без поставлених задач — жодного запиту стану й жодного статусу', async () => {
    const client = show([]);

    await editAndSave();

    await waitFor(() => expect((screen.getByLabelText('Field A') as HTMLInputElement).value).toBe('2'));
    expect(jobRequests).toEqual([]);
    expect(recalcStatus()).toBeNull();
    expect(stale(client, slices.jan)).toBe(false);
  });

  it('кожна задача річного документа відстежується; після завершення перечитуються зрізи записаних періодів', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const jan = 'IRecalculationJob#b35-a-202601';
    const feb = 'IRecalculationJob#b35-a-202602';
    const client = show([
      { jobId: jan, periodKey: 202601 },
      { jobId: feb, periodKey: 202602 },
    ]);
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    await editAndSave();

    // ⛔ Мутація: прибрати цикл `followRecalculation` з `onSuccess` — жодного запиту стану задач, зрізи не скинуться.
    await waitFor(() => expect(new Set(jobRequests)).toEqual(new Set([jan, feb])));
    await waitFor(() => expect(recalcStatus()).toBe('running'));
    expect(screen.getByText('⟦grid.recalculating⟧')).toBeTruthy();

    // Поки задачі йдуть — зрізи не чіпаються (CL-01: нуль перезапитів до завершення).
    expect(stale(client, slices.jan)).toBe(false);
    expect(stale(client, slices.feb)).toBe(false);

    jobStates.set(jan, { state: 'Succeeded', writtenCount: 3, writtenPeriods: [202601] });
    jobStates.set(feb, { state: 'Succeeded', writtenCount: 2, writtenPeriods: [202602] });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(RecalculationPollMs + 100);
    });

    await waitFor(() => expect(stale(client, slices.jan)).toBe(true));
    await waitFor(() => expect(stale(client, slices.feb)).toBe(true));
    // Перезапит (намір `invalidateSlicesForPeriods`) — лише зрізи періодів задач; період без задачі (закритий /
    // запланований) лише позначається застарілим без запиту, як і решта зрізів (`refetchType: 'none'`).
    expect(refetched(client, invalidate, slices.jan)).toBe(true);
    expect(refetched(client, invalidate, slices.feb)).toBe(true);
    expect(refetched(client, invalidate, slices.mar)).toBe(false);
    await waitFor(() => expect(recalcStatus()).toBe('succeeded'));
  });

  it('статус показує задачу періоду сторінки, а не останню', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const jan = 'IRecalculationJob#b35-b-202601';
    const feb = 'IRecalculationJob#b35-b-202602';
    jobStates.set(jan, { state: 'Failed' });
    jobStates.set(feb, { state: 'Running' });
    show(
      [
        { jobId: jan, periodKey: 202601 },
        { jobId: feb, periodKey: 202602 },
      ],
      202601,
    );

    await editAndSave();

    // ⛔ Мутація: завжди брати останню задачу (`usable.at(-1)`) — тут «running» замість «failed».
    await waitFor(() => expect(recalcStatus()).toBe('failed'));
    expect(screen.getByText('⟦grid.recalcFailed⟧')).toBeTruthy();
  });
});
