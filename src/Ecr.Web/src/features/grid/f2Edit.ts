/**
 * A2: F2 у сітці документа відкриває редактор поточної комірки (як в Excel).
 *
 * RevoGrid 4.11 F2 не знає зовсім (`KeyboardService.keyDown`: Enter → `change()`, символ →
 * `change(key)`, решта - навігація), тож у `DocumentGrid` F2 нічого не робив, хоча
 * TESTER-SCENARIOS п. «Клавіатура» його згадує.
 *
 * ⚠ Не власний шлях відкриття: F2 стає Enter для ВСІХ подальших читачів цієї самої події (як
 * `keyboardCompat.ts` для legacy-Enter). Тоді й перевірка права - та сама, що для Enter:
 * `canEdit()` RevoGrid (атрибут `readonly` сітки - закритий період, перегляд - і `readonly`
 * колонки з `decide()`, рядок підсумків); і черга вводу `keyCommitGate.ts` бачить звичайний
 * Enter: тримає вікно «відкриття» (символ одразу після F2 не губиться) і НЕ вважає редактор
 * «набраним» (A1-03) - стрілки рухають курсор у тексті, Esc скасовує, Enter/Tab зберігають.
 *
 * ⛔ Слухач - на `window` у фазі capture: черга вводу слухає `document` (capture) і мусить
 * побачити вже Enter. `window` у capture-фазі завжди раніше за `document`.
 *
 * Відмінність від Enter одна: курсор у кінці значення (Enter лишає його там, куди поставив
 * браузер).
 */

const EditWrapper = '.edit-input-wrapper';

/** Скільки після F2 чекаємо фокус поля редактора, щоб поставити курсор у кінець. */
const CaretWaitMs = 1500;

/** Типи `<input>`, де працює API виділення. */
const TextLike = new Set(['text', 'search', 'url', 'tel', 'password']);

function inEditor(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest(EditWrapper) !== null;
}

/** Чи це F2, який має відкрити редактор: без модифікаторів, поза полем редактора. */
export function isOpenEditorF2(event: KeyboardEvent): boolean {
  if (event.key !== 'F2' || event.isComposing) return false;
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return false;

  return !inEditor(event.target);
}

/** Встановлює F2 на контейнері сітки; повертає функцію відписки. */
export function installF2Edit(container: HTMLElement): () => void {
  const doc = container.ownerDocument;
  const win = doc.defaultView;
  let caretUntil = 0;

  const onKeyDown = (event: KeyboardEvent): void => {
    if (!isOpenEditorF2(event)) return;

    const target = event.target;
    if (!(target instanceof Node) || !container.contains(target)) return;
    // Редактор уже відкритий (фокус ще не дійшов до поля) - Enter тут зберіг би його.
    if (container.querySelector(EditWrapper) !== null) return;

    // ⚠ `key` у прототипі - лише геттер: власна властивість екранує його для всіх подальших
    // слухачів цієї події (черга вводу, RevoGrid). F2 у браузері дії за замовчуванням не має.
    Object.defineProperty(event, 'key', { value: 'Enter', configurable: true });
    caretUntil = Date.now() + CaretWaitMs;
  };

  const onFocusIn = (event: FocusEvent): void => {
    if (caretUntil === 0) return;

    const target = event.target;
    if (!inEditor(target)) return;

    const due = Date.now() <= caretUntil;
    caretUntil = 0;
    if (!due || !(target instanceof HTMLInputElement) || !TextLike.has(target.type)) return;

    const end = target.value.length;
    target.setSelectionRange(end, end);
  };

  win?.addEventListener('keydown', onKeyDown, true);
  container.addEventListener('focusin', onFocusIn);

  return () => {
    win?.removeEventListener('keydown', onKeyDown, true);
    container.removeEventListener('focusin', onFocusIn);
  };
}
