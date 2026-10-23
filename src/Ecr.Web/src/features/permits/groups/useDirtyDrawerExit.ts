import { useEffect, useRef } from 'react';

/**
 * Перехоплює ВСІ способи вийти зі шторки (Esc, хрестик, клік по затемненню), поки в ній є незбережене, і замість закриття
 * кличе `onAttempt` - там показується підтвердження «Discard».
 *
 * ⚠ Чому не проп `DetailDrawer`: компонент (`shared/ui`) закриває шторку сам - прибирає `?panel=` з адреси ще до `onClose`,
 * тож зсередини відмовити вже нічого. Правка `shared/ui` виходила за межі цієї задачі, тому перехоплення живе тут, а
 * `closeOnEscape` шторка отримує `false`, поки `active` (Esc обробляє цей хук).
 *
 * ⛔ Перехоплення - у фазі захоплення на `document`: воно раніше за обробники Mantine/React і гасить подію цілком.
 * Без `active` хук нічого не слухає, тож чиста форма закривається без питань.
 */
export function useDirtyDrawerExit(panelId: string, active: boolean, onAttempt: () => void): void {
  const attempt = useRef(onAttempt);
  attempt.current = onAttempt;

  useEffect(() => {
    if (!active) return undefined;

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
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      // Esc у відкритому списку вибору закриває список, а не шторку.
      const target = event.target;
      if (target instanceof Element && target.closest('[aria-expanded="true"]') !== null) return;

      attempt.current();
    };

    document.addEventListener('click', onClick, true);
    document.addEventListener('keydown', onKeyDown);

    return () => {
      document.removeEventListener('click', onClick, true);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [panelId, active]);
}
