import { vi } from 'vitest';

/**
 * B149: друге вікно класу B145 — діалог ЩЕ ВІДКРИТИЙ, бо власник після відповіді сервера спершу чекає
 * перечитування, а вже потім закриває. Тут тест тримає це перечитування скільки треба.
 *
 * Обгортає ПОТОЧНИЙ `fetch` (ставити ПІСЛЯ `holdWrites`): поки `arm()` і адреса підходить під `match`, читання
 * (`GET`) чекає `open()`; решта запитів іде далі як є.
 *
 * ⛔ Лише для тестів.
 */
export interface HeldReads {
  /** Відтепер підхожі читання чекають `open()`. */
  readonly arm: () => void;
  /** Скільки підхожих читань чекає. */
  readonly waiting: () => number;
  /** Відпускає всі, що чекають, і знімає утримання. */
  readonly open: () => void;
}

export function holdReads(match: (url: string) => boolean): HeldReads {
  const inner = globalThis.fetch;
  const held: (() => void)[] = [];
  let armed = false;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input).split('?')[0] ?? '';
      const method = String(init?.method ?? 'GET').toUpperCase();

      if (armed && method === 'GET' && match(url)) {
        await new Promise<void>((resolve) => {
          held.push(resolve);
        });
      }

      return inner(input, init);
    }),
  );

  return {
    arm: () => {
      armed = true;
    },
    waiting: () => held.length,
    open: () => {
      armed = false;
      for (const resolve of held.splice(0)) resolve();
    },
  };
}
