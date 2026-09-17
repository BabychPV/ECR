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

  /*
   * ⛔ КОРІНЬ «повільного Mantine в jsdom». Один рендер випадного блоку
   * `Combobox` коштував ~35 СЕКУНД процесорного часу — і це не рендер, не
   * позиціювання і не обсяг даних, а взаємна рекурсія між jsdom і його
   * власним добирачем селекторів `nwsapi`.
   *
   * ⛔ Механізм, прочитаний у `nwsapi/src/nwsapi.js` і підтверджений
   * профілем. `nwsapi` не вміє станових псевдокласів (`:modal`,
   * `:fullscreen`, …) і питає про них «нативну» реалізацію:
   *
   *     isModal(node)      -> matchesNative(node, ':modal') || isFullscreen(node)
   *     isFullscreen(node) -> matchesNative(node, ':fullscreen') || …
   *     matchesNative      -> _matches || node.matches || …
   *
   * У браузері `node.matches` справді нативний. У jsdom `Element.matches`
   * реалізований ЧЕРЕЗ `nwsapi` — тобто `matchesNative` повертається туди,
   * звідки вийшов, і кожен рівень породжує наступні. Профіль однієї такої
   * перевірки: `:modal` — 56 632 виклики, `:fullscreen` — 82 349 579,
   * глибина вкладеності 2 910, 33.9 с ЧИСТОГО CPU (`cpuUsage`), із них уся
   * верхівка — `nwsapi.matches`/`Element.matches`. Жодного таймера,
   * `requestAnimationFrame` чи мікрозадачі при цьому не створюється, тому
   * ззовні це виглядає як «тест підвис».
   *
   * ⚠ Питає про це не наш код і не Mantine, а `tabbable` усередині пастки
   * фокуса: будь-який змонтований випадний блок/діалог перевіряє, чи він не
   * всередині модального вікна. Тому ціна виникала САМЕ тоді, коли Mantine
   * тримав випадний блок у DOM (`keepMounted`, дефолт `Combobox`) — і саме
   * це раніше сприйняли за «повільний рендер Mantine».
   *
   * ⚠ Відповідь `false` — не спрощення, а єдиний правдивий стан цього
   * середовища: у jsdom немає ні повноекранного режиму, ні модальних
   * діалогів (`showModal` не реалізовано), ні «картинка в картинці», ні
   * показаних popover. `nwsapi` дійшов би рівно до `false` — просто через
   * 82 мільйони викликів.
   *
   * ⚠ Селектор порівнюється ТОЧНО, а не «містить». `div, :modal` мусить
   * лишитися на розборі `nwsapi`: відповісти `false` за цілий складений
   * селектор було б неправильно. Точний збіг покриває всі виклики
   * `tabbable` і не бере на себе чужих рішень.
   *
   * ⚠ `:open`/`:closed` у списку не за виміром, а за читанням джерела:
   * `isOpen`/`isClosed` спершу перевіряють `details`/`dialog`, а для будь-
   * якого іншого елемента падають у той самий `matchesNative` — тобто в ту
   * саму рекурсію. Вони просто ще нікого тут не зачепили.
   */
  const DEAD_STATE_PSEUDO = new Set([
    ':fullscreen',
    ':modal',
    ':picture-in-picture',
    ':popover-open',
    ':open',
    ':closed',
  ]);

  const nwsapiMatches = window.Element.prototype.matches;

  window.Element.prototype.matches = function matches(this: Element, selectors: string): boolean {
    if (DEAD_STATE_PSEUDO.has(selectors)) {
      return false;
    }

    return nwsapiMatches.call(this, selectors);
  };
}
