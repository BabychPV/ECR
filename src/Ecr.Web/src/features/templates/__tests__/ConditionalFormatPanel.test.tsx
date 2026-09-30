import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConditionalFormatPanel } from '../ConditionalFormatPanel';
import { renderWithMantine } from '@/test/render';

/**
 * Редактор умовного форматування (`ФВ-2.7`) у вимкненому стані збереження.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): зняти `disabled` з
 * кнопки збереження — червоніє «збереження вимкнене»; у перегляді замість
 * `firstMatchingRule` поставити `null` — червоніє «перегляд застосовує
 * правило».
 */
const columns = [
  { code: 'Q', label: 'Quantity' },
  { code: 'N', label: 'Note' },
];

describe('ConditionalFormatPanel', () => {
  it('збереження вимкнене й пояснене; причина — на видноті', () => {
    renderWithMantine(<ConditionalFormatPanel columns={columns} />);

    const save = screen.getByRole('button', { name: /conditionalFormat\.save/ });
    expect((save as HTMLButtonElement).disabled).toBe(true);
    expect((save)?.getAttribute('aria-describedby')).toBe('conditional-format-save-hint');
    expect(screen.getByText(/conditionalFormat\.unavailable(?!Title)/)).toBeTruthy();
  });

  it('перегляд застосовує правило до значення-прикладу', async () => {
    renderWithMantine(<ConditionalFormatPanel columns={columns} />);
    const user = userEvent.setup();

    await user.type(screen.getByRole('textbox', { name: /conditionalFormat\.value(?!To)/ }), '100');
    await user.click(screen.getByRole('switch', { name: /styles\.bold/ }));

    const preview = document.querySelector('[data-conditional-preview]');
    await user.type(screen.getByRole('textbox', { name: /conditionalFormat\.sample/ }), '150');
    expect((preview)?.getAttribute('data-conditional-preview')).toBe('match');
    expect((preview as HTMLElement | null)?.style.fontWeight).toBe('700');

    await user.clear(screen.getByRole('textbox', { name: /conditionalFormat\.sample/ }));
    await user.type(screen.getByRole('textbox', { name: /conditionalFormat\.sample/ }), '50');
    expect((preview)?.getAttribute('data-conditional-preview')).toBe('none');
  });

  it('правило додається й видаляється', async () => {
    renderWithMantine(<ConditionalFormatPanel columns={columns} />);
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.add/ }));
    expect(screen.getAllByRole('group', { name: /conditionalFormat\.rule/ })).toHaveLength(2);

    await user.click(screen.getByRole('button', { name: /conditionalFormat\.remove.*1/ }));
    expect(screen.getAllByRole('group', { name: /conditionalFormat\.rule/ })).toHaveLength(1);
  });
});
