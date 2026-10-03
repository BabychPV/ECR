import { afterEach, describe, expect, it, vi } from 'vitest';
import { ArmTtlMs, installBodyPasteRedirect } from '../bodyPaste';

/**
 * T4-02 (P1), версія 2: після закриття редактора `paste` іде в абзац поза
 * сіткою, а React-`onPaste` обгортки його не бачить. Слухач на `document`
 * живе лише поки сітка «озброєна» й є виділення (див. `bodyPaste.ts`).
 */
interface Mounted {
  container: HTMLElement;
  holder: HTMLElement;
  focusCell: () => void;
  handler: ReturnType<typeof vi.fn>;
  dispose: () => void;
  selection: { value: boolean };
}

function mount(): Mounted {
  const container = document.createElement('div');
  const holder = container.appendChild(document.createElement('div'));
  holder.tabIndex = 0;
  document.body.appendChild(container);
  const handler = vi.fn();
  const selection = { value: true };
  const dispose = installBodyPasteRedirect(container, handler, () => selection.value);

  return { container, holder, focusCell: () => container.dispatchEvent(new CustomEvent('focuscell')), handler, dispose, selection };
}

function paste(target: EventTarget): Event {
  const event = new Event('paste', { bubbles: true, cancelable: true });
  target.dispatchEvent(event);

  return event;
}

function el<K extends keyof HTMLElementTagNameMap>(tag: K): HTMLElementTagNameMap[K] {
  return document.body.appendChild(document.createElement(tag));
}

afterEach(() => {
  vi.useRealTimers();
  document.body.innerHTML = '';
});

describe('installBodyPasteRedirect v2 (T4-02)', () => {
  it('(а) озброєна сітка з виділенням: paste у абзац/body йде в обробник', () => {
    const m = mount();
    const paragraph = el('p');
    m.focusCell();

    paste(paragraph);
    paste(document.body);

    expect(m.handler).toHaveBeenCalledTimes(2);
    m.dispose();
  });

  it('(а) озброює й afteredit, і keydown усередині сітки', () => {
    const afterEdit = mount();
    afterEdit.container.dispatchEvent(new CustomEvent('afteredit'));
    paste(document.body);
    expect(afterEdit.handler).toHaveBeenCalledTimes(1);
    afterEdit.dispose();

    const keyed = mount();
    keyed.holder.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    paste(document.body);
    expect(keyed.handler).toHaveBeenCalledTimes(1);
    keyed.dispose();
  });

  it('(3) blur фокус-тримача / Esc -> <body> НЕ розброює: вставка лишається', () => {
    const m = mount();
    m.focusCell();
    m.holder.focus();
    m.holder.blur();

    paste(document.body);

    expect(m.handler).toHaveBeenCalledTimes(1);
    m.dispose();
  });

  it('(б) pointerdown/mousedown на нейтральній області (body, абзац) розброює - paste не вставляє', () => {
    for (const type of ['pointerdown', 'mousedown']) {
      const m = mount();
      const paragraph = el('p');
      m.focusCell();

      paragraph.dispatchEvent(new Event(type, { bubbles: true }));
      paste(paragraph);
      paste(document.body);

      expect(m.handler, type).not.toHaveBeenCalled();
      m.dispose();
      document.body.innerHTML = '';
    }

    const onBody = mount();
    onBody.focusCell();
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    paste(document.body);
    expect(onBody.handler).not.toHaveBeenCalled();
    onBody.dispose();
  });

  it('(б) pointerdown усередині сітки не розброює; новий focuscell озброює знову', () => {
    const m = mount();
    m.focusCell();
    m.holder.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    paste(document.body);
    expect(m.handler).toHaveBeenCalledTimes(1);

    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    paste(document.body);
    expect(m.handler).toHaveBeenCalledTimes(1);

    m.focusCell();
    paste(document.body);
    expect(m.handler).toHaveBeenCalledTimes(2);
    m.dispose();
  });

  it('(в) фокус у полі пошуку поза сіткою: розозброєно, paste у поле не чіпається', () => {
    const m = mount();
    const search = el('input');
    m.focusCell();

    search.focus();
    const event = paste(search);

    expect(m.handler).not.toHaveBeenCalled();
    expect(event.defaultPrevented).toBe(false);
    paste(document.body);
    expect(m.handler).not.toHaveBeenCalled();
    m.dispose();
  });

  it('(г) focusin на кнопку поза сіткою (Tab) розброює', () => {
    const m = mount();
    const button = el('button');
    m.focusCell();

    button.focus();
    // Фокус потім пішов з кнопки на `body` - озброєння вже не повертається.
    button.blur();
    paste(document.body);

    expect(m.handler).not.toHaveBeenCalled();
    m.dispose();
  });

  it('(д) сплив часу: озброєння спливає через ArmTtlMs; активність у сітці його подовжує', () => {
    vi.useFakeTimers();
    const m = mount();
    m.focusCell();

    vi.advanceTimersByTime(ArmTtlMs - 1);
    paste(document.body);
    expect(m.handler).toHaveBeenCalledTimes(1);

    // активність у сітці подовжує
    m.holder.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true }));
    vi.advanceTimersByTime(ArmTtlMs - 1);
    paste(document.body);
    expect(m.handler).toHaveBeenCalledTimes(2);

    vi.advanceTimersByTime(2);
    paste(document.body);
    expect(m.handler).toHaveBeenCalledTimes(2);
    m.dispose();
  });

  it('(е) без виділення нічого не робить і не викликає preventDefault', () => {
    const m = mount();
    m.selection.value = false;
    m.focusCell();

    const event = paste(document.body);

    expect(m.handler).not.toHaveBeenCalled();
    expect(event.defaultPrevented).toBe(false);
    m.dispose();
  });

  it('сховання вкладки і blur вікна розброюють', () => {
    const hidden = mount();
    hidden.focusCell();
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    document.dispatchEvent(new Event('visibilitychange'));
    vi.restoreAllMocks();
    paste(document.body);
    expect(hidden.handler).not.toHaveBeenCalled();
    hidden.dispose();

    const blurred = mount();
    blurred.focusCell();
    window.dispatchEvent(new Event('blur'));
    paste(document.body);
    expect(blurred.handler).not.toHaveBeenCalled();
    blurred.dispose();
  });

  it('paste у input/textarea/select/contenteditable поза сіткою НЕ перехоплюється', () => {
    const m = mount();
    const targets = [el('input'), el('textarea'), el('select')];
    const editable = el('div');
    editable.setAttribute('contenteditable', 'true');
    m.focusCell();

    for (const target of [...targets, editable]) paste(target);

    expect(m.handler).not.toHaveBeenCalled();
    m.dispose();
  });

  it('відкрита модалка: paste не перехоплюється', () => {
    const m = mount();
    el('div').setAttribute('role', 'dialog');
    m.focusCell();

    paste(document.body);

    expect(m.handler).not.toHaveBeenCalled();
    m.dispose();
  });

  it('фокус усередині сітки, а paste (за виділенням) іде в абзац поза нею - перехоплюється', () => {
    const m = mount();
    const paragraph = el('p');
    m.focusCell();
    m.holder.focus();

    paste(paragraph);

    expect(m.handler).toHaveBeenCalledTimes(1);
    m.dispose();
  });

  it('paste всередині самої сітки не дублюється (його бачить React-onPaste)', () => {
    const m = mount();
    m.focusCell();

    paste(m.holder);

    expect(m.handler).not.toHaveBeenCalled();
    m.dispose();
  });

  it('до фокуса комірки - нічого; з двох сіток озброєна остання, що мала активність', () => {
    const first = mount();
    const second = mount();

    paste(document.body);
    expect(first.handler).not.toHaveBeenCalled();
    expect(second.handler).not.toHaveBeenCalled();

    first.focusCell();
    second.focusCell();
    first.focusCell();
    paste(document.body);

    expect(first.handler).toHaveBeenCalledTimes(1);
    expect(second.handler).not.toHaveBeenCalled();
    first.dispose();
    second.dispose();
  });

  it('після відписки (unmount) слухач не працює', () => {
    const m = mount();
    m.focusCell();
    m.dispose();

    paste(document.body);

    expect(m.handler).not.toHaveBeenCalled();
  });
});
