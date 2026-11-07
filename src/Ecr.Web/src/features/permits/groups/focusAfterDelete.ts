/**
 * Фокус після видалення групи (AU13 P3): рядок, що відкрив шторку, зник, і повернення фокуса шторкою падало на BODY.
 * Ціль - сусідній рядок (наступний, інакше попередній), за порожнього переліку - заголовок сторінки.
 *
 * ⚠ Ставиться не одразу: шторка ще закривається (повертає фокус на рядок), а перелік перечитується (рядок зникає
 * пізніше). Чекаємо, доки рядка видаленої групи не стане в DOM, і забираємо фокус лише тоді, коли він ніде (BODY),
 * на видаленому рядку або в шторці, що закривається - фокус, який людина вже кудись перенесла, не чіпаємо.
 */
const Page = '[data-groups-page]';
const Budget = 3000;

function rowOf(code: string): HTMLElement | null {
  return document.querySelector<HTMLElement>(`${Page} tr[data-row-key="${CSS.escape(code)}"]`);
}

/** Код сусіднього рядка (наступний, інакше попередній) - береться ДО видалення, поки рядок ще в переліку. */
export function neighbourOf(code: string): string | null {
  const rows = [...document.querySelectorAll<HTMLElement>(`${Page} tr[data-row-key]`)];
  const index = rows.findIndex((row) => row.getAttribute('data-row-key') === code);
  if (index < 0) return null;
  const next = rows[index + 1] ?? rows[index - 1];

  return next?.getAttribute('data-row-key') ?? null;
}

function stray(deleted: HTMLElement | null): boolean {
  const active = document.activeElement;
  if (active === null || active === document.body) return true;
  if (deleted !== null && (active === deleted || !active.isConnected)) return true;

  return active.closest('[role="dialog"]') !== null;
}

/** Після видалення групи `code` ставить фокус на рядок `neighbour` або на заголовок сторінки. */
export function focusAfterDelete(code: string, neighbour: string | null): void {
  const started = Date.now();
  const deleted = rowOf(code);

  const tick = (): void => {
    const gone = rowOf(code) === null;
    const drawerOpen = document.querySelector('[role="dialog"]') !== null;
    if ((!gone || drawerOpen) && Date.now() - started < Budget) {
      window.setTimeout(tick, 50);

      return;
    }
    if (!stray(deleted)) return;

    const row = neighbour === null ? null : rowOf(neighbour);
    if (row !== null) {
      row.focus();

      return;
    }
    const heading = document.querySelector<HTMLElement>(`${Page} h1`);
    if (heading === null) return;
    if (!heading.hasAttribute('tabindex')) heading.setAttribute('tabindex', '-1');
    heading.focus();
  };

  window.setTimeout(tick, 0);
}
