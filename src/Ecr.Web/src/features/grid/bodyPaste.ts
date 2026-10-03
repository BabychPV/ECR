/**
 * Ctrl+V після закриття редактора комірки (T4-02, P1) - версія 2, «озброєна».
 *
 * ⛔ Дефект: Chrome визначає target `paste` для нередагованого елемента за
 * ВИДІЛЕННЯМ тексту, а не за `activeElement`. Після відкриття/закриття
 * редактора виділення лишається в `<p>` поза обгорткою сітки, тож React-
 * `onPaste` обгортки (`DocumentGrid`) подію не бачить - вставка мовчки не
 * відбувається до F5; після Esc фокус узагалі на `<body>`.
 *
 * ⛔ Версія 1 (e9028f0c) мала дірку, яку знайшов «Аудит»: «остання активна
 * сітка» не скидалась ніколи, тож після кліку на нейтральну область Ctrl+V зі
 * СТОРОННЬОГО буфера летів у сітку (а без виділення - на кут (0,0), тихо
 * перезаписуючи чужі комірки). Тепер слухач живий лише поки сітка ОЗБРОЄНА:
 *  - озброєння: `focuscell`, `afteredit`, keydown усередині сітки;
 *  - розозброєння: pointerdown/mousedown ПОЗА сіткою (включно з `<body>`),
 *    `focusin` на елемент поза сіткою, сховання вкладки/блюр вікна, відписка
 *    (unmount) і запобіжник - `ArmTtlMs` після останньої активності в сітці;
 *  - сам `blur`/Esc→`<body>` НЕ розброює (це і є випадок T4-02);
 *  - вставка - лише коли є реальне виділення (`hasSelection`); без нього не
 *    робимо нічого й НЕ викликаємо `preventDefault`.
 * Paste у `<input>`/`<textarea>`/`select`/contenteditable (зокрема в редакторі
 * комірки), у модалці й усередині сітки (її бачить React) не чіпаємо.
 */

/** Запобіжник: озброєння спливає через стільки мс після останньої активності в сітці. */
export const ArmTtlMs = 30_000;

let armed: HTMLElement | null = null;

const EditableSelector = 'input, textarea, select, [contenteditable=""], [contenteditable="true"]';
const ModalSelector = '[role="dialog"], [aria-modal="true"], dialog[open]';

export function installBodyPasteRedirect(
  container: HTMLElement,
  onPaste: (event: ClipboardEvent) => void,
  hasSelection: () => boolean,
): () => void {
  const doc = container.ownerDocument;
  const win = doc.defaultView;
  let ttl: ReturnType<typeof setTimeout> | null = null;

  const disarm = (): void => {
    if (ttl !== null) clearTimeout(ttl);
    ttl = null;
    if (armed === container) armed = null;
  };

  const arm = (): void => {
    armed = container;
    if (ttl !== null) clearTimeout(ttl);
    ttl = setTimeout(disarm, ArmTtlMs);
  };

  const outside = (target: EventTarget | null): boolean =>
    !(target instanceof Node && container.contains(target));

  const onPointerDown = (event: Event): void => {
    if (outside(event.target)) disarm();
  };

  const onFocusIn = (event: FocusEvent): void => {
    const target = event.target;
    if (target === doc.body || target === doc.documentElement) return;
    if (outside(target)) disarm();
  };

  const onVisibility = (): void => {
    if (doc.visibilityState === 'hidden') disarm();
  };

  const handler = (event: ClipboardEvent): void => {
    if (armed !== container || !container.isConnected || event.defaultPrevented) return;
    if (!hasSelection()) return;

    const target = event.target;
    if (!outside(target)) return;
    if (target instanceof Element && target.closest(EditableSelector) !== null) return;
    if (doc.querySelector(ModalSelector) !== null) return;

    // Фокус у чужому елементі поза сіткою - вставка не для сітки. Фокус на
    // `body` або всередині сітки (фокус-тримач комірки) - наш випадок.
    const focused = doc.activeElement;
    if (
      focused !== null &&
      focused !== doc.body &&
      focused !== doc.documentElement &&
      outside(focused)
    ) {
      return;
    }

    onPaste(event);
  };

  container.addEventListener('focuscell', arm);
  container.addEventListener('afteredit', arm);
  container.addEventListener('keydown', arm, true);
  doc.addEventListener('pointerdown', onPointerDown, true);
  doc.addEventListener('mousedown', onPointerDown, true);
  doc.addEventListener('focusin', onFocusIn, true);
  doc.addEventListener('visibilitychange', onVisibility);
  win?.addEventListener('blur', disarm);
  doc.addEventListener('paste', handler);

  return () => {
    container.removeEventListener('focuscell', arm);
    container.removeEventListener('afteredit', arm);
    container.removeEventListener('keydown', arm, true);
    doc.removeEventListener('pointerdown', onPointerDown, true);
    doc.removeEventListener('mousedown', onPointerDown, true);
    doc.removeEventListener('focusin', onFocusIn, true);
    doc.removeEventListener('visibilitychange', onVisibility);
    win?.removeEventListener('blur', disarm);
    doc.removeEventListener('paste', handler);
    disarm();
  };
}
