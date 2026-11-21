import { describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import { BUSY_BUDGET_MS, BUSY_MAX_RETRIES, isBusyRefusal, withBusyRetry } from '@/api/busyRetry';

/**
 * Повтор на «сервер зайнятий» (PSS-29, e2e RC #988).
 *
 * ⚠ Очікування підмінене (`sleep`), випадковість — нульова: перевіряється, СКІЛЬКИ чекали і ЧИ повторили, а не
 * таймери середовища.
 */
function refusal(status: number, retryAfterSeconds?: number): EcrApiError {
  return new EcrApiError({
    title: 'err.http.requestFailed',
    status,
    errorCode: `HTTP-${status}`,
    correlationId: 'cid',
    ...(retryAfterSeconds === undefined ? {} : { retryAfterSeconds }),
  });
}

function harness(): { sleep: (ms: number) => Promise<void>; waits: number[] } {
  const waits: number[] = [];
  return {
    waits,
    sleep: (ms: number) => {
      waits.push(ms);
      return Promise.resolve();
    },
  };
}

describe('isBusyRefusal', () => {
  it('429 — завжди; 503 — лише з Retry-After; решта — ні', () => {
    expect(isBusyRefusal(refusal(429))).toBe(true);
    expect(isBusyRefusal(refusal(429, 5))).toBe(true);
    expect(isBusyRefusal(refusal(503, 2))).toBe(true);
    expect(isBusyRefusal(refusal(503))).toBe(false);
    expect(isBusyRefusal(refusal(500))).toBe(false);
    expect(isBusyRefusal(refusal(401))).toBe(false);
    expect(isBusyRefusal(new TypeError('Failed to fetch'))).toBe(false);
  });
});

describe('withBusyRetry', () => {
  it('429 з Retry-After: чекає рівно стільки, скільки сказав сервер, і повторює', async () => {
    const { sleep, waits } = harness();
    const operation = vi
      .fn<() => Promise<string>>()
      .mockRejectedValueOnce(refusal(429, 3))
      .mockRejectedValueOnce(refusal(429, 2))
      .mockResolvedValueOnce('catalog');

    await expect(withBusyRetry(operation, { sleep, random: () => 0 })).resolves.toBe('catalog');

    expect(operation).toHaveBeenCalledTimes(3);
    expect(waits).toEqual([3_000, 2_000]);
  });

  it('випадковий зсув лише ДОДАЄТЬСЯ: раніше Retry-After повтору немає', async () => {
    const { sleep, waits } = harness();
    const operation = vi.fn<() => Promise<string>>().mockRejectedValueOnce(refusal(429, 4)).mockResolvedValueOnce('ok');

    await withBusyRetry(operation, { sleep, random: () => 0.999 });

    expect(waits).toHaveLength(1);
    expect(waits[0]).toBeGreaterThanOrEqual(4_000);
    expect(waits[0]).toBeLessThan(4_500);
  });

  it('без Retry-After — короткий експоненційний відступ', async () => {
    const { sleep, waits } = harness();
    const operation = vi
      .fn<() => Promise<string>>()
      .mockRejectedValueOnce(refusal(429))
      .mockRejectedValueOnce(refusal(429))
      .mockResolvedValueOnce('ok');

    await withBusyRetry(operation, { sleep, random: () => 0 });

    expect(waits).toEqual([1_000, 2_000]);
  });

  it(`повторів не більше ${BUSY_MAX_RETRIES}: далі — остання відмова з retryAfterSeconds`, async () => {
    const { sleep, waits } = harness();
    const last = refusal(429, 1);
    const operation = vi.fn<() => Promise<string>>().mockRejectedValue(last);

    await expect(withBusyRetry(operation, { sleep, random: () => 0 })).rejects.toBe(last);

    expect(operation).toHaveBeenCalledTimes(BUSY_MAX_RETRIES + 1);
    expect(waits).toHaveLength(BUSY_MAX_RETRIES);
  });

  it(`сервер просить чекати довше за бюджет (${BUSY_BUDGET_MS} мс) — одразу чесна відмова, без спінера`, async () => {
    const { sleep, waits } = harness();
    const operation = vi.fn<() => Promise<string>>().mockRejectedValue(refusal(429, 45));

    const error = await withBusyRetry(operation, { sleep }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(EcrApiError);
    expect((error as EcrApiError).problem.retryAfterSeconds).toBe(45);
    expect(operation).toHaveBeenCalledTimes(1);
    expect(waits).toEqual([]);
  });

  it('інші відмови не повторюються', async () => {
    const { sleep } = harness();
    const operation = vi.fn<() => Promise<string>>().mockRejectedValue(refusal(500));

    await expect(withBusyRetry(operation, { sleep })).rejects.toBeInstanceOf(EcrApiError);
    expect(operation).toHaveBeenCalledTimes(1);
  });

  it('результат більше не потрібен (мову перемкнули) — повтору немає', async () => {
    const { sleep } = harness();
    let wanted = true;
    const operation = vi.fn<() => Promise<string>>().mockRejectedValue(refusal(429, 1));

    const pending = withBusyRetry(operation, {
      sleep: async (ms) => {
        wanted = false;
        await sleep(ms);
      },
      shouldContinue: () => wanted,
    });

    await expect(pending).rejects.toBeInstanceOf(EcrApiError);
    expect(operation).toHaveBeenCalledTimes(1);
  });

  it('onWait сповіщає перед кожним очікуванням', async () => {
    const { sleep } = harness();
    const onWait = vi.fn();
    const operation = vi.fn<() => Promise<string>>().mockRejectedValueOnce(refusal(429, 0)).mockResolvedValueOnce('ok');

    await withBusyRetry(operation, { sleep, onWait });

    expect(onWait).toHaveBeenCalledWith(0, 1);
  });
});
