import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { queryKeys } from '@/api/queryKeys';
import { RecalculationPollMs, useRecalculationStatus } from '../useCellPatch';

/**
 * `BE-05`: статус-рядок сітки стежить за перерахунком по `jobId` з відповіді
 * на `PATCH`.
 *
 * ⛔ Три твердження, і кожне закриває свій дефект:
 *  1. `#` у шляху КОДУЄТЬСЯ. Ідентифікатор має вигляд
 *     `IFormulaRecalculationJob#42`, а `#` в URL починає фрагмент: без
 *     кодування шлях обрізається до `/api/v1/jobs/IFormulaRecalculationJob` і
 *     сервер чесно віддає 404. Саме на цьому падав крок 17 `smoke.ps1`, і
 *     шістнадцять кроків перед ним проходили.
 *  2. Зріз НЕ інвалідується опитуванням. `CL-01…03` прибрали перезапит
 *     найважчого `GET` системи з кожного успішного збереження; перерахунок
 *     ставиться саме автозбереженням, тож інвалідація «на час стеження»
 *     повернула б його туди ж, лише під іншим приводом.
 *  3. Опитування ЗУПИНЯЄТЬСЯ на кінцевому стані — інакше кожна відкрита
 *     вкладка документа питала б готову задачу раз на дві секунди назавжди.
 */

/** Адреси, за якими сходив клієнт, — у порядку викликів. */
let calls: string[] = [];

/** Стан, який віддає підроблений сервер; тести його змінюють. */
let state = 'Running';

function stubFetch(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      calls.push(url);

      return Promise.resolve(
        new Response(
          JSON.stringify({ jobId: 'IFormulaRecalculationJob#42', state, percent: 50, message: null, error: null }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        ),
      );
    }),
  );
}

function jobCalls(): string[] {
  return calls.filter((url) => url.includes('/api/v1/jobs/'));
}

beforeEach(() => {
  calls = [];
  state = 'Running';
  stubFetch();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('стеження за перерахунком після запису комірок', () => {
  it('кодує # у сегменті шляху — інакше адреса обривається на фрагменті', async () => {
    const { client, wrapper } = harness();

    renderHook(() => useRecalculationStatus('IFormulaRecalculationJob#42'), { wrapper });

    await waitFor(() => expect(jobCalls()).toHaveLength(1));

    expect(jobCalls()[0]).toBe('/api/v1/jobs/IFormulaRecalculationJob%2342');

    // ⚠ Окреме твердження, а не лише збіг рядка вище: воно падає з читабельною
    // причиною, якщо хтось «спростить» адресу назад до шаблону без кодування.
    expect(jobCalls()[0]).not.toContain('#');

    client.clear();
  });

  it('`null` не породжує жодного запиту — стежити нема за чим', async () => {
    const { client, wrapper } = harness();

    renderHook(() => useRecalculationStatus(null), { wrapper });

    // ⚠ Мікрозатримка, щоб запит устиг статися, якби `enabled` був неправильний:
    // «нічого не сталося» без вікна, у якому воно МОГЛО статися, не доводить
    // нічого.
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(jobCalls()).toHaveLength(0);

    client.clear();
  });

  it('порожній ідентифікатор теж не опитується — це адреса без сегмента', async () => {
    const { client, wrapper } = harness();

    // ⛔ Сервер віддає `null`, коли перерахунку не поставлено; але порожній
    // рядок, який колись міг би приїхати замість нього, послав би клієнта на
    // `/api/v1/jobs/` — тобто на ПЕРЕЛІК задач під правом `System.ViewHealth`,
    // якого в редактора немає.
    renderHook(() => useRecalculationStatus(''), { wrapper });

    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(jobCalls()).toHaveLength(0);

    client.clear();
  });

  it('не інвалідує зріз і не тягне його заново — інакше це відкат CL-01…03', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { client, wrapper } = harness();
    const invalidate = vi.spyOn(client, 'invalidateQueries');

    renderHook(() => useRecalculationStatus('IFormulaRecalculationJob#42'), { wrapper });

    await vi.waitFor(() => expect(jobCalls().length).toBeGreaterThanOrEqual(1));

    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 3);

    // Кілька опитувань уже відбулося…
    expect(jobCalls().length).toBeGreaterThanOrEqual(2);

    // …і ЖОДНОГО звернення до зрізу чи його інвалідації.
    expect(calls.filter((url) => url.includes('/tables/'))).toHaveLength(0);
    expect(invalidate).not.toHaveBeenCalled();

    // Опудало проти «зріз просто не в кеші»: ключ зрізу існує і живий.
    expect(queryKeys.slices.one(500, 202601)).toBeTruthy();

    client.clear();
  });

  it('опитує не частіше ніж раз на 2 с, доки задача виконується', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });

    const { client, wrapper } = harness();

    renderHook(() => useRecalculationStatus('IFormulaRecalculationJob#42'), { wrapper });

    await vi.waitFor(() => expect(jobCalls()).toHaveLength(1));

    // ⚠ Трохи МЕНШЕ за інтервал: якби опитування йшло з кроком `jobFollow.PollMs`
    // (1.5 с), другий запит уже стався б — і саме цим тест розрізняє два
    // інтервали, а не просто «щось опитується».
    await vi.advanceTimersByTimeAsync(RecalculationPollMs - 100);
    expect(jobCalls()).toHaveLength(1);

    await vi.advanceTimersByTimeAsync(200);
    await vi.waitFor(() => expect(jobCalls()).toHaveLength(2));

    client.clear();
  });

  it('зупиняється на кінцевому стані і повідомляє час завершення', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    state = 'Succeeded';

    const { client, wrapper } = harness();

    const { result } = renderHook(
      () => useRecalculationStatus('IFormulaRecalculationJob#42'),
      { wrapper },
    );

    await vi.waitFor(() => expect(result.current.outcome).toBe('succeeded'));

    await vi.advanceTimersByTimeAsync(RecalculationPollMs * 5);

    // ⛔ РІВНО один запит: нескінченне опитування готової задачі — це запит раз
    // на дві секунди від кожної відкритої вкладки, назавжди.
    expect(jobCalls()).toHaveLength(1);

    // Час завершення — `ГГ:ХХ`, а не порожнеча: саме його показує рядок
    // «Recalculated 14:02» з макета.
    expect(result.current.finishedAt).toMatch(/^\d{2}:\d{2}$/);

    client.clear();
  });

  it('відмова читання стану — це НЕ «ще виконується»', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        calls.push(String(input));

        return Promise.resolve(
          new Response(
            JSON.stringify({ title: 'Ні', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'c1' }),
            { status: 403, headers: { 'Content-Type': 'application/json' } },
          ),
        );
      }),
    );

    const { client, wrapper } = harness();

    const { result } = renderHook(
      () => useRecalculationStatus('IFormulaRecalculationJob#42'),
      { wrapper },
    );

    // ⛔ `unknown`, а не `running`: інакше статус-рядок показував би
    // «перераховується» вічно на задачі, стан якої просто не віддали (Q-156).
    await waitFor(() => expect(result.current.outcome).toBe('unknown'));

    client.clear();
  });
});

function harness(): { client: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return {
    client,
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    ),
  };
}
