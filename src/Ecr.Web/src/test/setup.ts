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

  /*
   * ⛔ `window.scrollTo` в jsdom Є, але не працює: він одразу віддає в
   * virtualConsole `Error: Not implemented: window.scrollTo`. Тому перевірка
   * `=== undefined`, як у заглушках вище, тут НЕ спрацювала б — саме тому
   * метод підмінюється беззастережно, а не за умовою.
   *
   * ⚠ Викликає його не наш код, а `react-router`: `RouterProvider` скидає
   * прокрутку на початок сторінки в layout-ефекті на кожну навігацію. Тобто
   * будь-який тест, що монтує реальний роутер (`AppLayout.prefetch`), друкує
   * у stderr чужу помилку зі стеком `react-dom` — і її читають як збій
   * власного коду, хоча тест міг падати зовсім з іншої причини.
   *
   * ⚠ Заглушка — саме порожня, а не «запам'ятай позицію»: у jsdom немає
   * розкладки, тож прокручувати нічого, і жодне місце в цьому коді не читає
   * `scrollX`/`scrollY`. Записувати позицію означало б завести неперевірений
   * код, що вдає браузерну поведінку, якої тут не існує.
   */
  window.scrollTo = function scrollTo(): void {};
}
