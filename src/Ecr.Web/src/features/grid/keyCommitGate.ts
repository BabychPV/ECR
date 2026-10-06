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
/**
 * T5-01: стеля вікна. Запасні терміни вище - не «відпустити будь-що»: поки
 * перехід RevoGrid ЩЕ ТРИВАЄ (збережений редактор у DOM, або відкритий ще без
 * фокуса), вікно тримається далі, але не довше за цю стелю.
 */
export const MaxHoldMs = 1500;
/** Крок перевірки кінця переходу, поки вікно відкрите. */
const PollMs = 4;

const EditWrapper = '.edit-input-wrapper';

/**
 * A1-03: клавіші, що переводять фокус сітки через `keyChangeSelection` RevoGrid - тобто
 * з тією самою паузою ~70 мс (`timeout(RESIZE_INTERVAL + 30)`), що й після Enter.
 */
const NavKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Tab']);
const ArrowKeys = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight']);

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
  let holding: 'commit' | 'open' | 'nav' | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let queue: QueuedKey[] = [];
  let afterSettled: (() => void)[] = [];
  // Стан поточного вікна (T5-01): коли відкрите, чи справді збережено комірку
  // (`celledit`), чи був `focuscell`, чи RevoGrid почав відкривати редактор
  // (`setedit`) і чи його обгортка вже з'являлась у DOM.
  let startedAt = 0;
  let saved = false;
  let focusSeen = false;
  let editRequested = false;
  let editorSeen = false;
  // A1-03: скільки переходів стрілкою/Tab ще без `focuscell`.
  let navPending = 0;
  // A1-03: коли символ поза редактором почав відкривати редактор (його поле, що першим отримає
  // фокус, - «набране»). Не прив'язано до вікна «відкриття»: його може відпустити запасний
  // термін раніше, ніж `<input>` отримає фокус.
  let typedOpenAt: number | null = null;
  // A1-03: фіксація стрілкою - без переходу фокуса, вікно чекає лише закриття редактора.
  let inPlace = false;
  // A1-03: власні синтетичні клавіші фіксації не мусять потрапити в чергу.
  let passing = false;
  /**
   * A1-03: поля редактора, відкритого НАБОРОМ символу (як «режим вводу» Excel): стрілка в
   * такому полі фіксує значення й переходить, а не рухає курсор. Відкритий Enter/F2/подвійним
   * кліком (або поле, куди клацнули мишею) - стрілки, як і раніше, рухають курсор.
   */
  const typedEditors = new WeakSet<EventTarget>();

  gates.set(container, {
    isCommitting: () => holding === 'commit',
    afterSettled: (run) => {
      afterSettled.push(run);
    },
  });

  const overlay = (): Element | null => container.querySelector('revogr-overlay-selection');
  const editorClosed = (): boolean => container.querySelector(EditWrapper) === null;

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

  /*
   * ⛔ T5-01 (прохід 5, 50–80 мс, ≈4 %; відтворено на `/_key-commit-gate/document`
   * під CPU ×4 - 3 з 200): запасний термін відпускав вікно, коли перехід
   * RevoGrid ще ТРИВАВ. У `DocumentGrid` після фіксації перерендер (React-стан
   * правки, рядок формули) тягне перехід за 250 мс: журнал - `celledit` R1 2659,
   * `focuscell` 2835, старий `<input>` знову у фокусі 3124, відтворений Enter
   * 3153 у НЬОГО - друга фіксація R1 і стрибок на R3 (R1=1, R2 порожньо, R3=2).
   * Другий вид: вікно «відкриття» відпускалось, поки старий редактор ще в DOM,
   * тож Enter, що відкриває R2, не ставив нового вікна, і символ після нього
   * падав у сітку в режимі редагування - мовчки губився (1, порожньо, 3).
   *
   * Тепер запасний термін відпускає вікно лише тоді, коли переходу справді
   * кінець: для коміту - збережений редактор прибрано з DOM; для відкриття -
   * редактор не відкривається або вже закритий. Інакше вікно тримається далі,
   * до стелі `MaxHoldMs`.
   */
  const settled = (): boolean => {
    const elapsed = Date.now() - startedAt;
    if (elapsed >= MaxHoldMs) return true;

    // ⛔ `focuscell` приходить раніше, ніж DOM-фокус повертається в сітку (живий журнал CPU ×4:
    // focuscell 52074, `<body>` ще активний, focusin 52097): символ на `<body>` у цій щілині
    // йшов повз чергу - редактор відкривався не «набраним» («204» замість «20» і «4»).
    if (holding === 'nav') return elapsed >= CommitSettleMs || (navPending <= 0 && container.contains(doc.activeElement));

    if (holding === 'commit') {
      if (inPlace) return editorClosed();

      // Enter не зберіг комірку (список без варіанта, редактор лишився відкритим) - як раніше.
      if (!saved) return (focusSeen && editorClosed()) || elapsed >= CommitSettleMs;

      // ⛔ T4-01: `focuscell` приходить РАНІШЕ, ніж RevoGrid прибирає старий
      // редактор (журнал при інтервалі 50 мс: focuscell 1543 → focusin DIV 1545).
      // Відтворений у цей момент Enter потрапляв у ще живий `<input>` попередньої
      // комірки. Тож вікно закривається, лише коли редактора вже немає в DOM.
      return editorClosed() && (focusSeen || elapsed >= CommitSettleMs);
    }

    if (!editorClosed()) editorSeen = true;
    if (elapsed < OpenSettleMs) return false;

    // Редактор відкривається (`setedit` був), але `<input>` ще без фокуса - чекаємо.
    return !(editRequested && (!editorSeen || !editorClosed()));
  };

  const tick = (): void => {
    timer = null;
    if (holding === null) return;

    if (settled()) release();
    else timer = setTimeout(tick, PollMs);
  };

  const hold = (kind: 'commit' | 'open' | 'nav'): void => {
    holding = kind;
    navPending = kind === 'nav' ? 1 : 0;
    inPlace = false;
    startedAt = Date.now();
    saved = false;
    focusSeen = false;
    editRequested = false;
    editorSeen = false;
    if (timer !== null) clearTimeout(timer);
    timer = setTimeout(tick, PollMs);
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.isComposing || passing) return;

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

    /*
     * A1-03: ще одна стрілка у вікні навігації, коли черга порожня, проходить одразу (RevoGrid
     * рахує новий крок від фокуса ПІСЛЯ паузи, тож кроки не губляться) і подовжує вікно: інакше
     * затиснута стрілка (автоповтор) відставала б від клавіатури на секунди.
     */
    if (holding === 'nav' && queue.length === 0 && NavKeys.has(key) && !inEditor(target)) {
      navPending += 1;
      startedAt = Date.now();

      return;
    }

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
      if (ArrowKeys.has(key) && !event.shiftKey && typedEditors.has(event.target as EventTarget)) {
        commitAndMove(event, key);

        return;
      }

      if (key === 'Enter' || key === 'Tab') hold('commit');
      if (key === 'F2') typedEditors.delete(event.target as EventTarget);

      return;
    }

    // Будь-яка інша клавіша поза редактором (F2, Enter, стрілка) - редактор уже не «набраний».
    typedOpenAt = null;

    /*
     * ⛔ A1-03: стрілка/Tab поза редактором переводить фокус лише через ~70 мс (на повільній
     * машині - довше). Цифра, набрана раніше, відкривала редактор на СТАРІЙ комірці: живий
     * Chromium, `ArrowDown, 2, 0, Enter` з паузою 0/20/50 мс - 5/5, 5/5, 3/5 збоїв («20» у R1
     * або «2» у R1 і «0» у R2). Тож після неї - вікно «навігації» до `focuscell`.
     * Shift+стрілка лише розширює діапазон (фокус на місці) - вікна не треба.
     */
    if (NavKeys.has(key) && (key === 'Tab' || !event.shiftKey)) {
      hold('nav');

      return;
    }

    // Редактор ще не відкритий: Enter або символ відкриє його, а фокус на
    // `<input>` він отримає не одразу.
    if (container.querySelector(EditWrapper) === null && (key === 'Enter' || isPrintable(event))) {
      hold('open');
      if (key !== 'Enter') typedOpenAt = Date.now();
    }
  };

  /*
   * ⛔ A1-03 (приймання A1, збірка ecr-msi): «7», стрілка вниз, «20» давали «720» в одній
   * комірці за БУДЬ-ЯКОЇ паузи (живий Chromium, 0/100/300/1000 мс - 8 з 8): стандартний
   * редактор RevoGrid у режимі редагування віддає стрілки курсору `<input>`, а не сітці. Для
   * користувача Excel це «режим вводу»: стрілка фіксує набране й переходить.
   *
   * Фіксація - тим самим шляхом, що Tab у редакторі (`TextEditor` зберігає з
   * `preventFocus`), але БЕЗ коду клавіші Tab, тож RevoGrid нікуди не переходить; сама стрілка
   * стає в чергу й відтворюється на сітці після закриття редактора - як звичайна навігація.
   */
  const commitAndMove = (event: KeyboardEvent, key: string): void => {
    const input = event.target as HTMLElement;
    event.preventDefault();
    event.stopImmediatePropagation();
    typedEditors.delete(input);

    hold('commit');
    inPlace = true;
    // ⚠ Tab без коду: `TextEditor` зберігає (`preventFocus`), а редактор лишається відкритим -
    // фокус не переходив. Escape його закриває (`cancelChanges` після збереження нічого не
    // відкочує: `preventSaveOnClose` уже стоїть). Обидві клавіші - повз чергу.
    passing = true;
    try {
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true, cancelable: true }));
    } finally {
      passing = false;
    }
    // ⛔ На ПОЧАТОК черги: стрілка могла сама прийти з черги (набір без пауз, редактор ще без
    // фокуса), і клавіші після неї вже там - у кінці вони б випередили перехід.
    queue.unshift({ init: { key, code: event.code }, target: overlay() });
  };

  const onFocusCell = (): void => {
    if (holding === 'nav') {
      navPending -= 1;
      if (timer !== null) clearTimeout(timer);
      tick();

      return;
    }

    if (holding !== 'commit') return;

    focusSeen = true;
    if (timer !== null) clearTimeout(timer);
    tick();
  };

  // Сигнали RevoGrid (події `revogr-edit`/`revogr-overlay-selection` спливають до контейнера).
  const onCellEdit = (): void => {
    if (holding === 'commit') saved = true;
  };

  const onSetEdit = (): void => {
    if (holding === 'open') editRequested = true;
  };

  const onFocusIn = (event: FocusEvent): void => {
    const target = event.target;
    if (!inEditor(target)) return;

    // Лише стандартний текстовий редактор (`<input>` прямо в обгортці): власні редактори
    // (список, дата, одиниці) мають свої стрілки.
    const typed = typedOpenAt !== null && Date.now() - typedOpenAt < MaxHoldMs;
    typedOpenAt = null;
    if (typed && target instanceof HTMLInputElement && target.parentElement?.matches(EditWrapper) === true) {
      typedEditors.add(target);
    }

    if (holding === 'open') release();
  };

  // Клік мишею в поле - «режим правки» Excel: стрілки знову рухають курсор.
  const onPointerDown = (event: Event): void => {
    typedOpenAt = null;
    if (event.target !== null) typedEditors.delete(event.target);
  };

  doc.addEventListener('keydown', onKeyDown, true);
  container.addEventListener('focuscell', onFocusCell);
  container.addEventListener('focusin', onFocusIn);
  container.addEventListener('celledit', onCellEdit);
  container.addEventListener('setedit', onSetEdit);
  container.addEventListener('mousedown', onPointerDown, true);

  return () => {
    doc.removeEventListener('keydown', onKeyDown, true);
    container.removeEventListener('focuscell', onFocusCell);
    container.removeEventListener('focusin', onFocusIn);
    container.removeEventListener('celledit', onCellEdit);
    container.removeEventListener('setedit', onSetEdit);
    container.removeEventListener('mousedown', onPointerDown, true);
    if (timer !== null) clearTimeout(timer);
    queue = [];
    afterSettled = [];
    holding = null;
    if (gates.get(container) !== undefined) gates.delete(container);
  };
}
