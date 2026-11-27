import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState, type JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';

/**
 * ✎ DOCUX (огляд UI 27.11): перші символи, набрані одразу після Ctrl+K, губилися — чанк палітри вантажиться ліниво, і поля
 * ще немає. Тепер лаунчер накопичує їх, поки поле палітри не у фокусі, і віддає полю при фокусі (`takeTyped`).
 *
 * ⚠ Заглушка палітри з'являється лише після того, як тест «довантажить чанк» (`release`), — як справжній лінивий `import()`.
 * Мутація: прибрати слухач накопичення в `SearchLauncher` — поле отримує «» замість «gas», тест червоний.
 */
const gate = vi.hoisted(() => ({ release: null as (() => void) | null }));

let SearchLauncher: typeof import('@/features/search/SearchLauncher').SearchLauncher;

function StubPalette({ opened, takeTyped }: { opened: boolean; takeTyped?: () => string }): JSX.Element | null {
  const [value, setValue] = useState('');
  if (!opened) return null;
  return (
    <div role="dialog" aria-label="palette-stub">
      <input
        aria-label="palette-input"
        data-palette-input=""
        value={value}
        onChange={(event) => setValue(event.currentTarget.value)}
        onFocus={() => setValue((current) => current + (takeTyped?.() ?? ''))}
      />
    </div>
  );
}

beforeEach(async () => {
  vi.resetModules();
  gate.release = null;
  vi.doMock('@/features/search/DataSearchPalette', async () => {
    await new Promise<void>((resolve) => {
      gate.release = resolve;
    });
    return { DataSearchPalette: StubPalette };
  });
  ({ SearchLauncher } = await import('@/features/search/SearchLauncher'));
});

afterEach(() => cleanup());

describe('Ctrl+K: набране до появи поля не губиться', () => {
  it('символи (і Backspace) між Ctrl+K і фокусом поля потрапляють у поле; у сторінку — ні', async () => {
    const outside = vi.fn();
    render(
      <MantineProvider theme={testTheme}>
        <div onKeyDown={outside}>
          <button type="button">grid</button>
        </div>
        <SearchLauncher />
      </MantineProvider>,
    );
    const grid = screen.getByRole('button', { name: 'grid' });
    grid.focus();

    fireEvent.keyDown(window, { key: 'k', ctrlKey: true });
    for (const key of ['g', 'a', 'x', 'Backspace', 's']) fireEvent.keyDown(grid, { key });
    expect(outside).not.toHaveBeenCalled();

    // Чанк «вантажиться»: фабрика модуля чекає, доки тест її відпустить.
    await vi.waitFor(() => expect(gate.release).not.toBeNull());
    await act(async () => {
      gate.release?.();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    const input = await screen.findByLabelText('palette-input');
    act(() => input.focus());

    expect((input as HTMLInputElement).value).toBe('gas');
    // Далі символи йдуть у поле звичайним шляхом — накопичувач їх не перехоплює.
    expect(fireEvent.keyDown(input, { key: 'x' })).toBe(true);
  });
});
