import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { renderWithMantine } from '@/test/render';

/**
 * PERIODNAV: стрілка ‹ › ходить по ПЕРІОДАХ ПРОЄКТУ й вимикається на першому/останньому
 * (раніше з грудня 2026 вела в 202701, якого в проєкті немає -> `ECR-PRD-0422` і глухий кут).
 */

afterEach(() => {
  cleanup();
});

const next = (): HTMLButtonElement => screen.getByRole('button', { name: '⟦period.next⟧' });
const previous = (): HTMLButtonElement => screen.getByRole('button', { name: '⟦period.previous⟧' });

describe('PeriodPicker: періоди проєкту', () => {
  const project = [202611, 202612];

  it('на останньому періоді › вимкнена й не викликає onChange', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202612} onChange={onChange} periods={project} />);

    // ⛔ Мутація «ігнорувати periods» дає 202701 і кнопка активна.
    expect(next().disabled).toBe(true);
    expect(previous().disabled).toBe(false);
    fireEvent.click(next());
    expect(onChange).not.toHaveBeenCalled();
  });

  it('на першому періоді ‹ вимкнена', () => {
    renderWithMantine(<PeriodPicker value={202611} onChange={vi.fn()} periods={project} />);

    expect(previous().disabled).toBe(true);
    expect(next().disabled).toBe(false);
  });

  it('посередині обидві активні, крок - сусід у переліку', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202612} onChange={onChange} periods={[202610, 202612, 202703]} />);

    fireEvent.click(previous());
    expect(onChange).toHaveBeenCalledWith(202610);
    expect(next().disabled).toBe(false);
  });

  it('єдиний період: обидві вимкнені', () => {
    renderWithMantine(<PeriodPicker value={202612} onChange={vi.fn()} periods={[202612]} />);

    expect(previous().disabled).toBe(true);
    expect(next().disabled).toBe(true);
  });

  it('набраний ключ поза проєктом не йде в onChange', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202612} onChange={onChange} periods={project} />);

    const input = screen.getByRole('textbox');
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: '202701' } });
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.change(input, { target: { value: '202611' } });
    expect(onChange).toHaveBeenCalledWith(202611);
  });

  it('сітка вимикає місяці поза проєктом', async () => {
    renderWithMantine(<PeriodPicker value={202612} onChange={vi.fn()} periods={project} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦period.choose⟧' }));
    await waitFor(() => expect(screen.getByTestId('period-grid')).toBeTruthy());
    const grid = screen.getByTestId('period-grid');
    const button = (key: number): HTMLButtonElement =>
      grid.querySelector<HTMLButtonElement>(`[data-period-key="${String(key)}"]`) as HTMLButtonElement;

    expect(button(202612).disabled).toBe(false);
    expect(button(202611).disabled).toBe(false);
    expect(button(202610).disabled).toBe(true);
  });

  it('без periods - як було (календарний крок)', () => {
    const onChange = vi.fn();
    renderWithMantine(<PeriodPicker value={202612} onChange={onChange} />);

    fireEvent.click(next());
    expect(onChange).toHaveBeenCalledWith(202701);
  });
});
