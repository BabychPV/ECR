import { afterEach, describe, expect, it, vi } from 'vitest';
import { refreshPermitBinding } from '@/features/permits/api/bindingsRuntimeApi';

/**
 * Споживач «оновити з дозволу» (Permits, PM-10): адреса, метод і розбір відповіді. Екрана ще немає (PM-11) — сторож
 * «кожна дія сервера має споживача» бачить літерал, але не бачить, ЩО відправлено.
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('API «оновити з дозволу»', () => {
  it('шле POST на адресу документа з кодованим кодом і періодом та повертає підсумок', async () => {
    const calls: { url: string; method: string }[] = [];
    const answer = { added: 2, updated: 1, removedFromPermit: ['P7'], warnings: [{ code: 'cellNotFromPermit', text: 'x' }] };

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        calls.push({ url: String(input), method: (init?.method ?? 'GET').toUpperCase() });

        return new Response(JSON.stringify(answer), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

    const result = await refreshPermitBinding(42, 'A/B', 202601);

    expect(calls).toEqual([
      { url: '/api/v1/documents/42/permit-bindings/A%2FB/refresh?periodKey=202601', method: 'POST' },
    ]);
    expect(result).toEqual(answer);
  });

  it('відмову сервера (закритий період) не ковтає', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ errorCode: 'ECR-ACCS-0403', status: 403 }), {
            status: 403,
            headers: { 'Content-Type': 'application/json' },
          }),
      ),
    );

    await expect(refreshPermitBinding(42, 'B1', 202601)).rejects.toBeDefined();
  });
});
