import { useEffect, useRef } from 'react';

/**
 * Перехоплює ВСІ способи вийти зі шторки (Esc, хрестик, клік по затемненню), поки в ній є незбережене, і замість закриття
 * кличе `onAttempt` - там показується підтвердження «Discard».
 *
 * ⚠ Чому не проп `DetailDrawer`: компонент (`shared/ui`) закриває шторку сам - прибирає `?panel=` з адреси ще до `onClose`,
 * тож зсередини відмовити вже нічого. Правка `shared/ui` виходила за межі цієї задачі, тому перехоплення живе тут, а
 * `closeOnEscape` шторка отримує `false`, поки `active` (Esc обробляє цей хук).
 *
 * ⚠ Відоме обмеження (не регрес, аудит d65ef23f8 P3-B): `active` вимикається і під час збереження (`busy`), тож клік по
 * іншому рядку в цей момент перемикає запис без питання. Поведінку свідомо не змінено.
 *
 * ⛔ Esc обробляється, лише коли його ціль усередині ЦІЄЇ шторки (`[data-panel=panelId]`): Esc у чужому модальному, у
 * списку/календарі в порталі чи з фокусом на сторінці хук не чіпає й не гасить.
 *
 * ⛔ Перехоплення кліку - у фазі захоплення на `document`: воно раніше за обробники Mantine/React і гасить подію цілком.
 * Без `active` хук нічого не слухає, тож чиста форма закривається без питань.
 *
 * ⛔ Esc, що ЗАКРИВАЄ підтвердження, не має одразу відкрити його знову (живий e2e, run 39921583289): `Modal` Mantine слухає
 * Esc на `window` у фазі захоплення, закриває підтвердження, React синхронно перемальовує (дискретна подія) і ВМИКАЄ цей
 * хук - а та сама подія ще їде вгору до `document` і потрапляла б у щойно доданий слухач. У jsdom цього не видно:
 * синхронна `dispatchEvent` не пускає мікрозадачі між слухачами. Тому слухач Esc глухий до кінця задачі, в якій його
 * ввімкнули (`fresh`).
 */
export function useDirtyDrawerExit(panelId: string, active: boolean, onAttempt: () => void): void {
  const attempt = useRef(onAttempt);
  attempt.current = onAttempt;

  useEffect(() => {
    if (!active) return undefined;

    let fresh = true;
    const settle = window.setTimeout(() => {
      fresh = false;
    }, 0);

    const inThisDrawer = (target: EventTarget | null): target is Element =>
      target instanceof Element && target.closest('[data-panel]')?.getAttribute('data-panel') === panelId;

    const onClick = (event: MouseEvent): void => {
      const target = event.target;
      if (!inThisDrawer(target)) return;
      if (target.closest('.mantine-Drawer-close, .mantine-Drawer-overlay') === null) return;

      event.preventDefault();
      event.stopPropagation();
      attempt.current();
    };

    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key !== 'Escape' || event.defaultPrevented || fresh) return;
      // Esc поза шторкою (інший Modal, випадний список чи календар у порталі, тіло сторінки) - не наш: ні питання, ні поглинання.
      const target = event.target;
      if (!inThisDrawer(target)) return;
      // Esc у відкритому списку вибору закриває список, а не шторку.
      if (target.closest('[aria-expanded="true"]') !== null) return;

      attempt.current();
    };

    document.addEventListener('click', onClick, true);
    document.addEventListener('keydown', onKeyDown);

    return () => {
      window.clearTimeout(settle);
      document.removeEventListener('click', onClick, true);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [panelId, active]);
}
