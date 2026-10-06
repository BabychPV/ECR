/**
 * Ярлики сітки Ctrl+S / Ctrl+Z / Ctrl+Y (AN-39 / L8-05).
 *
 * ⛔ Раніше порівнювалось `event.key`: на кириличній розкладці `key` = «я»/«н»/«і»,
 * при CapsLock і Ctrl+Shift+Z — велика літера, тож undo/redo/save не спрацьовували,
 * а Ctrl+S віддавався браузеру («зберегти сторінку»). Фізична клавіша — `event.code`.
 *
 * ⚠ Ctrl+Alt (AltGr на Windows, T4-07) — це друкований символ, а не ярлик:
 * `AltGr+Z` на польській розкладці дає «ż» і не має скасовувати правку.
 *
 * ⚠ Порожній `code` (синтетичні події, старі агенти) — відкат до `key` без
 * урахування регістру.
 */
export type GridShortcut = 'save' | 'undo' | 'redo';

export function gridShortcut(event: {
  code: string;
  key: string;
  shiftKey: boolean;
  altKey: boolean;
}): GridShortcut | null {
  if (event.altKey) return null;

  const letter =
    event.code === 'KeyS' ? 's' : event.code === 'KeyZ' ? 'z' : event.code === 'KeyY' ? 'y' : event.code === '' ? event.key.toLowerCase() : '';

  if (letter === 's') return 'save';
  if (letter === 'z') return event.shiftKey ? 'redo' : 'undo';
  if (letter === 'y') return 'redo';

  return null;
}

/**
 * F9 — перерахунок аркуша (`UI-41`, макет `screen-document.js`: `k === 'F9'` → `doRecalc`).
 *
 * ⚠ Лише «голий» F9: Ctrl/Alt/Shift/Meta+F9 — чужі комбінації (ОС, браузер, читалка).
 * ⚠ `code` першим, `key` — запас для синтетичних подій і агентів без `code`.
 */
export function isRecalculateKey(event: {
  code: string;
  key: string;
  ctrlKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
  metaKey: boolean;
}): boolean {
  if (event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return false;

  return event.code === 'F9' || (event.code === '' && event.key === 'F9');
}

/**
 * Чи натискання прийшло з місця, де клавіша належить вводу, а не сторінці: поле, редактор
 * комірки, діалог. Там F9 не запускає перерахунок — інакше незафіксований набір у редакторі
 * (`keyCommitGate`) пішов би в обхід, а дія за модальним вікном сталася б «за спиною».
 */
export function isTypingOrDialogTarget(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) return false;
  if (target.closest('[role="dialog"], [role="alertdialog"]') !== null) return true;
  if (target instanceof HTMLElement && target.isContentEditable) return true;

  return target.closest('input, textarea, select, [contenteditable="true"]') !== null;
}
