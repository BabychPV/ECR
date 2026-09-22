/**
 * Гаряча клавіша палітри: `Ctrl+K` / `⌘K`.
 *
 * ⚠ Порівнюється і `key`, і `code`: у кириличній розкладці (ru, kz) та сама
 * клавіша дає `key === 'л'`, а `code` лишається `KeyK`.
 */
export function isPaletteHotkey(event: KeyboardEvent): boolean {
  if (!(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey) return false;

  return event.key.toLowerCase() === 'k' || event.code === 'KeyK';
}

/**
 * Чи належить клавіша комусь іншому.
 *
 * ⛔ У полі вводу сітки (редактор RevoGrid — `<input>`) і в редакторі виразів
 * (Monaco — `<textarea>`, де `Ctrl+K` відкриває акорд `Ctrl+K Ctrl+C`)
 * клавішу НЕ перехоплюємо: інакше палітра ламала б їхні власні скорочення.
 * Те саме — для будь-якого редагованого поля й `contenteditable`, і для події,
 * яку хтось уже обробив (`defaultPrevented`).
 */
export function belongsToSomeoneElse(event: KeyboardEvent): boolean {
  if (event.defaultPrevented) return true;

  const target = event.target;
  if (!(target instanceof HTMLElement)) return false;

  if (target.isContentEditable) return true;

  const tag = target.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;

  // Фокус усередині редактора чи сітки на елементі без власного вводу.
  // ⚠ `contenteditable` ще й селектором: jsdom `isContentEditable` не має.
  return (
    target.closest('.monaco-editor, revo-grid, [contenteditable=""], [contenteditable="true"]') !==
    null
  );
}
