import { afterEach, describe, expect, it, vi } from 'vitest';
import { focusedCell, publishFocus, resetFocus, subscribeFocus } from '../focusStore';

/**
 * Сховище комірки фокуса (`UI-08`).
 *
 * ⛔ Воно існує рівно для того, щоб рух курсора НЕ перемальовував сітку:
 * `DocumentGrid` на кожному своєму рендері перебудовує опис колонок
 * (`gridColumns`, до 60 замикань) і модель рядків. Тому тут перевіряється не
 * «значення зберігається», а два конкретні правила, без яких сховище коштувало
 * б дорожче за стан: ключ — зріз, і повтор не будить нікого.
 */

afterEach(() => {
  resetFocus();
});

describe('focusStore', () => {
  it('фокус належить ЗРІЗУ, а не модулю', () => {
    /*
     * ⛔ На аркуші одночасно змонтовано кілька сіток (`SheetTables.tsx`
     * монтує їх за прокруткою). Спільний фокус на весь модуль означав би, що
     * клік в одній таблиці посуває рядок формули в усіх.
     */
    publishFocus(1, 202609, { rowIndex: 3, columnIndex: 2 });

    expect(focusedCell(1, 202609)).toEqual({ rowIndex: 3, columnIndex: 2 });
    expect(focusedCell(2, 202609)).toBeNull();

    // ⚠ Той самий екземпляр таблиці в ІНШОМУ періоді — теж інший зріз.
    expect(focusedCell(1, 202512)).toBeNull();
  });

  it('повторна публікація тієї самої комірки нікого не будить', () => {
    /*
     * ⛔ RevoGrid шле `focuscell` і тоді, коли координати не змінилися:
     * повторний клік у ту саму клітинку, вихід із редактора. Без цієї
     * перевірки кожне таке сповіщення перемальовувало б рядок формули без
     * жодної зміни на екрані.
     */
    const listener = vi.fn();
    const stop = subscribeFocus(listener);

    publishFocus(1, 202609, { rowIndex: 0, columnIndex: 0 });
    expect(listener).toHaveBeenCalledTimes(1);

    publishFocus(1, 202609, { rowIndex: 0, columnIndex: 0 });
    expect(listener).toHaveBeenCalledTimes(1);

    publishFocus(1, 202609, { rowIndex: 0, columnIndex: 1 });
    expect(listener).toHaveBeenCalledTimes(2);

    stop();
  });

  it('скидання фокуса зрізу теж сповіщає — і лише один раз', () => {
    const listener = vi.fn();
    const stop = subscribeFocus(listener);

    publishFocus(1, 202609, { rowIndex: 0, columnIndex: 0 });
    publishFocus(1, 202609, null);

    expect(focusedCell(1, 202609)).toBeNull();
    expect(listener).toHaveBeenCalledTimes(2);

    // ⚠ Скидання вже скинутого — не подія.
    publishFocus(1, 202609, null);
    expect(listener).toHaveBeenCalledTimes(2);

    stop();
  });

  it('після відписки слухача не чіпають — інакше знята сітка лишала б витік', () => {
    const listener = vi.fn();
    subscribeFocus(listener)();

    publishFocus(1, 202609, { rowIndex: 1, columnIndex: 1 });

    expect(listener).not.toHaveBeenCalled();
  });

  it('знімок СТАБІЛЬНИЙ за посиланням, доки фокус не змінювався', () => {
    // ⛔ `useSyncExternalStore` порівнює знімки за посиланням: новий об'єкт
    // на кожен виклик дає «getSnapshot should be cached» і нескінченне
    // перемальовування.
    publishFocus(1, 202609, { rowIndex: 2, columnIndex: 2 });

    expect(focusedCell(1, 202609)).toBe(focusedCell(1, 202609));
  });
});
