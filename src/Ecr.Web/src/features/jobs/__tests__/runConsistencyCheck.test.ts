import { afterEach, describe, expect, it, vi } from 'vitest';
import { runConsistencyCheck } from '@/features/jobs/api';

/**
 * Прогін перевірки узгодженості на вимогу (`BE-30`).
 *
 * ⛔ Чому дія взагалі існує: знахідку узгодженості НЕ МОЖНА «взяти до відома»
 * — пряме рішення людини (`Q15-03`), знахідка зникає сама, коли наступна
 * перевірка проходить. Отже прогін на вимогу — єдиний спосіб зняти з переліку
 * знахідку, причину якої вже усунули.
 *
 * ⚠ Головне твердження файлу — ПРИЧИНА в тілі запиту. Сервер вимагає її і
 * пише в журнал безпеки; виклик, що шле порожнє тіло, дав би `422` на кожне
 * натискання, і побачити це можна було б лише в браузері.
 */

const original = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = original;
});

/** Перехоплює запит і повертає надіслані адресу, метод і тіло. */
function capture(): {
  url: () => string;
  method: () => string | undefined;
  body: () => string | undefined;
} {
  let url = '';
  let method: string | undefined;
  let body: string | undefined;

  globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    url = String(input);
    method = init?.method;
    body = typeof init?.body === 'string' ? init.body : undefined;

    return new Response(JSON.stringify({ jobId: 'IConsistencyCheckJob-7f3a' }), {
      status: 202,
      headers: { 'Content-Type': 'application/json' },
    });
  }) as typeof globalThis.fetch;

  return { url: () => url, method: () => method, body: () => body };
}

describe('прогін перевірки узгодженості на вимогу', () => {
  it('це POST на адресу перевірки, а не на чергу задач', async () => {
    const sent = capture();

    await runConsistencyCheck('Полагодили довідник');

    expect(sent.url()).toBe('/api/v1/consistency/run');
    expect(sent.method()).toBe('POST');
  });

  it('несе причину в тілі — без неї сервер відповідає 422', async () => {
    const sent = capture();

    await runConsistencyCheck('Полагодили довідник');

    expect(JSON.parse(sent.body() ?? '{}')).toEqual({ reason: 'Полагодили довідник' });
  });

  it('повертає ідентифікатор задачі з відповіді 202', async () => {
    capture();

    const accepted = await runConsistencyCheck('Полагодили довідник');

    // ⚠ 202 — обіцянка, не результат: стан клієнт дочитує окремим
    // `GET /jobs/{jobId}`, і саме тому ідентифікатор має дожити, а разом із
    // ним — готова адреса опитування.
    expect(accepted.jobId).toBe('IConsistencyCheckJob-7f3a');
    expect(accepted.statusUrl).toBe('/api/v1/jobs/IConsistencyCheckJob-7f3a');
  });
});
