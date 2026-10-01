/**
 * Чи відкрито поверх шторки `panelId` інший діалог (модалка проби, форми, підтвердження).
 *
 * ⚠ Mantine вішає `Escape` кожного `Modal`/`Drawer` на `window` у фазі захоплення, тож
 * одне натискання закривало І верхню модалку, І шторку під нею (P3 живого проходу
 * екрана джерел). Зупинити подію не вийде — обидва слухачі на тому самому `window`.
 * Тому шторка, закрита ПОКИ поверх неї є інший діалог, повертає собі `?panel=`:
 * за відкритої модалки клік повз шторку перекритий оверлеєм, тож закрити її в цю
 * мить міг лише `Escape`, призначений верхньому діалогу.
 */
export function hasDialogAbovePanel(panelId: string): boolean {
  return Array.from(document.querySelectorAll('[role="dialog"]')).some(
    (dialog) => dialog.closest('[data-panel]')?.getAttribute('data-panel') !== panelId,
  );
}
