import { fireEvent, waitFor } from '@testing-library/react';
import { expect, vi } from 'vitest';
import { settle, type HeldWrites } from '@/test/closedDialogProbe';

/**
 * B149: зонд «три вікна» для діалогу, чия головна кнопка шле запит запису.
 *
 * ⚠ Без залежності від часу. Поверх `holdWrites` (запис чекає `release()`) тут ще й ворота ЧИТАНЬ: після
 * відповіді на запис власник часто чекає перечитування (`await invalidateQueries`, `await refetch()`), і весь
 * цей час діалог ВІДКРИТИЙ. Ворота тримають ці `GET`, доки тест не клацне по кнопці у вікні.
 *
 * Вікна:
 * 1. запит у дорозі (подвійний клік);
 * 2. сервер відповів, власник ще чекає перечитування — діалог відкритий (якщо `refetchWindow`);
 * 3. діалог закрито, але `Modal` ще в DOM (`keepMountedModalsTheme`) — клік після звірки `isConnected`.
 *
 * ⛔ Лише для тестів.
 */

export interface ReadGate {
  /** З цієї миті кожен `GET` чекає `open()`. */
  readonly close: () => void;
  /** Відпускає всі `GET`, що чекають, і більше не тримає нових. */
  readonly open: () => void;
  /** Скільки `GET` зараз чекає (з `path` — лише ті, чий шлях закінчується на нього). */
  readonly waiting: (path?: string) => number;
}

/** Обгортає ПОТОЧНИЙ `fetch` (зазвичай заглушку `holdWrites`) воротами читань. Типово ворота відчинені. */
export function gateReads(): ReadGate {
  const inner = globalThis.fetch;
  let closed = false;
  const held: { readonly url: string; readonly resolve: () => void }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const method = String(init?.method ?? 'GET').toUpperCase();
      if (closed && method === 'GET') {
        const url = String(input).split('?')[0] ?? '';
        await new Promise<void>((resolve) => {
          held.push({ url, resolve });
        });
      }

      return inner(input, init);
    }),
  );

  return {
    close: () => {
      closed = true;
    },
    open: () => {
      closed = false;
      held.splice(0).forEach((entry) => entry.resolve());
    },
    waiting: (path) => held.filter((entry) => path === undefined || entry.url.endsWith(path)).length,
  };
}

/**
 * Проганяє три вікна й вимагає, щоб у журналі запису був рівно `expected` (один запит).
 *
 * `click` — дія підтвердження (зазвичай `fireEvent.click` по кнопці, знайденій ДО закриття);
 * `button` — та сама кнопка (для звірки `isConnected` у вікні 3); `isOpen` — чи діалог ще відкритий.
 */
export async function probeThreeWindows(options: {
  readonly server: HeldWrites;
  readonly gate: ReadGate;
  readonly button: () => HTMLElement;
  readonly isOpen: () => boolean;
  readonly expected: readonly string[];
  /**
   * Шлях `GET`, який власник чекає після відповіді на запис (перечитування переліку), або `null`, якщо між
   * відповіддю й закриттям нічого не чекається. Вікно 2 тримає саме цей запит — не будь-яке інше читання.
   */
  readonly awaitedRead: string | null;
  /**
   * Чи діалог ще відкритий, поки власник чекає (вікно 2). `false` — власник закриває ДО очікування (`onClose();
   * await …`): тоді вікно 2 — це «закрито, а запит ще не завершено». Типово `true`.
   */
  readonly openWhileAwaiting?: boolean;
  /**
   * `'detached'` — тіло діалогу розмонтовується разом зі станом (`{x !== null && …}`): після закриття кнопка
   * від'єднана від документа, і клік до React не дійде. Тоді зонд вимагає саме `isConnected === false`.
   */
  readonly afterClose?: 'connected' | 'detached';
}): Promise<void> {
  const { server, gate, button, isOpen, expected } = options;

  fireEvent.click(button());
  await waitFor(() => {
    expect(server.sent).toEqual(expected);
  });

  // Вікно 1: запит у дорозі.
  fireEvent.click(button());
  await settle();
  expect(server.sent).toEqual(expected);

  if (options.awaitedRead !== null) {
    // Вікно 2: сервер відповів, власник чекає перечитування — діалог ще відкритий.
    gate.close();
    server.release();
    await waitFor(() => {
      expect(gate.waiting(options.awaitedRead ?? undefined)).toBeGreaterThan(0);
    });
    await waitFor(() => {
      expect(isOpen()).toBe(options.openWhileAwaiting ?? true);
    });
    fireEvent.click(button());
    await settle();
    expect(server.sent).toEqual(expected);
    // Очікування власника ще триває: клік припав саме на вікно 2.
    expect(gate.waiting(options.awaitedRead)).toBeGreaterThan(0);
    gate.open();
  } else {
    server.release();
  }

  // Вікно 3: діалог закрито, кнопка ще в DOM.
  await waitFor(() => {
    expect(isOpen()).toBe(false);
  });
  const node = button();
  if (options.afterClose === 'detached') {
    expect(node.isConnected).toBe(false);
    return;
  }

  expect(node.isConnected).toBe(true);
  fireEvent.click(node);
  await settle();
  expect(server.sent).toEqual(expected);
}
