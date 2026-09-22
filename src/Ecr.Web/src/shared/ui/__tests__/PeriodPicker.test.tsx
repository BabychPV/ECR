import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { renderWithMantine } from '@/test/render';

/**
 * `PeriodPicker` (UI-06, `docs/build/DIRECTIVE-15-FRONTEND.md:129`; той самий
 * пункт — `DIRECTIVE-14-UIUX.md` U.3).
 *
 * ⛔ Замінює `NumberInput` із написом `Period: 202609` у `DocumentsPage` і
 * `DocumentPage` (`DIRECTIVE-14-UIUX.md:110-112`). Перевіряється ПОВЕДІНКА:
 * (1) пряме введення `periodKey` працює так само, як у старого поля — той
 * самий підпис `documents.period`, той самий формат числа `YYYYMM`;
 * (2) стрілки крокують КАЛЕНДАРЕМ, а не `periodKey ± 1` (`R-A6`) — грудень
 * веде в січень наступного року, не в невалідний `…13`.
 */

afterEach(() => {
  cleanup();
});

describe('PeriodPicker: пряме введення periodKey — те саме поле, що й раніше', () => {
  it('має підпис `documents.period`, як замінений NumberInput', async () => {
    renderWithMantine(<PeriodPicker value={202609} onChange={vi.fn()} />);

    expect(await screen.findByLabelText('⟦documents.period⟧')).toBeTruthy();
  });

  it('дозволяє передати власний підпис (`label`)', () => {
    renderWithMantine(<PeriodPicker value={202609} onChange={vi.fn()} label="Custom" />);

    expect(screen.getByLabelText('Custom')).toBeTruthy();
  });

  it('уведене число потрапляє в onChange так само, як у старому полі', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202512} onChange={onChange} />);

    fireEvent.change(screen.getByLabelText('⟦documents.period⟧'), {
      target: { value: '202601' },
    });

    expect(onChange).toHaveBeenCalledWith(202601);
  });

  it('порожнє поле дає `null`, а не старе значення — виклик сам вирішує, що робити далі', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202609} onChange={onChange} />);

    fireEvent.change(screen.getByLabelText('⟦documents.period⟧'), { target: { value: '' } });

    expect(onChange).toHaveBeenCalledWith(null);
  });

  it('показує підпис періоду мовою інтерфейсу під полем', () => {
    renderWithMantine(<PeriodPicker value={202609} onChange={vi.fn()} />);

    expect(screen.getByText('September 2026')).toBeTruthy();
  });
});

describe('PeriodPicker: стрілки крокують КАЛЕНДАРЕМ (R-A6), не арифметикою periodKey', () => {
  it(
    'грудень → «вперед» → січень НАСТУПНОГО року (не 202513)',
    () => {
      const onChange = vi.fn();
      renderWithMantine(<PeriodPicker value={202512} onChange={onChange} />);

      fireEvent.click(screen.getByRole('button', { name: '⟦period.next⟧' }));

      /*
       * ⛔ Мутаційний доказ: поверніть `shiftPeriod` до `value + delta` —
       * клік дасть `onChange(202513)` (невалідний periodKey), і цей рядок
       * почервоніє. Саме цей дефект описує DIRECTIVE-14-UIUX.md:110-112 і
       * забороняє R-A6.
       */
      expect(onChange).toHaveBeenCalledWith(202601);
      expect(onChange).not.toHaveBeenCalledWith(202513);
    },
  );

  it('січень → «назад» → грудень ПОПЕРЕДНЬОГО року (не 202600)', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202601} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦period.previous⟧' }));

    expect(onChange).toHaveBeenCalledWith(202512);
    expect(onChange).not.toHaveBeenCalledWith(202600);
  });

  it('звичайний крок усередині року — просто сусідній місяць', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202605} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦period.next⟧' }));
    expect(onChange).toHaveBeenCalledWith(202606);
  });

  it('період не обрано (`null`) — обидві стрілки вимкнені: крокувати нізвідки', () => {
    renderWithMantine(<PeriodPicker value={null} onChange={vi.fn()} />);

    expect(screen.getByRole('button', { name: '⟦period.previous⟧' })).toHaveProperty(
      'disabled',
      true,
    );
    expect(screen.getByRole('button', { name: '⟦period.next⟧' })).toHaveProperty('disabled', true);
  });

  it('клік по вимкненій стрілці не викликає onChange', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={null} onChange={onChange} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦period.next⟧' }));

    expect(onChange).not.toHaveBeenCalled();
  });

  it('`disabled` вимикає й стрілки, і поле вводу', () => {
    renderWithMantine(<PeriodPicker value={202609} onChange={vi.fn()} disabled />);

    expect(screen.getByRole('button', { name: '⟦period.previous⟧' })).toHaveProperty(
      'disabled',
      true,
    );
    expect(screen.getByRole('button', { name: '⟦period.next⟧' })).toHaveProperty('disabled', true);
    expect(screen.getByLabelText('⟦documents.period⟧')).toHaveProperty('disabled', true);
  });
});

/**
 * Корінь дефекту `e2e/keyboardPath.spec.ts:39` (прогін 2026-09-22, 28/29,
 * `keyboardPath.spec.ts:101`): `page.getByLabel(/Period|Період/i).first()`
 * резолвиться НЕ в поле вводу, а в кнопку «‹» (`period.previous`). Обидва
 * aria-label стрілок теж містять підрядок «period» («Previous period»/«Next
 * period», `Sql/09-seed.sql:1214-1215`), а тест вище (рядок 100-108) уже
 * доводить: коли `value === null` (документи одразу після входу, період ще
 * не обрано), ОБИДВІ стрілки вимкнені. Вимкнений елемент не є «focusable
 * area» (HTML-специфікація) — `target.focus()` на ньому фокус НЕ переставляє,
 * і перевірка `expectFocusRing` падала «фокус на <body>»: не тому, що поле
 * недоступне з клавіатури, а тому що локатор резолвився не в поле.
 *
 * Той самий клас дефекту вже описаний у файлі для `Password`
 * (`e2e/keyboardPath.spec.ts:256-261`): кнопка-тумблер видимості пароля теж
 * мала aria-label із підрядком «password». Фікс той самий: звузити пошук до
 * ролі `textbox` — `NumberInput` рендерить `<input type="text">`
 * (роль `textbox`), `ActionIcon` — `<button>` (роль `button`), тож роль
 * однозначно відкидає обидві стрілки.
 */
describe('PeriodPicker: e2e/keyboardPath.spec.ts:101 — локатор поля не має плутати його зі стрілками', () => {
  it('getAllByLabelText(/period/i) без ролі повертає ТРИ елементи, коли період не обрано', () => {
    renderWithMantine(<PeriodPicker value={null} onChange={vi.fn()} />);

    // ⛔ Це й є пастка, у яку впав старий e2e-локатор: три елементи, а не
    // один, — обидві стрілки й поле водночас підпадають під /period/i.
    // ⚠ Порядок цього масиву НЕ відтворює порядок DOM (RTL спершу повертає
    // елементи, знайдені через асоціацію з `<label>`, і лише потім —
    // знайдені через `aria-label`), тому індекс `[0]` тут навмисно не
    // перевіряється: наступний тест бере справжній DOM-порядок напряму.
    expect(screen.getAllByLabelText(/period/i)).toHaveLength(3);
  });

  it('DOM-порядок: кнопка «previous» стоїть у документі ПЕРЕД полем — так само, як бачить Playwright getByLabel().first()', () => {
    renderWithMantine(<PeriodPicker value={null} onChange={vi.fn()} />);

    const previousButton = screen.getByRole('button', { name: '⟦period.previous⟧' });
    const field = screen.getByRole('textbox', { name: /period/i });

    // `DOCUMENT_POSITION_FOLLOWING`: `field` йде ПІСЛЯ `previousButton` у
    // документі — саме в такому порядку Playwright обходить DOM для
    // `getByLabel(...).first()` (на відміну від `getAllByLabelText` вище,
    // Playwright не розрізняє джерело accessible name — лише порядок DOM).
    const fieldFollowsButton = Boolean(
      previousButton.compareDocumentPosition(field) & Node.DOCUMENT_POSITION_FOLLOWING,
    );
    expect(fieldFollowsButton).toBe(true);
  });

  it('мутаційний доказ: перший у DOM-порядку елемент — вимкнена кнопка «previous», і вона НЕ приймає programmatic-фокус', () => {
    renderWithMantine(<PeriodPicker value={null} onChange={vi.fn()} />);

    // Те, у що фактично резолвиться `page.getByLabel(/Period|Період/i).first()`
    // до фіксу: перший елемент з підрядком «period» у мітці за DOM-порядком.
    const previousButton = screen.getByRole('button', { name: '⟦period.previous⟧' });
    expect(previousButton).toHaveProperty('disabled', true);

    expect(document.activeElement).toBe(document.body);
    previousButton.focus();

    // ⛔ Рядок, що ловить регресію самого припущення тесту: якби стрілку
    // випадково зробили фокусованою (прибрали `disabled` без зміни причини
    // дефекту), `.focus()` спрацював би, `activeElement` змінився б на
    // `previousButton`, і це порівняння почервоніло б — сигналізуючи, що
    // сценарій вище вже не відтворює те, що зловив e2e ("фокус на <body>").
    expect(document.activeElement).toBe(document.body);
    expect(document.activeElement).not.toBe(previousButton);
  });

  it('фікс: getByRole("textbox", {name: /period/i}) однозначно резолвиться в поле і приймає фокус', () => {
    renderWithMantine(<PeriodPicker value={null} onChange={vi.fn()} />);

    const field = screen.getByRole('textbox', { name: /period/i });
    expect(field).toBe(screen.getByLabelText('⟦documents.period⟧'));

    expect(document.activeElement).not.toBe(field);
    field.focus();

    // ⛔ Мутаційний доказ: поверніть локатор `e2e/keyboardPath.spec.ts:101`
    // до `getByLabel(...).first()` — тест вище («мутаційний доказ: перший у
    // DOM-порядку...») прямо показує, чому це червоніє: перший елемент за
    // DOM-порядком — вимкнена кнопка «previous», а не це поле, і фокус на
    // нього programmatically ніколи не потрапляє.
    expect(document.activeElement).toBe(field);
  });
});
