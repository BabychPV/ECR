import './truncationTooltip.css';

/**
 * Підказка повного тексту для ОБРІЗАНИХ заголовків, групових смуг і комірок сітки документа.
 *
 * Слова людини (11.11): «додайте щоб для документів були тултипи якщо він повністю не відображається
 * рішення має бути продуктивним … я мав на увазі про заголовки та комірки» (документ 96, таблиця 3.00:
 * «Stat…», «Description of equi…», «Rot…»).
 *
 * ⛔ Продуктивність — умова, а не побажання. Тому:
 * - жодного компонента чи слухача на комірку: на корінь сітки ставиться ФІКСОВАНИЙ набір делегованих
 *   слухачів (`pointerover`, `pointerout`, `pointerdown`, `focusin`, `focusout`, `scroll` у фазі
 *   перехоплення) — незалежно від кількості комірок;
 * - жодного читання розмітки під час малювання чи прокрутки: `scrollWidth`/`clientWidth` і
 *   `getBoundingClientRect` читаються лише в момент ПОКАЗУ і лише для тієї однієї комірки, на якій
 *   стоїть миша чи фокус (через ~350 мс наведення або одразу на фокусі з клавіатури);
 * - ОДИН спільний вузол підказки (звичайний `div`, без Mantine і `floating-ui` — бюджет маршруту `D-132`).
 *
 * Що вважається текстом: межа — комірка RevoGrid (`.rgCell`, `.rgHeaderCell`); перевіряється елемент
 * під курсором і його предки до межі включно, перший обрізаний (`scrollWidth > clientWidth`) і дає
 * текст. Для фокуса з клавіатури — сама комірка, потім її нащадки. Кнопки й приховані для очей
 * підписи читалки (`.ecr-header-sr`, крім розділювачів «, ») у текст не входять.
 *
 * ⚠ `title` комірки (підказка «обов'язкова колонка», причина уваги рядка): поки видно нашу підказку,
 * атрибут знімається, а його текст стає другим приглушеним рядком — інакше браузер показав би ДРУГУ
 * підказку поверх. Після приховування атрибут повертається, якщо його тим часом не переписала сітка.
 *
 * ⚠ a11y: `role="tooltip"`, не фокусується. Повний текст комірки читалка чує й так (обрізання лише
 * візуальне), тому `aria-describedby` не ставиться — інакше текст звучав би двічі. Escape ховає;
 * наведення на саму підказку її тримає (WCAG 1.4.13).
 */

/** Затримка показу на наведенні, мс. На фокусі з клавіатури — без затримки. */
export const TruncationTooltipDelayMs = 350;

/** Фокус, що прийшов слідом за натисканням миші, — не «клавіатурний»: підказку там показує наведення. */
const PointerFocusWindowMs = 500;

/** Межа пошуку тексту: комірка даних і комірка шапки (колонка або група) RevoGrid. */
const CellSelector = '.rgCell, .rgHeaderCell';

/** Атрибут для тексту з `line-clamp`: для нього обрізання перевіряється ще й за висотою. */
export const ClampAttribute = 'data-truncation-clamp';

/** Скільки нащадків комірки перевіряється на фокусі (вміст комірок неглибокий; межа — від патологій). */
const MaxFocusCandidates = 16;

/** Відступ підказки від країв вікна, px. */
const ViewportGap = 8;

const SeparatorPattern = /^[\s\p{P}]*$/u;

/** Текст, який бачить людина: без кнопок і без підписів лише для читалки (розділювачі «, » лишаються). */
export function displayedText(element: Element): string {
  const walker = element.ownerDocument.createTreeWalker(element, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
    acceptNode(node: Node): number {
      if (node.nodeType === Node.TEXT_NODE) return NodeFilter.FILTER_ACCEPT;
      const el = node as Element;
      if (el.tagName === 'BUTTON') return NodeFilter.FILTER_REJECT;
      if (el.classList.contains('ecr-header-sr') && !SeparatorPattern.test(el.textContent ?? '')) return NodeFilter.FILTER_REJECT;

      return NodeFilter.FILTER_SKIP;
    },
  });
  let text = '';

  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) text += node.nodeValue ?? '';

  return text.replace(/\s+/g, ' ').trim();
}

/** Чи обрізаний текст елемента. Читає розмітку — кличеться лише в момент показу. */
export function isTruncated(element: HTMLElement): boolean {
  if (element.classList.contains('ecr-header-sr')) return false;
  const width = element.clientWidth;
  // Прихований підпис читалки (1×1 px із `clip`) теж «переповнений», але його не видно взагалі.
  if (width <= 2) return false;
  if (element.scrollWidth - width > 1) return true;

  return element.hasAttribute(ClampAttribute) && element.scrollHeight - element.clientHeight > 1;
}

type Via = 'pointer' | 'focus';

interface Shown {
  readonly cell: Element;
  readonly via: Via;
  readonly stashed: readonly (readonly [Element, string])[];
}

/**
 * Ставить підказки обрізаного тексту на корінь сітки.
 *
 * @param root предок `revo-grid` (події з комірок спливають до нього).
 * @returns функція зняття: прибирає слухачі й вузол підказки.
 */
export function installTruncationTooltip(root: HTMLElement): () => void {
  const doc = root.ownerDocument;
  const view = doc.defaultView ?? window;
  let tip: HTMLDivElement | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let pending: { cell: Element; target: Element } | null = null;
  let shown: Shown | null = null;
  let lastPointerDown = Number.NEGATIVE_INFINITY;

  const cellOf = (target: EventTarget | null): Element | null => {
    if (!(target instanceof Element)) return null;
    const cell = target.closest(CellSelector);

    return cell !== null && root.contains(cell) ? cell : null;
  };

  const cancelPending = (): void => {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    pending = null;
  };

  const onWindowKey = (event: KeyboardEvent): void => {
    if (event.key === 'Escape') hide();
  };
  const onWindowMove = (): void => hide();

  function hide(): void {
    cancelPending();
    if (shown === null) return;

    for (const [element, title] of shown.stashed) {
      // Сітка могла за цей час перемалювати комірку з новим `title` — його не затираємо.
      if (!element.hasAttribute('title')) element.setAttribute('title', title);
    }
    shown = null;
    if (tip !== null) tip.hidden = true;
    doc.removeEventListener('keydown', onWindowKey, true);
    view.removeEventListener('scroll', onWindowMove, true);
    view.removeEventListener('resize', onWindowMove);
  }

  const tipNode = (): HTMLDivElement => {
    if (tip !== null) return tip;
    tip = doc.createElement('div');
    tip.className = 'ecr-trunc-tip';
    tip.setAttribute('role', 'tooltip');
    tip.setAttribute('data-truncation-tooltip', '');
    tip.hidden = true;
    // Наведення на саму підказку її тримає; вихід з неї не в ту саму комірку — ховає.
    tip.addEventListener('pointerleave', (event) => {
      if (shown === null) return;
      const next = (event as MouseEvent).relatedTarget;
      if (next instanceof Node && shown.cell.contains(next)) return;
      hide();
    });
    doc.body.appendChild(tip);

    return tip;
  };

  const pick = (cell: Element, target: Element, via: Via): HTMLElement | null => {
    for (let el: Element | null = target; el !== null; el = el === cell ? null : el.parentElement) {
      if (el instanceof HTMLElement && isTruncated(el)) return el;
    }
    if (via !== 'focus' || target !== cell) return null;

    const descendants = cell.querySelectorAll<HTMLElement>('*');
    for (let i = 0; i < descendants.length && i < MaxFocusCandidates; i += 1) {
      const el = descendants[i];
      if (el !== undefined && isTruncated(el)) return el;
    }

    return null;
  };

  const show = (cell: Element, target: Element, via: Via): void => {
    cancelPending();
    if (!cell.isConnected) return;
    // Відкритий редактор у комірці — людина бачить і править повне значення.
    if (cell.querySelector('input, textarea, select') !== null) return;

    const element = pick(cell, target, via);
    if (element === null) return;
    const text = displayedText(element);
    if (text === '') return;

    const stashed: [Element, string][] = [];
    for (let el: Element | null = target; el !== null; el = el === cell ? null : el.parentElement) {
      const title = el.getAttribute('title');
      if (title !== null) {
        stashed.push([el, title]);
        el.removeAttribute('title');
      }
    }
    const extra = [...new Set(stashed.map(([, title]) => title.trim()))].filter((title) => title !== '' && title !== text);

    const node = tipNode();
    node.textContent = text;
    if (extra.length > 0) {
      const line = doc.createElement('span');
      line.className = 'ecr-trunc-tip-extra';
      line.textContent = extra.join(' · ');
      node.appendChild(line);
    }

    // Розмітка читається тут і лише тут: одна комірка + сам вузол підказки.
    const box = element.getBoundingClientRect();
    node.style.left = '0px';
    node.style.top = '0px';
    node.hidden = false;
    const width = node.offsetWidth;
    const height = node.offsetHeight;
    const vw = view.innerWidth;
    const vh = view.innerHeight;
    const left = Math.max(ViewportGap, Math.min(box.left, vw - width - ViewportGap));
    // Під коміркою впритул (миша переходить на підказку, не зачепивши сусіда); не влазить — над нею.
    let top = box.bottom;
    if (top + height > vh - ViewportGap) top = box.top - height;
    top = Math.max(ViewportGap, top);
    node.style.left = `${String(Math.round(left))}px`;
    node.style.top = `${String(Math.round(top))}px`;

    shown = { cell, via, stashed };
    doc.addEventListener('keydown', onWindowKey, true);
    view.addEventListener('scroll', onWindowMove, true);
    view.addEventListener('resize', onWindowMove);
  };

  const onPointerOver = (event: Event): void => {
    if ((event as PointerEvent).pointerType === 'touch') return;
    const target = event.target;
    const cell = cellOf(target);

    if (cell === null || !(target instanceof Element)) {
      hide();
      return;
    }
    if (shown !== null && shown.cell === cell) return;
    if (pending !== null && pending.cell === cell) {
      pending.target = target;
      return;
    }

    hide();
    pending = { cell, target };
    timer = setTimeout(() => {
      const next = pending;
      timer = null;
      pending = null;
      if (next !== null) show(next.cell, next.target, 'pointer');
    }, TruncationTooltipDelayMs);
  };

  const onPointerOut = (event: Event): void => {
    const next = (event as MouseEvent).relatedTarget;
    const cell = cellOf(event.target);
    if (cell !== null && next instanceof Node && cell.contains(next)) return;
    if (tip !== null && next instanceof Node && tip.contains(next)) return;

    if (pending !== null && pending.cell === cell) cancelPending();
    if (shown !== null && shown.via === 'pointer' && shown.cell === cell) hide();
  };

  const onPointerDown = (): void => {
    lastPointerDown = Date.now();
    hide();
  };

  const onFocusIn = (event: Event): void => {
    const cell = cellOf(event.target);
    if (cell === null) {
      hide();
      return;
    }
    if (Date.now() - lastPointerDown < PointerFocusWindowMs) return;
    if (shown !== null && shown.cell === cell) return;

    hide();
    show(cell, cell, 'focus');
  };

  const onFocusOut = (event: Event): void => {
    if (shown === null || shown.via !== 'focus') return;
    const next = (event as FocusEvent).relatedTarget;
    if (next instanceof Node && shown.cell.contains(next)) return;
    hide();
  };

  const onScroll = (): void => hide();

  root.addEventListener('pointerover', onPointerOver);
  root.addEventListener('pointerout', onPointerOut);
  root.addEventListener('pointerdown', onPointerDown, true);
  root.addEventListener('focusin', onFocusIn);
  root.addEventListener('focusout', onFocusOut);
  root.addEventListener('scroll', onScroll, { capture: true, passive: true });

  return () => {
    hide();
    root.removeEventListener('pointerover', onPointerOver);
    root.removeEventListener('pointerout', onPointerOut);
    root.removeEventListener('pointerdown', onPointerDown, true);
    root.removeEventListener('focusin', onFocusIn);
    root.removeEventListener('focusout', onFocusOut);
    root.removeEventListener('scroll', onScroll, { capture: true });
    tip?.remove();
    tip = null;
  };
}
