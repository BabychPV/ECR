/**
 * Чи контейнер сітки ЗАРАЗ намальований (N3-12): сам і всі його предки без `display: none` (у режимі однієї
 * таблиці прихована сітка не розмонтовується, а ховається — `SheetTables.tsx`, тож її слухачі на `document` живі).
 *
 * ⚠ Не `getClientRects().length === 0`: jsdom не має розкладки й повертає порожній перелік для КОЖНОГО елемента,
 * тож перевірка вважала б прихованою кожну сітку в тестах. Обхід предків за обчисленим `display` однаково
 * працює в браузері й у jsdom (`style`/`hidden` враховуються обома).
 */
export function isContainerRendered(container: HTMLElement): boolean {
  const view = container.ownerDocument.defaultView;
  if (view === null) return true;

  for (let node: HTMLElement | null = container; node !== null; node = node.parentElement) {
    if (view.getComputedStyle(node).display === 'none') return false;
  }

  return true;
}
