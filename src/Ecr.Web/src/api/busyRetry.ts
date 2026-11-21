import { EcrApiError } from './client';

/**
 * Повтор запиту, який сервер відхилив як «зайнято, приходь за N с» (PSS-29, e2e RC #988).
 *
 * ⛔ Навіщо. Анонімні читання екрана входу (каталог рядків, публічний стан) можуть дістати `429` з
 * `Retry-After` — від межі частоти ECR або від проксі перед ним. Досі будь-яка відмова каталогу показувалася
 * як «Interface texts could not be loaded — check the network connection», тобто людині радили лагодити
 * мережу, яка справна, а сервер тим часом прямо казав, через скільки секунд повторити.
 *
 * ⚠ Правила, і чому саме такі:
 *   - повторюється лише «зайнято»: `429` завжди, `503` — лише з `Retry-After` (база зайнята, `ECR-SYS-0503`).
 *     Решта відмов повтором не лікується (див. `isRetryable` у `client.ts`);
 *   - `Retry-After` шанується: раніше сказаного сервер не питаємо. Без заголовка — короткий експоненційний
 *     відступ (1 с, 2 с, …);
 *   - до затримки додається невеликий випадковий зсув: офіс за одним NAT дістає `429` разом, і без зсуву
 *     всі вкладки повторили б в ту саму мить, коли вікно межі відкриється;
 *   - сумарне очікування обмежене (`budgetMs`): якщо сервер просить чекати довше, ніж лишилося, ми НЕ висимо
 *     на спінері, а одразу віддаємо відмову з `retryAfterSeconds` — екран чесно скаже «спробуйте за N с».
 */

/** Скільки разів повторювати після першої відмови. */
export const BUSY_MAX_RETRIES = 3;

/** Стеля сумарного очікування між спробами, мс. */
export const BUSY_BUDGET_MS = 20_000;

/** Перший відступ без `Retry-After`, мс; далі — удвічі. */
const DEFAULT_BACKOFF_MS = 1_000;

/** Найбільший випадковий зсув затримки, мс. */
const JITTER_MS = 500;

/** Налаштування повтору. */
export interface BusyRetryOptions {
  /** Скільки разів повторювати. */
  readonly maxRetries?: number;
  /** Стеля сумарного очікування, мс. */
  readonly budgetMs?: number;
  /** Сповіщення перед очікуванням: затримка й номер повтору (з 1). */
  readonly onWait?: (delayMs: number, retry: number) => void;
  /** Чи ще потрібен результат (наприклад, мову вже перемкнули) — ні, то повторів більше немає. */
  readonly shouldContinue?: () => boolean;
  /** Очікування; підміняється в тестах. */
  readonly sleep?: (ms: number) => Promise<void>;
  /** Джерело випадковості для зсуву; підміняється в тестах. */
  readonly random?: () => number;
}

/** Чи відмова — «сервер зайнятий, повтори пізніше». */
export function isBusyRefusal(error: unknown): error is EcrApiError {
  if (!(error instanceof EcrApiError)) return false;

  const { status, retryAfterSeconds } = error.problem;
  return status === 429 || (status === 503 && retryAfterSeconds !== undefined);
}

function defaultSleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

/**
 * Виконує `operation`, повторюючи її на «зайнято» за правилами вище.
 *
 * @returns Результат першої вдалої спроби.
 * @throws Останню відмову — якщо вона не «зайнято», повтори вичерпано, бюджет очікування не дозволяє
 * дочекатися `Retry-After` або результат більше не потрібен.
 */
export async function withBusyRetry<T>(operation: () => Promise<T>, options: BusyRetryOptions = {}): Promise<T> {
  const maxRetries = options.maxRetries ?? BUSY_MAX_RETRIES;
  const budgetMs = options.budgetMs ?? BUSY_BUDGET_MS;
  const sleep = options.sleep ?? defaultSleep;
  const random = options.random ?? Math.random;
  const wanted = options.shouldContinue ?? ((): boolean => true);

  let waited = 0;

  for (let retry = 1; ; retry += 1) {
    try {
      return await operation();
    } catch (error: unknown) {
      if (!isBusyRefusal(error) || retry > maxRetries || !wanted()) throw error;

      const advised = error.problem.retryAfterSeconds;
      const base = advised !== undefined ? advised * 1_000 : DEFAULT_BACKOFF_MS * 2 ** (retry - 1);

      // ⛔ Сервер просить чекати довше, ніж ми готові тримати людину на спінері, — чесна відмова зараз.
      if (waited + base > budgetMs) throw error;

      const delay = base > 0 ? base + Math.floor(random() * JITTER_MS) : 0;
      options.onWait?.(delay, retry);
      await sleep(delay);
      waited += delay;

      if (!wanted()) throw error;
    }
  }
}
