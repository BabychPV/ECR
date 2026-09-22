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
