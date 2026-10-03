/**
 * Серіалізація швидкого вводу з клавіатури в комірках сітки (T3-01, P2,
 * ризик псування даних).
 *
 * ⛔ Дефект (тестувальник, прохід 3): послідовність `Enter, 1, Enter, Enter, 2,
 * Enter` без пауз склеювала значення (`R2.QTY = 12` замість 1 і 2,
 * `NAME = aaabbbccc`) і писала їх НЕ в ту комірку. З паузою ≥ 0,1 с усе
 * працювало; тригери — сканер штрих-кодів, макрос, автоввід.
 *
 * ⛔ Першопричина (RevoGrid 4.11, `revogr-overlay-selection` +
 * `keyboard.service`): Enter у редакторі СИНХРОННО зберігає комірку й закриває
 * редактор, але фокус на наступний рядок переносить `focusNext()` →
 * `keyChangeSelection()`, який спершу чекає `timeout(RESIZE_INTERVAL + 30)`
 * (~70 мс). У цьому вікні фокус ще стоїть на ТІЙ САМІЙ комірці, а редактор уже
 * закритий — тож наступний Enter (режим «не редагування») відкриває редактор
 * знову на ній, і редактор показує щойно збережене значення; наступний символ
 * дописується до нього (`1` + `2` = `12`). Далі запізнілий «стрілка вниз»
 * переносить фокус, а решта вводу потрапляє вже не туди. Аналогічно редактор
 * після відкриття фокусує свій `<input>` лише через `await timeout()`, тож
 * символи, що прийшли до цього, губляться.
 *
 * ⚠ Виправлення — не друга копія логіки коміту (див. `keyboardCompat.ts`), а
 * ЧЕРГА клавіш у capture-фазі контейнера: щойно Enter/Tab зафіксував комірку
 * (або Enter/символ відкрив редактор), подальші клавіші затримуються й
 * відтворюються по порядку, коли RevoGrid завершив перехід (подія
 * `focuscell` / фокус на `<input>` редактора) або мине запасний термін. Так
 * кожне значення потрапляє у свою комірку, у порядку введення; звичайний
 * темп людини нічого не затримує — вікно відкривається лише після Enter/Tab.
 */

import { isEnterKeyEvent } from './keyboardCompat';

/** Запасний термін очікування кінця переходу фокуса (останній рядок — `focuscell` не буде). */
export const CommitSettleMs = 250;
/** Запасний термін очікування фокуса на `<input>` щойно відкритого редактора. */
export const OpenSettleMs = 200;

const EditWrapper = '.edit-input-wrapper';

/** Типи `<input>`, де працює API виділення (`setRangeText`). */
const TextLike = new Set(['text', 'search', 'url', 'tel', 'password']);

function inEditor(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest(EditWrapper) !== null;
}

/** Подія з поля відкритого редактора комірки (light DOM RevoGrid спливає до обгортки сітки). */
export function isInCellEditor(target: EventTarget | null): boolean {
  return inEditor(target);
}

function isPrintable(event: KeyboardEvent): boolean {
  // Модифікаторні комбінації відсіяно раніше (`onKeyDown`), тут лише символ.
  return event.key.length === 1;
}

/**
 * AltGr-символ (`@`, `€`, `{`, `\`...). У браузері на Windows AltGr = ctrlKey +
 * altKey одночасно (T4-07), на інших ОС - `getModifierState('AltGraph')`.
 * Це ДРУКОВАНИЙ символ, а не ярлик: його треба ставити в чергу, як звичайну
 * літеру, інакше за < 30 мс після Enter він губиться.
 *
 * ⚠ Ctrl+Alt+літера на розкладці без AltGr (key збігається з літерою коду -
 * `KeyQ`/`q`, `Digit1`/`1`) лишається ярликом і не затримується.
 */
function isAltGrChar(event: KeyboardEvent): boolean {
  if (event.key.length !== 1 || event.metaKey) return false;
  if (event.getModifierState('AltGraph')) return true;
  if (!(event.ctrlKey && event.altKey)) return false;

  const sameAsCode = event.code === `Key${event.key.toUpperCase()}` || event.code === `Digit${event.key}`;

  return !sameAsCode;
}

interface QueuedKey {
  readonly init: KeyboardEventInit;
  readonly target: EventTarget | null;
}

/** Стан затримки коміту контейнера сітки (для відкладення вставки, AN-39/L8-16). */
interface GateState {
  isCommitting(): boolean;
  afterSettled(run: () => void): void;
}

const gates = new WeakMap<HTMLElement, GateState>();

/**
 * AN-39/L8-16: Ctrl+V за 70-250 мс після Enter (макрос, сканер) вставляв у ПОПЕРЕДНЮ комірку:
 * фокус ще не перейшов, а нативний `paste` не ставиться в чергу. Тут вставку відкладають
 * до кінця вікна коміту.
 *
 * @returns `true` - `run` відкладено й виконається після вікна; `false` - вікна немає,
 * викликач виконує вставку сам, одразу.
 */
export function deferWhileCommitting(container: HTMLElement, run: () => void): boolean {
  const gate = gates.get(container);
  if (gate === undefined || !gate.isCommitting()) return false;

  gate.afterSettled(run);

  return true;
}

/** Встановлює чергу клавіш на контейнері сітки; повертає функцію відписки. */
export function installKeyCommitGate(container: HTMLElement): () => void {
  const doc = container.ownerDocument;
  let holding: 'commit' | 'open' | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let queue: QueuedKey[] = [];
  let afterSettled: (() => void)[] = [];

  gates.set(container, {
    isCommitting: () => holding === 'commit',
    afterSettled: (run) => {
      afterSettled.push(run);
    },
  });

  const overlay = (): Element | null => container.querySelector('revogr-overlay-selection');

  const resolveTarget = (queued: QueuedKey): EventTarget => {
    const active = document.activeElement;
    if (active instanceof HTMLInputElement && inEditor(active) && container.contains(active)) return active;

    const original = queued.target;
    if (original instanceof Node && original.isConnected && container.contains(original)) return original;

    return overlay() ?? container;
  };

  const replay = (queued: QueuedKey): void => {
    const target = resolveTarget(queued);
    const event = new KeyboardEvent('keydown', { ...queued.init, bubbles: true, cancelable: true });
    target.dispatchEvent(event);

    // ⚠ Синтетичний keydown символ у `<input>` не вводить — дописуємо самі,
    // лише якщо ніхто не скасував подію й редактор уже має фокус.
    if (
      !event.defaultPrevented &&
      target instanceof HTMLInputElement &&
      inEditor(target) &&
      TextLike.has(target.type) &&
      isPrintable(event)
    ) {
      // AN-39/L8-04: `setRangeText` для date/number/... кидає InvalidStateError - решта
      // черги «зависала» й відтворювалась у іншу комірку. Тому лише текстові типи й try.
      try {
        target.setRangeText(event.key, target.selectionStart ?? target.value.length, target.selectionEnd ?? target.value.length, 'end');
        target.dispatchEvent(new Event('input', { bubbles: true }));
      } catch {
        // поле без API виділення: символ не дописуємо, черга триває
      }
    }
  };

  const release = (): void => {
    if (timer !== null) clearTimeout(timer);
    timer = null;
    holding = null;

    // Відтворення може знову відкрити вікно (Enter у редакторі) — тоді решта лишається в черзі.
    while (queue.length > 0 && holding === null) {
      const next = queue.shift();
      if (next !== undefined) replay(next);
    }

    // Вікно справді закрите (черга не відкрила нового) - відкладені вставки йдуть у вже
    // новій комірці.
    if (holding === null && afterSettled.length > 0) {
      const due = afterSettled;
      afterSettled = [];
      for (const run of due) run();
    }
  };

  const hold = (kind: 'commit' | 'open'): void => {
    holding = kind;
    if (timer !== null) clearTimeout(timer);
    timer = setTimeout(release, kind === 'commit' ? CommitSettleMs : OpenSettleMs);
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.isComposing) return;

    // ⚠ Слухач стоїть на `document`: на час переходу редактор вже втратив фокус
    // (`blur` при фіксації), а нового ще немає — клавіша йде в `<body>` і до
    // контейнера не дійшла б узагалі, тобто губилась би мовчки.
    const target = event.target;
    const key = isEnterKeyEvent(event) ? 'Enter' : event.key;
    const inside = target instanceof Node && container.contains(target);
    const detached = target === doc.body || target === doc.documentElement;
    if (!inside && !(holding !== null && detached)) return;

    // ⛔ Модифікаторні комбінації (Ctrl/Meta/Alt+клавіша: C, V, Z, S, ...) не
    // затримуємо ВЗАГАЛІ. Нативний `copy`/`paste` породжується дією за
    // замовчуванням САМОГО keydown: `preventDefault` затриманої клавіші
    // мовчки з'їдав Ctrl+C (доведено живим прогоном: без черги `copy` є,
    // з чергою за 5 мс після Enter — немає), а синтетичне відтворення його не
    // повертає. Вони не друкують символ, тож порядок вводу не змінюють.
    // Виняток - AltGr-символ (T4-07): він друкується, тож стає в чергу.
    const altGr = isAltGrChar(event);
    if (!altGr && (event.ctrlKey || event.metaKey || event.altKey)) return;

    if (holding !== null) {
      event.preventDefault();
      event.stopImmediatePropagation();
      queue.push({
        init: {
          key,
          code: event.code,
          shiftKey: event.shiftKey,
          // AltGr уже «використано» на складання символу: при відтворенні
          // модифікатори знімаємо, щоб RevoGrid (isCopy/isPaste...) не прийняв
          // символ за ярлик.
          ctrlKey: altGr ? false : event.ctrlKey,
          altKey: altGr ? false : event.altKey,
          metaKey: event.metaKey,
        },
        target: event.target,
      });

      return;
    }

    if (inEditor(event.target)) {
      if (key === 'Enter' || key === 'Tab') hold('commit');

      return;
    }

    // Редактор ще не відкритий: Enter або символ відкриє його, а фокус на
    // `<input>` він отримає не одразу.
    if (container.querySelector(EditWrapper) === null && (key === 'Enter' || isPrintable(event))) {
      hold('open');
    }
  };

  const editorClosed = (): boolean => container.querySelector(EditWrapper) === null;

  // ⛔ T4-01: `focuscell` приходить РАНІШЕ, ніж RevoGrid прибирає старий
  // редактор (журнал при інтервалі 50 мс: focuscell 1543 → focusin DIV 1545).
  // Відтворений в цей момент Enter потрапляв у ще живий `<input>` попередньої
  // комірки - друга фіксація тієї ж комірки, і фокус стрибав на два рядки. Тож
  // вікно закривається, лише коли переходу кінець (`focuscell`) І редактора вже
  // немає в DOM; поки він є - опитування кожні 4 мс (запасний термін лишається).
  const PollMs = 4;
  const waitForEditorClosed = (): void => {
    if (holding !== 'commit') return;
    if (editorClosed()) release();
    else setTimeout(waitForEditorClosed, PollMs);
  };

  const onFocusCell = (): void => {
    if (holding === 'commit') waitForEditorClosed();
  };

  const onFocusIn = (event: FocusEvent): void => {
    if (holding === 'open' && inEditor(event.target)) release();
  };

  doc.addEventListener('keydown', onKeyDown, true);
  container.addEventListener('focuscell', onFocusCell);
  container.addEventListener('focusin', onFocusIn);

  return () => {
    doc.removeEventListener('keydown', onKeyDown, true);
    container.removeEventListener('focuscell', onFocusCell);
    container.removeEventListener('focusin', onFocusIn);
    if (timer !== null) clearTimeout(timer);
    queue = [];
    afterSettled = [];
    holding = null;
    if (gates.get(container) !== undefined) gates.delete(container);
  };
}
