import { afterEach, describe, expect, it, vi } from 'vitest';
import { cancelJob } from '@/features/jobs/api';

/**
 * Скасування задачі: адреса й метод.
 *
 * ⛔ Головне твердження файлу — ЕКРАНУВАННЯ `jobId`. Ідентифікатор фонової
 * задачі має вигляд `IRecalculationJob#42`, а `#` в URL починає фрагмент:
 * незакодований він обрізає шлях до `/api/v1/jobs/IRecalculationJob`, і сервер
 * чесно відповідає `404`. Саме цей дефект прожив у `smoke.ps1` до кроку 17 —
 * шістнадцять кроків перед ним проходили, бо ламався рівно один сегмент шляху,
 * а жоден із 1800+ тестів і жоден із семи гейтів його не бачив.
 */

const original = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = original;
});

/** Перехоплює запит і повертає надіслані адресу й метод. */
function capture(): { url: () => string; method: () => string | undefined } {
  let url = '';
  let method: string | undefined;

  globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    url = String(input);
    method = init?.method;

    return new Response(JSON.stringify({ jobId: 'IRecalculationJob#42' }), {
      status: 202,
      headers: { 'Content-Type': 'application/json' },
    });
  }) as typeof globalThis.fetch;

  return { url: () => url, method: () => method };
}

describe('скасування фонової задачі', () => {
  it('кодує # в ідентифікаторі — інакше шлях обривається на фрагменті', async () => {
    const sent = capture();

    await cancelJob('IRecalculationJob#42');

    expect(sent.url()).toBe('/api/v1/jobs/IRecalculationJob%2342/cancel');

    // ⚠ Окреме твердження, а не лише збіг рядка вище: воно падає з читабельною
    // причиною, якщо хтось «спростить» адресу назад до шаблону без кодування.
    expect(sent.url()).not.toContain('#');
  });

  it('кодує і решту небезпечних символів сегмента', async () => {
    const sent = capture();

    // Косу риску кодувати обов'язково: інакше один сегмент шляху
    // перетворюється на два, і маршрут не збігається взагалі.
    await cancelJob('Excel Export/2026#7');

    expect(sent.url()).toBe('/api/v1/jobs/Excel%20Export%2F2026%237/cancel');
  });

  it('це POST — скасування міняє стан, а не читає його', async () => {
    const sent = capture();

    await cancelJob('job-1');

    expect(sent.method()).toBe('POST');
  });

  it('повертає ідентифікатор задачі з відповіді 202', async () => {
    capture();

    const accepted = await cancelJob('IRecalculationJob#42');

    // ⚠ 202 — це обіцянка, не результат: стан `Cancelled` клієнт дочитує
    // окремим `GET /jobs/{jobId}`, і саме тому ідентифікатор має дожити.
    expect(accepted.jobId).toBe('IRecalculationJob#42');
  });
});
