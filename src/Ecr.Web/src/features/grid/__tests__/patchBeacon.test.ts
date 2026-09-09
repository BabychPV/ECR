import { afterEach, describe, expect, it, vi } from 'vitest';
import { sendPatchBeacon } from '../useCellPatch';
import type { PatchCellsRequest } from '@/api/types';

/**
 * Останній пакет правок при закритті вкладки (`B-35`, `#38`).
 *
 * ⛔ Звичайний `apiFetch` тут не підходить: `beforeunload` не чекає на
 * `await`. `keepalive: true` — єдине, що браузер шанує в цей момент.
 */
describe('sendPatchBeacon', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  const request: PatchCellsRequest = {
    tableInstanceId: 700,
    periodKey: 202609,
    origin: 'UserEdit',
    rows: [{ rowKey: 'R1', baseVersion: '0x01', cells: [{ columnCode: 'C1', value: 5, isEmpty: false }] }],
  };

  it('надсилає PATCH на маршрут документа з keepalive: true', () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 200 }));

    sendPatchBeacon(42, request);

    // ⚠ D-134: без `keepalive: true` браузер обриває запит разом зі
    // сторінкою — цей рядок і ловить його відсутність.
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const [url, init] = fetchSpy.mock.calls[0]!;

    expect(url).toBe('/api/v1/documents/42/cells');
    expect(init).toMatchObject({
      method: 'PATCH',
      keepalive: true,
      credentials: 'include',
    });
    expect(JSON.parse(init?.body as string)).toEqual(request);
  });

  it('не кидає виняток, якщо fetch синхронно падає', () => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(() => {
      throw new Error('мережа недоступна');
    });

    // ⚠ Викликається на вивантаженні сторінки: показати помилку вже нема на
    // чому, тому функція не має права кидати далі.
    expect(() => sendPatchBeacon(42, request)).not.toThrow();
  });
});
