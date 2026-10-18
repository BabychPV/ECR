import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { CellColorMenu } from '../CellColorMenu';
import { StandardColours, ThemeColours, ThemeShades } from '../userCellStyle';

/**
 * CL-23 (B-9): зразки палітри мають людську назву для читалки.
 *
 * ⛔ Ім'я кнопки (`aria-label`) ЛИШАЄТЬСЯ hex: e2e (`cellColor.spec.ts`, `cellColorLive.spec.ts`) шукає
 * зразок за `getByRole('button', { name: '#c00000', exact: true })`. Назва — в `aria-description`.
 *
 * ⚠ Каталог тут не піднято, тому `t()` віддає позначений ключ: тест доводить, ЯКИЙ ключ стоїть на
 * зразку, а текст назви стереже сід (`SeedTranslationTests`/`SeedCatalogTextTests`).
 */
async function openFill(): Promise<void> {
  render(
    <MantineProvider>
      <CellColorMenu onApply={vi.fn()} disabled={false} disabledHint={null} />
    </MantineProvider>,
  );
  await userEvent.click(screen.getByTestId('cell-color-fill'));
  await screen.findByRole('button', { name: '#ffff00' });
}

describe('CellColorMenu: назви зразків', () => {
  it('ім’я зразка = hex, назва кольору - в aria-description', async () => {
    await openFill();

    const red = screen.getByRole('button', { name: '#c00000' });

    expect(red.getAttribute('aria-label')).toBe('#c00000');
    expect(red.getAttribute('aria-description')).toContain('grid.cellColor.name.darkRed');
    expect(red.getAttribute('title')).toContain('#c00000');
  });

  it('відтінок теми називає і базовий колір, і ступінь', async () => {
    await openFill();

    // Перший відтінок білого: світліший на 80 %; hex не збігається з жодним зразком іншого рядка.
    const hex = ThemeShades[0]?.[4] ?? '';
    const swatch = screen.getByRole('button', { name: hex });
    const description = swatch.getAttribute('aria-description') ?? '';

    expect(description).toContain('grid.cellColor.name.blue');
    expect(description).toContain('grid.cellColor.shade.lighter80');
  });

  it('кожен зразок палітри має назву', async () => {
    await openFill();

    const all = new Set<string>([...ThemeColours, ...StandardColours, ...ThemeShades.flat()]);

    for (const hex of all) {
      for (const swatch of screen.getAllByRole('button', { name: hex })) {
        expect(swatch.getAttribute('aria-description') ?? '').not.toBe('');
      }
    }
  });
});
