import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import { CellLegend, legendEntries } from '../CellLegend';

/**
 * ✎ DOCUX (доповнення координатора 26.11): легенда позначок комірок не розходиться з сіткою — КОЖЕН клас стану з
 * `cell-states.css` має рядок легенди, і зразок малюється ТИМИ САМИМИ класами. Новий стан без рядка — тест червоний.
 */
const css = readFileSync(path.resolve(process.cwd(), 'src/shared/theme/cell-states.css'), 'utf8');

/** Класи, що не є станом комірки: база, вирівнювання числа, база модифікаторів, підсилення того самого стану. */
const NotStates = new Set(['ecr-cell', 'ecr-cell-numeric', 'ecr-cell-needs-input', 'ecr-cell-needs-input--strong']);

const stateClasses = [...new Set([...css.matchAll(/\.(ecr-cell(?:--|-)[a-z][a-z-]*)/g)].map((match) => match[1] ?? ''))].filter(
  (name) => !NotStates.has(name),
);

describe('CellLegend', () => {
  it('кожен клас стану з cell-states.css є в легенді', () => {
    expect(stateClasses.length).toBeGreaterThanOrEqual(12);
    const used = new Set(legendEntries().flatMap((entry) => entry.classes.split(' ')));
    expect(stateClasses.filter((name) => !used.has(name))).toEqual([]);
  });

  it('зразки — ті самі класи, що ставить сітка; кожен рядок пояснено словами (ключ каталогу)', async () => {
    render(
      <MantineProvider theme={testTheme}>
        <CellLegend />
      </MantineProvider>,
    );

    await userEvent.setup().click(screen.getByRole('button', { name: /grid\.legend\.title/ }));
    await waitFor(() => expect(document.querySelectorAll('[data-legend-sample]')).toHaveLength(legendEntries().length));
    const samples = [...document.querySelectorAll<HTMLElement>('[data-legend-sample]')];
    for (const sample of samples) {
      for (const name of (sample.getAttribute('data-legend-sample') ?? '').split(' ')) expect(sample.classList.contains(name)).toBe(true);
      expect(sample.closest('.ecr-legend-row')?.querySelector('dd')?.textContent).toMatch(/grid\.legend\./);
    }
  });

  // ✎ DOCUX (живий прохід 27.11): довідка не закривалась клавішею Esc — фокус лишався на кнопці, а `closeOnEscape` Mantine
  // слухає лише всередині вікна. Мутація: прибрати `usePopoverEscape` у `CellLegend` — червоний.
  it('Esc закриває довідку і повертає фокус на кнопку', async () => {
    render(
      <MantineProvider theme={testTheme}>
        <CellLegend />
      </MantineProvider>,
    );
    const user = userEvent.setup();
    const button = screen.getByRole('button', { name: /grid\.legend\.title/ });

    await user.click(button);
    await waitFor(() => expect(document.querySelector('[data-grid-legend-list]')).not.toBeNull());
    expect(button.getAttribute('aria-expanded')).toBe('true');

    // Фокус деінде (як у сітці): Esc однаково закриває.
    button.blur();
    await user.keyboard('{Escape}');
    await waitFor(() => expect(document.querySelector('[data-grid-legend-list]')).toBeNull());
    expect(button.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(button);
  });

  it('Esc без відкритої довідки нічого не перехоплює', async () => {
    render(
      <MantineProvider theme={testTheme}>
        <CellLegend />
      </MantineProvider>,
    );
    const seen = vi.fn();
    document.addEventListener('keydown', seen);
    await userEvent.setup().keyboard('{Escape}');
    document.removeEventListener('keydown', seen);
    expect(seen).toHaveBeenCalledTimes(1);
    expect((seen.mock.calls[0]?.[0] as KeyboardEvent).defaultPrevented).toBe(false);
  });
});
