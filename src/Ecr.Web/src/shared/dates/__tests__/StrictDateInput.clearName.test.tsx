import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { StrictDateInput } from '@/shared/dates/StrictDateInput';

/**
 * Кнопка очищення поля дати мала порожнє доступне ім'я — axe `button-name` на /admin/audit (темна
 * тема): читалка казала «кнопка», а поруч їх дві («From», «To»). Ім'я несе підпис поля.
 */
afterEach(() => cleanup());

describe('StrictDateInput: ім\'я кнопки очищення', () => {
  it('кнопка очищення називає поле', () => {
    renderWithMantine(<StrictDateInput label="From" clearable value={new Date('2026-10-05T00:00:00')} onChange={() => {}} />);

    // ⛔ Мутація «прибрати clearButtonProps» — кнопка без імені, запит не знаходить її.
    expect(screen.getByRole('button', { name: '⟦dates.clearNamed (field=From)⟧' })).toBeTruthy();
  });
});
