import { describe, expect, it } from 'vitest';
import { installEnterKeyCompat, isEnterKeyEvent } from '../keyboardCompat';

/**
 * Finding 1 (Critical, Stage 1): Enter не підтверджував правку в клітинці —
 * значення тихо губилося при навігації геть, тоді як Tab підтверджував і
 * автозберігав одразу.
 *
 * ⛔ Корінь, доведений живим RevoGrid у браузері (Chrome DevTools Protocol,
 * синтетична подія без сучасного `KeyboardEvent.key`): вбудований текстовий
 * редактор RevoGrid розпізнає Enter ЛИШЕ строгим порівнянням
 * `event.key === 'Enter'`, без запасного варіанту на legacy `keyCode`/`which`
 * — джерела вводу поза стандартною клавіатурою (термінали збору даних,
 * клавіатурні емулятори сканерів, деякі RDP/Citrix-проксі) традиційно
 * заповнюють ЛИШЕ числовий код. `keyboardCompat.ts` дописує `key` РАНІШЕ, ніж
 * RevoGrid встигає його прочитати.
 *
 * ⚠ RevoGrid у ці тести НЕ монтується (як і в `DocumentGrid.a11y-status.test.tsx`
 * — реальний веб-компонент у jsdom лише заважає): перевіряється чистий
 * предикат і сам DOM-механізм нормалізації (реальний `<input>` + реальний
 * `KeyboardEvent`, без React і без RevoGrid).
 */

describe('isEnterKeyEvent — розпізнавання Enter без сучасного `key`', () => {
  it('сучасний `key === "Enter"` — Enter', () => {
    expect(isEnterKeyEvent({ key: 'Enter' })).toBe(true);
  });

  it('порожній `key`, legacy `keyCode === 13` — теж Enter', () => {
    expect(isEnterKeyEvent({ key: '', keyCode: 13 })).toBe(true);
  });

  it('порожній `key`, legacy `which === 13` — теж Enter', () => {
    expect(isEnterKeyEvent({ key: '', which: 13 })).toBe(true);
  });

  it('порожній `key` без жодного legacy-коду — НЕ Enter (немає підстави вгадувати)', () => {
    expect(isEnterKeyEvent({ key: '' })).toBe(false);
  });

  it('заповнений `key`, який НЕ "Enter" (наприклад, "Tab") — НЕ Enter, навіть якщо keyCode=13', () => {
    // ⚠ Легасі-код розглядається ЛИШЕ коли сучасне поле відсутнє: якщо
    // джерело вводу explicit назвало клавішу інакше, довіряємо йому.
    expect(isEnterKeyEvent({ key: 'Tab', keyCode: 13 })).toBe(false);
  });

  it('legacy-код Tab (9) без сучасного `key` — НЕ Enter', () => {
    expect(isEnterKeyEvent({ key: '', keyCode: 9 })).toBe(false);
  });
});

describe('installEnterKeyCompat — нормалізація на реальному DOM (Finding 1)', () => {
  function mount(): { container: HTMLDivElement; input: HTMLInputElement } {
    const container = document.createElement('div');
    const input = document.createElement('input');
    container.appendChild(input);
    document.body.appendChild(container);
    return { container, input };
  }

  it('малформована подія Enter (порожній key, keyCode=13) доходить до `<input>` вже з key="Enter"', () => {
    const { container, input } = mount();
    const unregister = installEnterKeyCompat(container);

    const seen: string[] = [];
    input.addEventListener('keydown', (event) => seen.push(event.key));

    const event = new KeyboardEvent('keydown', { key: '', bubbles: true, cancelable: true });
    Object.defineProperty(event, 'keyCode', { value: 13, configurable: true });
    input.dispatchEvent(event);

    // ⛔ Це і є доказ Finding 1: БЕЗ нормалізації `<input>` (а отже й
    // RevoGrid-редактор всередині нього) бачить `key === ''` — саме те, на
    // чому `isEnterKeyValue` бібліотеки мовчки нічого не робить, і введене
    // губиться. З нормалізацією той самий `<input>` бачить звичайний Enter.
    expect(seen).toEqual(['Enter']);

    unregister();
    container.remove();
  });

  it('справжній Tab (key="Tab") не підмінюється на Enter', () => {
    const { container, input } = mount();
    const unregister = installEnterKeyCompat(container);

    const seen: string[] = [];
    input.addEventListener('keydown', (event) => seen.push(event.key));

    const event = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    input.dispatchEvent(event);

    expect(seen).toEqual(['Tab']);

    unregister();
    container.remove();
  });

  it('справжній Enter (key="Enter") проходить без змін', () => {
    const { container, input } = mount();
    const unregister = installEnterKeyCompat(container);

    const seen: string[] = [];
    input.addEventListener('keydown', (event) => seen.push(event.key));

    const event = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true });
    input.dispatchEvent(event);

    expect(seen).toEqual(['Enter']);

    unregister();
    container.remove();
  });

  it('відписка знімає нормалізацію — подія після неї доходить непоміненою', () => {
    const { container, input } = mount();
    const unregister = installEnterKeyCompat(container);
    unregister();

    const seen: string[] = [];
    input.addEventListener('keydown', (event) => seen.push(event.key));

    const event = new KeyboardEvent('keydown', { key: '', bubbles: true, cancelable: true });
    Object.defineProperty(event, 'keyCode', { value: 13, configurable: true });
    input.dispatchEvent(event);

    expect(seen).toEqual(['']);

    container.remove();
  });
});
