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

  /**
   * ⛔ D-181: `try/catch` навколо `fetch` ловить лише СИНХРОННИЙ кидок (тест
   * вище). Відмова самого запиту — мережа впала, сервер віддав помилку —
   * приходить відхиленням проміса, і `void fetch(...)` без `.catch()` лишає
   * його необробленим.
   *
   * ⚠ Доказ саме такий, бо звичайний `expect` тут безсилий: необроблене
   * відхилення не повертається з функції й не кидається в тесті — його видно
   * ЛИШЕ окремим повідомленням середовища. Тому тест слухає
   * `process.on('unhandledRejection')` — канал, яким V8 звітує про
   * відхилення, що не отримало обробника до кінця мікрозадачної межі.
   *
   * ⚠ Слухачі vitest знімаються на час перевірки й повертаються назад: без
   * цього раннер зловив би те саме відхилення першим і завалив би ВЕСЬ файл
   * замість того, щоб дати одному тесту чесно впасти на власному `expect`.
   */
  it('відхилений fetch не лишає необробленого відхилення промісу', async () => {
    // ⛔ НЕ `vi.spyOn(...).mockRejectedValue(...)`. Мок vitest веде облік
    // `settledResults` і для цього САМ підписується на повернутий проміс —
    // тобто вішає обробник відхилення замість коду, який перевіряємо, і
    // дефект зникає з поля зору (перевірено: з моком тест зелений навіть на
    // зламаному коді). Тому підміна — звичайною функцією, вручну.
    const realFetch = globalThis.fetch;
    globalThis.fetch = (): Promise<Response> => Promise.reject(new Error('мережа впала'));

    const runnerListeners = process.listeners('unhandledRejection');
    process.removeAllListeners('unhandledRejection');

    const unhandled: unknown[] = [];
    const probe = (reason: unknown): void => {
      unhandled.push(reason);
    };
    process.on('unhandledRejection', probe);

    try {
      sendPatchBeacon(42, request);

      // ⚠ Не очікування запиту й не таймаут: V8 звітує про необроблене
      // відхилення на найближчій межі мікрозадач, тож достатньо пропустити
      // один такт циклу подій.
      await new Promise((resolve) => {
        setTimeout(resolve, 0);
      });
    } finally {
      process.off('unhandledRejection', probe);
      for (const listener of runnerListeners) {
        process.on('unhandledRejection', listener);
      }
      globalThis.fetch = realFetch;
    }

    expect(unhandled.map((reason) => String(reason))).toEqual([]);
  });
});
