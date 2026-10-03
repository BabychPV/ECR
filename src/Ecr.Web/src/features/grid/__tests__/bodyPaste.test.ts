import { afterEach, describe, expect, it, vi } from 'vitest';
import { installBodyPasteRedirect } from '../bodyPaste';

/**
 * T4-02 (P1): після закриття редактора фокус на `<body>`, `paste` іде в абзац
 * поза сіткою, React-`onPaste` обгортки його не бачить — вставка мовчки не
 * відбувається. Живий RevoGrid відтворено в Chromium (див. `bodyPaste.ts`);
 * тут — контракт слухача на `document`.
 */
function mount(): { container: HTMLElement; focusCell: () => void } {
  const container = document.createElement('div');
  container.appendChild(document.createElement('revo-grid'));
  document.body.appendChild(container);

  return { container, focusCell: () => container.dispatchEvent(new CustomEvent('focuscell')) };
}

function paste(target: EventTarget): Event {
  const event = new Event('paste', { bubbles: true, cancelable: true });
  target.dispatchEvent(event);

  return event;
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('installBodyPasteRedirect (T4-02)', () => {
  it('paste у абзац поза сіткою (фокус на body) після фокуса комірки йде в обробник сітки', () => {
    const { container, focusCell } = mount();
    const handler = vi.fn();
    const dispose = installBodyPasteRedirect(container, handler);
    const paragraph = document.createElement('p');
    document.body.appendChild(paragraph);
    focusCell();

    paste(paragraph);
    paste(document.body);

    expect(handler).toHaveBeenCalledTimes(2);
    dispose();
  });

  it('paste у input/textarea/select/contenteditable поза сіткою НЕ перехоплюється', () => {
    const { container, focusCell } = mount();
    const handler = vi.fn();
    const dispose = installBodyPasteRedirect(container, handler);
    const targets = ['input', 'textarea', 'select'].map((tag) => document.body.appendChild(document.createElement(tag)));
    const editable = document.body.appendChild(document.createElement('div'));
    editable.setAttribute('contenteditable', 'true');
    focusCell();

    for (const target of [...targets, editable]) paste(target);

    expect(handler).not.toHaveBeenCalled();
    dispose();
  });

  it('paste, коли фокус у чужому полі (ціль — абзац), НЕ перехоплюється', () => {
    const { container, focusCell } = mount();
    const handler = vi.fn();
    const dispose = installBodyPasteRedirect(container, handler);
    const input = document.body.appendChild(document.createElement('input'));
    const paragraph = document.body.appendChild(document.createElement('p'));
    focusCell();
    input.focus();

    paste(paragraph);

    expect(handler).not.toHaveBeenCalled();
    dispose();
  });

  it('фокус усередині сітки, а paste (за виділенням) іде в абзац поза нею - перехоплюється', () => {
    const { container, focusCell } = mount();
    const handler = vi.fn();
    const dispose = installBodyPasteRedirect(container, handler);
    const holder = container.appendChild(document.createElement('div'));
    holder.tabIndex = 0;
    const paragraph = document.body.appendChild(document.createElement('p'));
    focusCell();
    holder.focus();

    paste(paragraph);

    expect(handler).toHaveBeenCalledTimes(1);
    dispose();
  });

  it('відкрита модалка: paste не перехоплюється', () => {
    const { container, focusCell } = mount();
    const handler = vi.fn();
    const dispose = installBodyPasteRedirect(container, handler);
    const dialog = document.body.appendChild(document.createElement('div'));
    dialog.setAttribute('role', 'dialog');
    focusCell();

    paste(document.body);

    expect(handler).not.toHaveBeenCalled();
    dispose();
  });

  it('paste всередині самої сітки не дублюється (його бачить React-onPaste)', () => {
    const { container, focusCell } = mount();
    const handler = vi.fn();
    const dispose = installBodyPasteRedirect(container, handler);
    focusCell();

    paste(container.firstElementChild as Element);

    expect(handler).not.toHaveBeenCalled();
    dispose();
  });

  it('до першого фокуса комірки й для неактивної сітки — нічого; активна — остання, що мала фокус', () => {
    const first = mount();
    const second = mount();
    const firstHandler = vi.fn();
    const secondHandler = vi.fn();
    const disposeFirst = installBodyPasteRedirect(first.container, firstHandler);
    const disposeSecond = installBodyPasteRedirect(second.container, secondHandler);

    paste(document.body);
    expect(firstHandler).not.toHaveBeenCalled();
    expect(secondHandler).not.toHaveBeenCalled();

    first.focusCell();
    second.focusCell();
    first.focusCell();
    paste(document.body);

    expect(firstHandler).toHaveBeenCalledTimes(1);
    expect(secondHandler).not.toHaveBeenCalled();
    disposeFirst();
    disposeSecond();
  });

  it('після відписки слухач не працює', () => {
    const { container, focusCell } = mount();
    const handler = vi.fn();
    installBodyPasteRedirect(container, handler)();
    focusCell();

    paste(document.body);

    expect(handler).not.toHaveBeenCalled();
  });
});
