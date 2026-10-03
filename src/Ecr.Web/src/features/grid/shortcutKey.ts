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
