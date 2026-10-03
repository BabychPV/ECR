/**
 * Ctrl+V після закриття редактора комірки (T4-02, P1).
 *
 * ⛔ Дефект: після закриття редактора (Esc, Enter, Tab, blur) фокус може
 * лишитися на `<body>` (редактор знімає фокус з `<input>` через `blur()` і
 * видаляється, а фокус-тримач сітки не відновлюється), а подію `paste`
 * браузер кидає в елемент виділення — абзац чи `<body>` поза обгорткою
 * сітки. React-`onPaste` обгортки (`DocumentGrid`) її не бачить, і вставка
 * мовчки не відбувається до F5. Відтворено в живому RevoGrid (Chromium): після
 * Esc у редакторі `document.activeElement === BODY`, подія `paste` на
 * сторінковий абзац до обробника сітки не доходить.
 *
 * ⚠ Виправлення — слухач `paste` на `document`: якщо ціль поза сіткою й НЕ є
 * полем вводу, фокус не в чужому елементі, а остання активна сітка — ця, подія
 * передається обробнику сітки. Paste у `<input>`/`<textarea>`/`select`/
 * contenteditable (зокрема в редакторі комірки), у модалці й усередині самої
 * сітки (її бачить React) не чіпаємо. «Активна» — та, що останньою мала фокус
 * комірки: на сторінці таблиць кілька.
 */

let active: HTMLElement | null = null;

const EditableSelector = 'input, textarea, select, [contenteditable=""], [contenteditable="true"]';
const ModalSelector = '[role="dialog"], [aria-modal="true"], dialog[open]';

export function installBodyPasteRedirect(
  container: HTMLElement,
  onPaste: (event: ClipboardEvent) => void,
): () => void {
  const doc = container.ownerDocument;
  const mark = (): void => {
    active = container;
  };

  const handler = (event: ClipboardEvent): void => {
    if (active !== container || !container.isConnected || event.defaultPrevented) return;

    const target = event.target;
    if (target instanceof Node && container.contains(target)) return;
    if (target instanceof Element && target.closest(EditableSelector) !== null) return;
    if (doc.querySelector(ModalSelector) !== null) return;

    // Фокус у чужому елементі поза сіткою — вставка не для сітки. Фокус на
    // `body` або всередині сітки (фокус-тримач комірки) - наш випадок: Chrome
    // для нередагованої цілі визначає target за ВИДІЛЕННЯМ тексту, яке після
    // редактора лишається в `<p>` поза сіткою, а не за `activeElement`.
    const focused = doc.activeElement;
    if (
      focused !== null &&
      focused !== doc.body &&
      focused !== doc.documentElement &&
      !container.contains(focused)
    ) {
      return;
    }

    onPaste(event);
  };

  container.addEventListener('focuscell', mark);
  doc.addEventListener('paste', handler);

  return () => {
    container.removeEventListener('focuscell', mark);
    doc.removeEventListener('paste', handler);
    if (active === container) active = null;
  };
}
