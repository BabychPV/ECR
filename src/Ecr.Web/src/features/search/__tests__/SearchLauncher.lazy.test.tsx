import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { createElement, type JSX } from 'react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';

/**
 * Палітра не входить у статичний бандл оболонки (бюджет `D-132`).
 *
 * ⚠ Чим це ловиться. Фабрика `vi.mock` виконується тоді, коли модуль
 * ІМПОРТУЮТЬ уперше. Статичний `import { DataSearchPalette }` у
 * `SearchLauncher.tsx` обчислив би її разом із самим лаунчером — ще до
 * рендера, — і перша перевірка нижче почервоніла б. Лінивий `import()`
 * обчислює її лише на першому `Ctrl+K`.
 *
 * ⛔ Порядок тестів у файлі значущий: модуль обчислюється один раз на файл,
 * тож перевірки «ще не обчислено» стоять перед першим відкриттям.
 */
const probe = vi.hoisted(() => ({ evaluated: false }));

vi.mock('@/features/search/DataSearchPalette', () => {
  probe.evaluated = true;

  return {
    DataSearchPalette: ({ opened }: { opened: boolean }): JSX.Element | null =>
      opened ? <div role="dialog" aria-label="palette-stub" /> : null,
  };
});

import { SearchLauncher } from '@/features/search/SearchLauncher';

afterEach(() => {
  cleanup();
});

function mount(extra?: JSX.Element): void {
  render(
    <MantineProvider theme={testTheme}>
      {extra}
      <SearchLauncher />
    </MantineProvider>,
  );
}

function pressCtrlK(target: Element | Window): boolean {
  // `fireEvent` повертає false, якщо хтось викликав preventDefault.
  return fireEvent.keyDown(target, { key: 'k', code: 'KeyK', ctrlKey: true });
}

describe('вхід у палітру з оболонки', () => {
  it('кнопка пошуку має доступну назву й оголошує гарячу клавішу', () => {
    mount();

    const button = screen.getByRole('button', { name: /search\.open/ });
    expect(button.getAttribute('aria-keyshortcuts')).toBe('Control+K Meta+K');
    expect(probe.evaluated).toBe(false);
  });

  it.each([
    ['поле вводу сітки', createElement('revo-grid', { key: 'g' }, <input aria-label="cell" />)],
    [
      'комірка сітки без вводу',
      createElement('revo-grid', { key: 'c' }, <div aria-label="cell" tabIndex={0} />),
    ],
    ['contenteditable', <div key="e" aria-label="cell" contentEditable suppressContentEditableWarning />],
    ['редактор виразів', <div key="m" className="monaco-editor"><textarea aria-label="cell" /></div>],
    ['звичайне поле', <input key="i" aria-label="cell" />],
  ])('Ctrl+K у полі «%s» не перехоплюється', (_name, field) => {
    mount(field);

    const cell = screen.getByLabelText('cell');
    const notPrevented = pressCtrlK(cell);

    expect(notPrevented).toBe(true);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(probe.evaluated).toBe(false);
  });

  it('модуль палітри не обчислено до першого відкриття; Ctrl+K його довантажує', async () => {
    mount();
    expect(probe.evaluated).toBe(false);

    const notPrevented = pressCtrlK(document.body);

    expect(notPrevented).toBe(false);
    expect(await screen.findByRole('dialog', { name: 'palette-stub' })).toBeTruthy();
    expect(probe.evaluated).toBe(true);
  });

  it('⌘K на macOS і кирилична розкладка (key «л», code KeyK) теж відкривають', async () => {
    mount();
    fireEvent.keyDown(document.body, { key: 'л', code: 'KeyK', metaKey: true });

    expect(await screen.findByRole('dialog', { name: 'palette-stub' })).toBeTruthy();
  });
});
