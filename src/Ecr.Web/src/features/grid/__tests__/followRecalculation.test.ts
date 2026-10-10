import { QueryClient } from '@tanstack/react-query';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { queryKeys } from '@/api/queryKeys';
import { RecalculationPollMs, followRecalculation, isFollowedJob, settleRecalculation } from '../useCellPatch';

/**
 * N-1 (RC15): після правки на одному аркуші зрізи ІНШИХ таблиць документа (Rollup на «Contract») не
 * лишаються зі значеннями до перерахунку, навіть коли сітка, що поставила задачу, вже розмонтована.
 */
const other = queryKeys.slices.one(900, 202609);
const foreignPeriod = queryKeys.slices.one(901, 202610);

function clientWithSlices(): QueryClient {
  const client = new QueryClient();
  client.setQueryData(other, { rows: [] });
  client.setQueryData(foreignPeriod, { rows: [] });
  return client;
}

const stale = (client: QueryClient, key: readonly unknown[]): boolean =>
  client.getQueryState(key)?.isInvalidated === true;

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('перерахунок скидає зрізи документа', () => {
  it('settleRecalculation: зріз іншого аркуша застарів, інший період - ні; повтор задачі ігнорується', async () => {
    const client = clientWithSlices();
    expect(settleRecalculation(client, 'job#1', 7, 202609)).toBe(true);
    await vi.waitFor(() => expect(stale(client, other)).toBe(true));
    expect(stale(client, foreignPeriod)).toBe(false);
    expect(settleRecalculation(client, 'job#1', 7, 202609)).toBe(false);
  });

  it('followRecalculation: задача завершилась без жодної змонтованої сітки - зрізи застарілі', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        new Response(JSON.stringify({ state: 'Succeeded' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })),
    );
    const client = clientWithSlices();
    followRecalculation(client, 'job#2', 7, 202609);
    await vi.waitFor(() => expect(stale(client, other)).toBe(true));
  });

  it('серія швидких правок: один слідкувач, одна фінальна інвалідація після останньої задачі', async () => {
    vi.useFakeTimers();
    const finished = new Set<string>();
    const calls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const id = decodeURIComponent(String(input).split('/').pop() ?? '');
        calls.push(id);
        return new Response(JSON.stringify({ state: finished.has(id) ? 'Succeeded' : 'Running' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );
    const client = clientWithSlices();
    const invalidate = vi.spyOn(client, 'invalidateQueries');
    for (let i = 1; i <= 5; i += 1) followRecalculation(client, `burst#${i}`, 11, 202609);

    // Усі п'ять задач, крім останньої, завершились - зрізи скидати рано.
    for (let i = 1; i <= 4; i += 1) finished.add(`burst#${i}`);
    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 3);
    expect(invalidate).not.toHaveBeenCalled();
    // Один слідкувач: за кожен тік - один запит, і лише про останню задачу.
    // (перший запит іде синхронно зі стартом слідкувача, до решти правок)
    expect(new Set(calls.slice(1))).toEqual(new Set(['burst#5']));
    expect(calls.length).toBeLessThanOrEqual(5);

    finished.add('burst#5');
    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 2);
    const afterFinish = invalidate.mock.calls.length;
    expect(afterFinish).toBeGreaterThan(0);
    const polled = calls.length;
    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 5);
    expect(calls.length).toBe(polled); // слідкувач завершився
    expect(invalidate.mock.calls.length).toBe(afterFinish); // друга інвалідація не з'явилась
    expect(stale(client, other)).toBe(true);
  });
});

/** Сервер, що відповідає «ще виконується» і рахує запити стану задач. */
function stubRunning(): string[] {
  const calls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      calls.push(String(input));
      return new Response(JSON.stringify({ state: 'Running' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
  return calls;
}

describe('AN-108 / P2-03: слідкувач не опитує даремно', () => {
  afterEach(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
  });

  // ⛔ Мутаційний доказ: прибери очікування видимості в `followRecalculation` — запити підуть і з прихованої вкладки.
  it('прихована вкладка — нуль запитів; повернення у вкладку відновлює опитування', async () => {
    vi.useFakeTimers();
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    const calls = stubRunning();
    const client = clientWithSlices();

    followRecalculation(client, 'hidden#1', 21, 202609);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(calls).toHaveLength(0);
    expect(isFollowedJob('hidden#1')).toBe(true);

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(RecalculationPollMs);
    expect(calls.length).toBeGreaterThan(0);
  });

  it('загальна стеля часу: безперервна серія нових задач не тримає слідкувача вічно', async () => {
    vi.useFakeTimers();
    const calls = stubRunning();
    const client = clientWithSlices();

    followRecalculation(client, 'cap#0', 22, 202609);
    // Нова задача кожні 3 с скидає лічильник спроб (останню ставимо на 597-й секунді); стеля в 10 хв мусить
    // спрацювати однаково — без неї слідкувач жив би ще 2 хв після останньої задачі.
    for (let i = 1; i <= 199; i += 1) {
      followRecalculation(client, `cap#${String(i)}`, 22, 202609);
      await vi.advanceTimersByTimeAsync(3_000);
    }
    await vi.advanceTimersByTimeAsync(10_000);
    expect(isFollowedJob('cap#199')).toBe(false);
    const polled = calls.length;
    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 5);
    expect(calls.length).toBe(polled);
  });

  it('слідкувач пішов без кінцевого стану — ключ задачі інвалідовано, сітка підхопить опитування', async () => {
    vi.useFakeTimers();
    stubRunning();
    const client = clientWithSlices();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    followRecalculation(client, 'gone#1', 23, 202609);
    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 61);

    expect(isFollowedJob('gone#1')).toBe(false);
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['job', 'gone#1'], exact: true });
  });
});

/** Сервер, що відповідає «виконано» з указаним `writtenCount` і рахує запити зрізів. */
function stubSucceeded(writtenCount: number | null): string[] {
  const slices: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/tables/')) slices.push(url);
      return new Response(JSON.stringify({ state: 'Succeeded', effectiveState: null, writtenCount }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
  return slices;
}

describe('AN-108 / P2-02: перерахунок, що нічого не записав, зрізів не чіпає', () => {
  // ⛔ Мутаційний доказ: прибери гілку `nothingWritten` у `settleRecalculation` — зріз стане застарілим.
  it('settleRecalculation з writtenCount = 0 — зрізи не застарівають; з null чи > 0 — застарівають', async () => {
    const zero = clientWithSlices();
    const invalidate = vi.spyOn(zero, 'invalidateQueries');
    expect(settleRecalculation(zero, 'none#1', 31, 202609, 0)).toBe(true);
    expect(invalidate).not.toHaveBeenCalled();
    expect(stale(zero, other)).toBe(false);
    expect(settleRecalculation(zero, 'none#1', 31, 202609, 0)).toBe(false);

    const unknown = clientWithSlices();
    expect(settleRecalculation(unknown, 'unknown#1', 31, 202609, null)).toBe(true);
    await vi.waitFor(() => expect(stale(unknown, other)).toBe(true));

    const some = clientWithSlices();
    expect(settleRecalculation(some, 'some#1', 31, 202609, 4)).toBe(true);
    await vi.waitFor(() => expect(stale(some, other)).toBe(true));
  });

  it('слідкувач: задача завершилась з writtenCount = 0 — нуль запитів зрізів, зріз не застарів', async () => {
    vi.useFakeTimers();
    const slices = stubSucceeded(0);
    const client = clientWithSlices();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    followRecalculation(client, 'zero#1', 32, 202609);
    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 2);

    expect(isFollowedJob('zero#1')).toBe(false);
    expect(slices).toHaveLength(0);
    expect(invalidate).not.toHaveBeenCalled();
    expect(stale(client, other)).toBe(false);
  });

  it('слідкувач перейшов зі старішої задачі на нову, що нічого не записала, — зрізи все одно перечитуються', async () => {
    vi.useFakeTimers();
    const finished = new Set<string>();
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const id = decodeURIComponent(String(input).split('/').pop() ?? '');
        const done = finished.has(id);
        return new Response(
          JSON.stringify({ state: done ? 'Succeeded' : 'Running', writtenCount: done ? 0 : null }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }),
    );
    const client = clientWithSlices();

    // Перша задача (могла записати) лишилась без кінцевого стану: слідкувач перейшов на другу.
    followRecalculation(client, 'older#1', 33, 202609);
    followRecalculation(client, 'newer#2', 33, 202609);
    finished.add('newer#2');
    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 3);

    expect(isFollowedJob('newer#2')).toBe(false);
    expect(stale(client, other)).toBe(true);
  });
});
