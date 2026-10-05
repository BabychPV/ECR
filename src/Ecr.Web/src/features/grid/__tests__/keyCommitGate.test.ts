import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { installCopyDefer, installKeyCommitGate } from '../keyCommitGate';

/**
 * T3-01 (P2): швидка послідовність `Enter, 1, Enter, Enter, 2, Enter` без пауз
 * склеювала значення й писала їх не в ту комірку.
 *
 * ⚠ Справжній RevoGrid у jsdom не монтується (див. `keyboardCompat.test.ts`),
 * тому тут — МОДЕЛЬ його ЧАСОВОЇ поведінки, виміряної в Chromium
 * (`@revolist/revogrid` 4.11, `revogr-overlay-selection` + `keyboard.service`):
 *  - Enter у редакторі зберігає значення й закриває редактор (blur) одразу;
 *  - фокус на наступний рядок іде через ~70 мс (`timeout(RESIZE_INTERVAL + 30)`),
 *    після чого летить `focuscell`;
 *  - Enter/символ ПОЗА редактором відкриває редактор на поточній комірці, з її
 *    значенням; `<input>` отримує фокус лише наступним тіком (`await timeout()`).
 * Саму модель підтверджено живим прогоном у браузері: без черги значення
 * губляться чи потрапляють не в ту комірку, з чергою — кожне у свою.
 */
const FocusMoveMs = 70;

interface Grid {
  container: HTMLElement;
  values: string[];
  press: (key: string) => void;
  dispose: () => void;
}

/**
 * T5-01: повільний перехід - так поводиться RevoGrid у `DocumentGrid` під
 * навантаженням (живий журнал при CPU ×4: `celledit` → `focuscell` 176 мс,
 * старий редактор у DOM ще ~450 мс; `setedit` → фокус на `<input>` ~150 мс).
 */
interface SlowTransition {
  /** Затримка переходу фокуса після Enter у редакторі, мс (замість `FocusMoveMs`). */
  readonly focusMoveMs?: number;
  /** Через скільки мс після відкриття `<input>` отримує фокус. */
  readonly openFocusMs?: number;
  /** A1-03: DOM-фокус повертається в сітку через стільки мс ПІСЛЯ `focuscell` (між ними - `<body>`). */
  readonly domFocusLagMs?: number;
  /** A1-03: фокус ПЕРШОГО відкритого редактора - пізніше за стелю вікна (зависла машина). */
  readonly firstOpenFocusMs?: number;
  /** Як справжній RevoGrid: `celledit` при збереженні і `setedit` при відкритті. */
  readonly revoEvents?: boolean;
}

function mountGrid(
  rows: number,
  startRow: number,
  withGate: boolean,
  lingeringEditor = false,
  slow: SlowTransition = {},
): Grid {
  const focusMoveMs = slow.focusMoveMs ?? FocusMoveMs;
  const container = document.createElement('div');
  const overlay = document.createElement('revogr-overlay-selection');
  const holder = document.createElement('div');
  holder.tabIndex = 0;
  overlay.appendChild(holder);
  container.appendChild(overlay);
  document.body.appendChild(container);

  const values = Array.from({ length: rows }, () => '');
  let row = startRow;
  let editor: HTMLInputElement | null = null;
  let opens = 0;

  const open = (initial: string): void => {
    const wrapper = document.createElement('div');
    wrapper.className = 'edit-input-wrapper';
    const input = document.createElement('input');
    input.value = initial;
    wrapper.appendChild(input);
    overlay.appendChild(wrapper);
    editor = input;
    if (slow.revoEvents === true) container.dispatchEvent(new CustomEvent('setedit', { bubbles: true }));
    opens += 1;
    const focusMs = opens === 1 && slow.firstOpenFocusMs !== undefined ? slow.firstOpenFocusMs : slow.openFocusMs ?? 0;
    setTimeout(() => input.focus(), focusMs);
  };

  /** Перехід фокуса сітки (`keyChangeSelection`): лише після паузи, з `focuscell`. */
  const moveFocus = (delta: number): void => {
    setTimeout(() => {
      row = Math.min(Math.max(row + delta, 0), rows - 1);
      if (slow.domFocusLagMs === undefined) {
        holder.focus();
        container.dispatchEvent(new CustomEvent('focuscell', { bubbles: true }));
        return;
      }
      holder.blur();
      container.dispatchEvent(new CustomEvent('focuscell', { bubbles: true }));
      setTimeout(() => holder.focus(), slow.domFocusLagMs);
    }, focusMoveMs);
  };

  overlay.addEventListener('keydown', (event) => {
    if (editor !== null) {
      // A1-03: Tab у `TextEditor` зберігає з `preventFocus`; без коду Tab RevoGrid нікуди не
      // переходить, і редактор ЛИШАЄТЬСЯ відкритим (його закриває лише зміна фокуса). Escape
      // закриває без збереження. Стрілки в режимі редагування - курсору `<input>`.
      if (event.key === 'Tab') {
        values[row] = editor.value;
        if (slow.revoEvents === true) editor.dispatchEvent(new CustomEvent('celledit', { bubbles: true }));
        editor.blur();
        return;
      }

      if (event.key === 'Escape') {
        editor.parentElement?.remove();
        editor = null;
        return;
      }

      if (event.key === 'Enter') {
        values[row] = editor.value;
        if (slow.revoEvents === true) editor.dispatchEvent(new CustomEvent('celledit', { bubbles: true }));
        editor.blur();
        const closing = editor;
        // ⚠ T4-01: справжній RevoGrid лишає старий редактор у DOM до самого
        // переходу фокуса й прибирає його ПІСЛЯ `focuscell` (кілька мс).
        if (!lingeringEditor) {
          closing.parentElement?.remove();
          editor = null;
        }

        setTimeout(() => {
          row = Math.min(row + 1, rows - 1);
          holder.focus();
          container.dispatchEvent(new CustomEvent('focuscell', { bubbles: true }));
          if (lingeringEditor) {
            setTimeout(() => {
              closing.parentElement?.remove();
              editor = null;
            }, 2);
          }
        }, focusMoveMs);
      }

      return;
    }

    if (event.key === 'ArrowDown') moveFocus(1);
    else if (event.key === 'ArrowUp') moveFocus(-1);
    else if (event.key === 'Enter') open(values[row] ?? '');
    // ⚠ RevoGrid не відкриває редактор від ярлика (Ctrl/Alt + символ).
    else if (event.key.length === 1 && !event.ctrlKey && !event.altKey) open(event.key);
  });

  holder.focus();
  const dispose = withGate ? installKeyCommitGate(container) : () => undefined;

  const press = (key: string): void => {
    const target: EventTarget = document.activeElement ?? document.body;
    const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
    target.dispatchEvent(event);

    // Звичайна клавіша друкує символ у сфокусований `<input>` — як браузер.
    if (!event.defaultPrevented && target instanceof HTMLInputElement && key.length === 1) {
      target.value += key;
    }
  };

  return { container, values, press, dispose };
}

/** Надсилає клавіші з паузою `gapMs` між ними й дочікується завершення переходів. */
function type(grid: Grid, keys: string[], gapMs: number): void {
  for (const key of keys) {
    grid.press(key);
    vi.advanceTimersByTime(gapMs);
  }

  vi.advanceTimersByTime(2000);
}

describe('installKeyCommitGate: швидкий ввід не склеює значення (T3-01)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = '';
  });

  const keys = ['Enter', '1', 'Enter', 'Enter', '2', 'Enter'];

  it('без пауз: кожне значення в СВОЇЙ комірці, у порядку введення', () => {
    const grid = mountGrid(4, 1, true);

    type(grid, keys, 0);

    expect(grid.values).toEqual(['', '1', '2', '']);
    grid.dispose();
  });

  it('три значення підряд з паузою 3 мс (темп сканера): жодне не губиться', () => {
    const grid = mountGrid(5, 1, true);

    type(grid, ['Enter', 'a', 'Enter', 'Enter', 'b', 'Enter', 'Enter', 'c', 'Enter'], 3);

    expect(grid.values).toEqual(['', 'a', 'b', 'c', '']);
    grid.dispose();
  });

  it('T4-01: редактор лишається в DOM після focuscell - інтервали 0..120 мс дають значення у СВОЇХ рядках', () => {
    for (const gap of [0, 10, 30, 50, 60, 65, 70, 80, 100, 120]) {
      const grid = mountGrid(5, 1, true, true);

      type(grid, keys, gap);

      expect(grid.values, `gap ${gap}`).toEqual(['', '1', '2', '', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('T5-01: перехід довший за запасний термін (250 мс) - вікно чекає кінця, значення у СВОЇХ рядках', () => {
    // ⛔ Мутаційний доказ: з відпусканням вікна за самим `CommitSettleMs` (стан до T5-01)
    // відтворений Enter влучає в ще живий редактор R1 - друга фіксація, значення
    // лягають через рядок (як у тестувальника: R1=1, R2 порожньо, R3=2).
    for (const gap of [0, 30, 50, 60, 70, 80, 120]) {
      const grid = mountGrid(6, 1, true, true, { focusMoveMs: 400, revoEvents: true });

      type(grid, keys, gap);

      expect(grid.values, `gap ${gap}`).toEqual(['', '1', '2', '', '', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('T5-01: редактор отримує фокус пізніше за запасний термін відкриття (200 мс) - символ не губиться', () => {
    // ⛔ Мутаційний доказ: з відпусканням за самим `OpenSettleMs` символ відтворюється в
    // сітку, що вже в режимі редагування без фокуса на `<input>`, і зникає (1, порожньо, 3).
    for (const gap of [0, 50, 60, 70, 80]) {
      const grid = mountGrid(6, 1, true, true, { openFocusMs: 300, revoEvents: true });

      type(grid, ['Enter', '1', 'Enter', 'Enter', '2', 'Enter', 'Enter', '3', 'Enter'], gap);

      expect(grid.values, `gap ${gap}`).toEqual(['', '1', '2', '3', '', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('T5-01: редактор після збереження так і не зник - черга все одно відтворюється (стеля вікна)', () => {
    const container = document.createElement('div');
    const wrapper = document.createElement('div');
    wrapper.className = 'edit-input-wrapper';
    const input = document.createElement('input');
    wrapper.appendChild(input);
    container.appendChild(wrapper);
    document.body.appendChild(container);
    input.focus();

    const dispose = installKeyCommitGate(container);
    const replayed: string[] = [];
    container.addEventListener('keydown', (event) => replayed.push(event.key));

    // Enter зберіг комірку (`celledit`), але редактор лишився в DOM.
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    input.dispatchEvent(new CustomEvent('celledit', { bubbles: true }));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: '5', bubbles: true, cancelable: true }));
    const beforeRelease = replayed.length;

    vi.advanceTimersByTime(1000);
    expect(replayed.slice(beforeRelease)).toEqual([]);

    vi.advanceTimersByTime(1000);
    expect(replayed.slice(beforeRelease)).toEqual(['5']);
    dispose();
  });

  it('повільний ввід (пауза ≥ 0,1 с) поводиться як раніше', () => {
    const grid = mountGrid(4, 1, true);

    type(grid, keys, 150);

    expect(grid.values).toEqual(['', '1', '2', '']);
    grid.dispose();
  });

  /** Відкриває вікно затримки: Enter, символ, Enter (коміт) — без часу на завершення. */
  function openCommitWindow(grid: Grid): void {
    grid.press('Enter');
    vi.advanceTimersByTime(5);
    grid.press('1');
    grid.press('Enter');
  }

  /** keydown на `target`; повертає, чи дійшов він до слухача на `document` одразу. */
  function fire(target: EventTarget, init: KeyboardEventInit): { delivered: boolean; prevented: boolean } {
    let delivered = false;
    const listener = (): void => {
      delivered = true;
    };
    document.addEventListener('keydown', listener);
    const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init });
    target.dispatchEvent(event);
    document.removeEventListener('keydown', listener);

    return { delivered, prevented: event.defaultPrevented };
  }

  it('M2: клавіша в полі ПОЗА гридом (input/textarea/select/модалка) у вікні затримки не затримується', () => {
    const grid = mountGrid(4, 1, true);
    const modal = document.createElement('div');
    modal.setAttribute('role', 'dialog');
    const outside = [
      document.createElement('input'),
      document.createElement('textarea'),
      document.createElement('select'),
      modal,
    ];
    for (const element of outside) document.body.appendChild(element);

    openCommitWindow(grid);

    for (const element of outside) {
      const result = fire(element, { key: 'x' });
      expect(result.delivered, element.tagName).toBe(true);
      expect(result.prevented, element.tagName).toBe(false);
    }

    // І нічого з цього не відтворюється пізніше в грид.
    vi.advanceTimersByTime(2000);
    expect(grid.values).toEqual(['', '1', '', '']);
    grid.dispose();
  });

  it('M3: Ctrl/Meta/Alt+клавіша (Ctrl+C/V/Z/S) у вікні затримки не затримується й не переупорядковується', () => {
    const grid = mountGrid(4, 1, true);
    const holder = grid.container.querySelector('div[tabindex]') ?? document.body;

    openCommitWindow(grid);

    const combos: KeyboardEventInit[] = [
      { key: 'c', ctrlKey: true },
      { key: 'v', metaKey: true },
      { key: 'z', ctrlKey: true },
      { key: 's', ctrlKey: true },
      { key: 'x', altKey: true },
    ];
    for (const init of combos) {
      for (const target of [holder, document.body]) {
        const result = fire(target, init);
        expect(result.delivered, `${init.key} -> ${target instanceof Element ? target.tagName : ''}`).toBe(true);
        expect(result.prevented).toBe(false);
      }
    }

    // Звичайна клавіша в тому ж вікні, як і раніше, у черзі.
    expect(fire(document.body, { key: 'q' }).delivered).toBe(false);
    grid.dispose();
  });

  it('T4-07: AltGr-символ (ctrl+alt або AltGraph) одразу після Enter ставиться в чергу й не губиться', () => {
    const variants: KeyboardEventInit[] = [
      { key: '@', code: 'Digit2', ctrlKey: true, altKey: true },
      { key: '@', code: 'Digit2', modifierAltGraph: true },
    ];

    for (const init of variants) {
      const grid = mountGrid(4, 1, true);

      openCommitWindow(grid);
      const result = fire(document.body, init);
      grid.press('Enter');
      vi.advanceTimersByTime(2000);

      expect(result.delivered, JSON.stringify(init)).toBe(false);
      expect(grid.values, JSON.stringify(init)).toEqual(['', '1', '@', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('T4-07: Ctrl+Alt+літера, що збігається з кодом клавіші (ярлик), не затримується', () => {
    const grid = mountGrid(4, 1, true);

    openCommitWindow(grid);

    const result = fire(document.body, { key: 'q', code: 'KeyQ', ctrlKey: true, altKey: true });

    expect(result.delivered).toBe(true);
    expect(result.prevented).toBe(false);
    grid.dispose();
  });

  it('AN-39 L8-04: date-редактор - друковані з черги не кидають, уся черга відтворюється', () => {
    const container = document.createElement('div');
    const wrapper = document.createElement('div');
    wrapper.className = 'edit-input-wrapper';
    const input = document.createElement('input');
    input.type = 'date';
    wrapper.appendChild(input);
    container.appendChild(wrapper);
    document.body.appendChild(container);
    input.focus();

    const dispose = installKeyCommitGate(container);
    const replayed: string[] = [];
    container.addEventListener('keydown', (event) => replayed.push(event.key));

    // Enter у редакторі відкриває вікно затримки; наступні символи йдуть у чергу.
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: '1', bubbles: true, cancelable: true }));
    input.dispatchEvent(new KeyboardEvent('keydown', { key: '2', bubbles: true, cancelable: true }));
    const beforeRelease = replayed.length;

    expect(() => vi.advanceTimersByTime(2000)).not.toThrow();
    expect(replayed.slice(beforeRelease)).toEqual(['1', '2']);
    dispose();
  });

  it('контроль моделі: БЕЗ черги та сама послідовність псує дані', () => {
    // Доказ, що тест відтворює дефект, а не проходить за будь-яких умов.
    const grid = mountGrid(4, 1, false);

    type(grid, keys, 0);

    expect(grid.values).not.toEqual(['', '1', '2', '']);
  });

  it('останній рядок: фокус нікуди не переходить — клавіші все одно відтворюються після запасного терміну', () => {
    const grid = mountGrid(2, 1, true);

    type(grid, ['Enter', '7', 'Enter'], 0);

    expect(grid.values).toEqual(['', '7']);
    grid.dispose();
  });

  it('A1-03: «7», стрілка вниз, «20» - стрілка в редакторі, відкритому набором, фіксує й переходить', () => {
    // ⛔ Мутаційний доказ: без `commitAndMove` стрілка йде курсору `<input>`, і за БУДЬ-ЯКОЇ паузи
    // R1 = «720», R2 порожньо (живий Chromium до фіксу - 8 з 8 на 0/100/300/1000 мс).
    for (const gap of [0, 10, 30, 70, 150, 300, 1000]) {
      const grid = mountGrid(5, 1, true, false, { revoEvents: true });

      type(grid, ['7', 'ArrowDown', '2', '0', 'Enter'], gap);

      expect(grid.values, `gap ${gap}`).toEqual(['', '7', '20', '', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('A1-03: стрілка прийшла раніше, ніж редактор отримав фокус (з черги) - порядок клавіш збережено', () => {
    // ⛔ Мутаційний доказ: стрілка в КІНЕЦЬ черги - «2», «0» відтворюються раніше за перехід, і
    // «20» перезаписує «7» в R1 (живий Chromium, пауза 0 мс: 2 з 4).
    for (const gap of [0, 10, 30]) {
      const grid = mountGrid(5, 1, true, false, { revoEvents: true, openFocusMs: 60 });

      type(grid, ['7', 'ArrowDown', '2', '0', 'Enter'], gap);

      expect(grid.values, `gap ${gap}`).toEqual(['', '7', '20', '', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('A1-03: поле отримало фокус ПІСЛЯ вікна «відкриття» (повільна машина) - стрілка все одно фіксує', () => {
    // ⛔ Мутаційний доказ: позначка «набраного» поля лише у вікні «відкриття» - без `setedit`
    // вікно відпущене запасним терміном (200 мс), фокус о 400 мс лишає стрілку курсору: «720».
    const grid = mountGrid(5, 1, true, false, { firstOpenFocusMs: 400 });

    type(grid, ['7'], 500);
    type(grid, ['ArrowDown', '2', '0', 'Enter'], 30);

    expect(grid.values).toEqual(['', '7', '20', '', '']);
    grid.dispose();
  });

  it('A1-03: стрілка поза редактором і одразу цифри - значення в НОВІЙ комірці, не в старій', () => {
    // ⛔ Мутаційний доказ: без вікна «навігації» цифра відкриває редактор на СТАРІЙ комірці
    // до переходу фокуса (~70 мс): «20» у R1 або «2» у R1 і «0» у R2.
    for (const gap of [0, 10, 30, 50, 60]) {
      const grid = mountGrid(5, 1, true, false, { revoEvents: true });

      type(grid, ['ArrowDown', '2', '0', 'Enter'], gap);

      expect(grid.values, `gap ${gap}`).toEqual(['', '', '20', '', '']);
      grid.dispose();
      document.body.innerHTML = '';
    }
  });

  it('A1-03: фокус ще на <body> після focuscell - символ у цій щілині теж чекає в черзі', () => {
    // ⛔ Мутаційний доказ: вікно «навігації» відпускається за самим `focuscell` - символ на
    // `<body>` іде повз чергу й мимо сітки (живий журнал CPU ×4: «204» замість «20» і «4»).
    const grid = mountGrid(5, 1, true, false, { revoEvents: true, domFocusLagMs: 40 });

    grid.press('ArrowDown');
    vi.advanceTimersByTime(FocusMoveMs + 10);
    type(grid, ['2', '0', 'Enter'], 5);

    expect(grid.values).toEqual(['', '', '20', '', '']);
    grid.dispose();
  });

  it('A1-03: дві стрілки підряд без пауз - обидва кроки зараховано, цифра в комірці через одну', () => {
    const grid = mountGrid(6, 1, true, false, { revoEvents: true });

    type(grid, ['ArrowDown', 'ArrowDown', '4', 'Enter'], 0);

    expect(grid.values).toEqual(['', '', '', '4', '', '']);
    grid.dispose();
  });

  it('A1-03: редактор, відкритий Enter (режим правки), - стрілки, як і раніше, рухають курсор', () => {
    const grid = mountGrid(4, 1, true, false, { revoEvents: true });

    type(grid, ['Enter', '7', 'ArrowDown', '2', 'Enter'], 30);

    expect(grid.values).toEqual(['', '72', '', '']);
    grid.dispose();
  });

  it('A1-03: клік мишею в поле, відкрите набором, повертає стрілкам курсор', () => {
    const grid = mountGrid(4, 1, true, false, { revoEvents: true });

    grid.press('7');
    vi.advanceTimersByTime(300);
    grid.container.querySelector('input')?.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    type(grid, ['ArrowDown', '2', 'Enter'], 30);

    expect(grid.values).toEqual(['', '72', '', '']);
    grid.dispose();
  });

  it('контроль моделі A1-03: БЕЗ черги стрілка й одразу цифра пишуть у стару комірку', () => {
    const grid = mountGrid(5, 1, false);

    type(grid, ['ArrowDown', '2', '0', 'Enter'], 0);

    expect(grid.values).not.toEqual(['', '', '20', '', '']);
  });
});

/**
 * T5-02 (T4-08 лишався): живий Chromium - Ctrl+C за 0-30 мс після Enter клав у буфер
 * старе (`copy` летів у `<body>`, бо редактор уже знято, а обгортка сітки події не бачила) або
 * читав виділення до завершення черги клавіш. Слухач `copy` на `document` відкладає запис
 * до кінця вікна. Мутація (перевірено 2026-10-05): прибрати `deferWhileCommitting` у
 * `installCopyDefer` - червоніють ПЕРШИЙ і третій тести. Лише вікно 'commit': Enter у черзі
 * (редактор не відкрито) copy НЕ відкладає - так гейт `keyCommitGateLive` не зачеплено.
 */
describe('installCopyDefer: Ctrl+C у вікні коміту (T5-02)', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = '';
  });

  function fireCopy(target: EventTarget): ClipboardEvent {
    const event = new Event('copy', { bubbles: true, cancelable: true }) as ClipboardEvent;
    target.dispatchEvent(event);

    return event;
  }

  it('copy у <body> під час коміту: скасовано, run - лише після вікна, з уже новим станом', () => {
    const grid = mountGrid(4, 1, true);
    const seen: string[][] = [];
    const stop = installCopyDefer(grid.container, () => seen.push([...grid.values]));

    grid.press('Enter');
    vi.advanceTimersByTime(10);
    grid.press('Enter');
    (document.activeElement as HTMLElement | null)?.blur();

    const event = fireCopy(document.body);

    expect(event.defaultPrevented).toBe(true);
    expect(seen).toEqual([]);

    vi.advanceTimersByTime(2000);

    expect(seen).toEqual([['', '', '', '']]);
    stop();
    grid.dispose();
  });

  it('Enter у черзі (редактор не відкрито): copy не відкладається й НЕ чіпає чергу - ввід цілий', () => {
    const grid = mountGrid(4, 1, true);
    const seen: string[][] = [];
    const stop = installCopyDefer(grid.container, () => seen.push([...grid.values]));

    grid.press('Enter');
    grid.press('7');
    grid.press('Enter');

    const event = fireCopy(document.body);

    expect(event.defaultPrevented).toBe(false);

    vi.advanceTimersByTime(2000);

    expect(seen).toEqual([]);
    expect(grid.values).toEqual(['', '7', '', '']);
    stop();
    grid.dispose();
  });

  it('copy усередині сітки під час коміту теж відкладається', () => {
    const grid = mountGrid(4, 1, true);
    const seen: number[] = [];
    const stop = installCopyDefer(grid.container, () => seen.push(1));

    grid.press('Enter');
    vi.advanceTimersByTime(10);
    grid.press('Enter');

    const event = fireCopy(grid.container);

    expect(event.defaultPrevented).toBe(true);
    vi.advanceTimersByTime(2000);
    expect(seen).toEqual([1]);
    stop();
    grid.dispose();
  });

  it('поза вікном коміту copy не чіпаємо; copy з чужого поля поза сіткою - теж', () => {
    const grid = mountGrid(4, 1, true);
    const seen: number[] = [];
    const stop = installCopyDefer(grid.container, () => seen.push(1));
    const outside = document.createElement('input');
    document.body.appendChild(outside);

    expect(fireCopy(document.body).defaultPrevented).toBe(false);

    grid.press('Enter');
    vi.advanceTimersByTime(10);
    grid.press('Enter');

    expect(fireCopy(outside).defaultPrevented).toBe(false);
    vi.advanceTimersByTime(2000);
    expect(seen).toEqual([]);
    stop();
    grid.dispose();
  });
});
