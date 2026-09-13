import '@testing-library/react';

/*
 * ⚠ jsdom не реалізує ані `matchMedia`, ані `ResizeObserver`, а Mantine і
 * RevoGrid звертаються до них при монтуванні. Без цих заглушок компонентний
 * тест падає не на своїй причині, а на відсутньому браузерному API — і
 * причину шукають у власному коді.
 */
if (typeof window !== 'undefined') {
  if (window.matchMedia === undefined) {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: (query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addEventListener: () => {},
        removeEventListener: () => {},
        addListener: () => {},
        removeListener: () => {},
        dispatchEvent: () => false,
      }),
    });
  }

  if (globalThis.ResizeObserver === undefined) {
    globalThis.ResizeObserver = class {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
    } as unknown as typeof ResizeObserver;
  }

  /*
   * ⛔ `Q-299`: jsdom НЕ реалізує `Element.prototype.scrollIntoView` (немає
   * навіть заглушки, на відміну від `ResizeObserver`/`matchMedia` вище).
   * Mantine `Combobox` (усе, що на ньому стоїть — `Select`, `Autocomplete`,
   * …) викликає його з `setTimeout` під час навігації клавіатурою/кліком по
   * опції. Без полі-філу виклик кидає `TypeError:
   * items[index]?.scrollIntoView is not a function` АСИНХРОННО, поза
   * стеком самого тесту — тест міг щойно пройти («GREEN»), а виняток
   * усе одно валить весь воркер Vitest миттю пізніше
   * (`[vitest-pool]: Worker forks emitted error`), і наступний файл у тому ж
   * воркері отримує чужий крах. Симптом на позір — «клік підвисає
   * назавжди»: `userEvent.click` чекає мікрозадачі, які так і не
   * розв'язалися через мертвий воркер.
   */
  if (window.HTMLElement !== undefined && window.HTMLElement.prototype.scrollIntoView === undefined) {
    window.HTMLElement.prototype.scrollIntoView = function scrollIntoView(): void {};
  }
}
