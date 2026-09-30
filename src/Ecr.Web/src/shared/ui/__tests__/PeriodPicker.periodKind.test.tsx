import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { renderWithMantine } from '@/test/render';

/**
 * `X-34`: квартал 202504 у виборі періоду підписано «April 2025», а стрілка ›
 * вела на 202505 — неіснуючий у квартальному проєкті період. `Sequence` —
 * номер періоду в році, а не місяць (`R-A6`).
 *
 * ⚠ Без `periodKind` поведінка — та сама місячна (`PeriodPicker.test.tsx`):
 * проп доданий, а не змінений.
 */

afterEach(() => {
  cleanup();
});

describe('PeriodPicker: періодичність проєкту', () => {
  it('квартальний проєкт — «четвертий квартал», а не квітень', () => {
    renderWithMantine(<PeriodPicker value={202504} onChange={vi.fn()} periodKind="Quarterly" />);

    // ⛔ Мутація «ігнорувати `periodKind`» повертає «April 2025».
    expect(screen.getByText('⟦periods.quarterOf (quarter=4, year=2025)⟧')).toBeTruthy();
    expect(screen.queryByText('April 2025')).toBeNull();
  });

  it('квартальний проєкт — › з четвертого кварталу веде в перший наступного року', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202504} onChange={onChange} periodKind="Quarterly" />);

    fireEvent.click(screen.getByRole('button', { name: '⟦period.next⟧' }));

    // ⛔ Місячний крок дав би 202505 — такого періоду в проєкті немає.
    expect(onChange).toHaveBeenCalledWith(202601);
  });

  it('квартальний проєкт — ‹ з першого кварталу веде в четвертий попереднього року', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202601} onChange={onChange} periodKind="Quarterly" />);

    fireEvent.click(screen.getByRole('button', { name: '⟦period.previous⟧' }));

    expect(onChange).toHaveBeenCalledWith(202504);
  });

  it('без periodKind — як і було, місяць', () => {
    renderWithMantine(<PeriodPicker value={202504} onChange={vi.fn()} />);

    expect(screen.getByText('April 2025')).toBeTruthy();
  });
});
