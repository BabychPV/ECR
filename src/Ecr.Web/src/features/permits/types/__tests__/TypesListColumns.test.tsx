import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { testTheme } from '@/test/render';
import type { PermitTypeListItem } from '../../api/types';
import { typeColumns } from '../TypesListColumns';
import { makeType } from './typesFixture';

/**
 * Меню рядка переліку типів «⋯» (PA-U-02, PA2-U-04): пункт «Delete type…» активний лише для чернетки; опублікований -
 * `aria-disabled`, але досяжний; Esc повертає фокус на «⋯»; клік по кнопці й пунктах не доходить до рядка (там перехід).
 *
 * Мутаційні докази (вручну, відновлено):
 * - прибрати `event.stopPropagation()` у `onClick` кнопки «⋯» валить «клік по «⋯» не відкриває рядок»;
 * - прибрати РАЗОМ `onClick` з `Menu.Dropdown` і `stopPropagation` пункту валить обидва «клік по пункту не відкриває рядок»
 *   (кожне окремо не валить: вони дублюють одне одного - навмисна подвійна страховка);
 * - `blocked` завжди `false` валить «опублікований: aria-disabled, onDelete не викликається»;
 * - `blocked` завжди `true` валить «чернетка: пункт активний, onDelete(item)»;
 * ⚠ Тест «Esc повертає фокус на «⋯»» - регресійний: `returnFocus={false}` на `Menu` його НЕ валить (мутацію перевірено, зелений) -
 * у jsdom фокус на кнопку повертає Mantine через Target; доказом лишається e2e стенда.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
  language: () => 'en',
}));

const draft = makeType('WasteStorage', { status: 'Draft', hasUnpublishedChanges: true, version: 1 });
const published = makeType('AirEmission', { version: 3 });

function renderMenu(item: PermitTypeListItem, onDelete: (item: PermitTypeListItem) => void, onRow: () => void): void {
  const actions = typeColumns(onDelete).find((column) => column.key === 'actions');
  if (actions === undefined) throw new Error('колонки дій немає');
  const cell: JSX.Element = <>{actions.render?.(item)}</>;

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter>
        {/* Як рядок DataTable: клік по рядку веде в редактор. */}
        <div data-testid="row" onClick={onRow}>
          {cell}
        </div>
      </MemoryRouter>
    </MantineProvider>,
  );
}

const menuButton = (code: string): HTMLElement => document.querySelector<HTMLElement>(`[data-type-menu="${code}"]`)!;

afterEach(() => cleanup());

describe('TypesListColumns: меню рядка «⋯»', () => {
  it('чернетка: пункт активний, клік викликає onDelete(item)', async () => {
    const onDelete = vi.fn();
    renderMenu(draft, onDelete, () => undefined);

    fireEvent.click(menuButton('WasteStorage'));
    const item = await screen.findByRole('menuitem', { name: 'permits.editor.actions.deleteType' });
    expect(item.getAttribute('aria-disabled')).toBeNull();
    fireEvent.click(item);

    expect(onDelete).toHaveBeenCalledTimes(1);
    expect(onDelete).toHaveBeenCalledWith(draft);
  });

  it('опублікований: aria-disabled, причина в aria-describedby, onDelete не викликається', async () => {
    const onDelete = vi.fn();
    renderMenu(published, onDelete, () => undefined);

    fireEvent.click(menuButton('AirEmission'));
    const item = await screen.findByRole('menuitem', { name: /permits\.editor\.actions\.deleteType/ });
    expect(item.getAttribute('aria-disabled')).toBe('true');
    expect(item.hasAttribute('disabled')).toBe(false);
    expect(document.getElementById(item.getAttribute('aria-describedby') ?? '')?.textContent).toBe(
      'permits.editor.dialogs.deleteType.published',
    );
    fireEvent.click(item);

    expect(onDelete).not.toHaveBeenCalled();
  });

  it('Esc повертає фокус на «⋯», а не на BODY', async () => {
    renderMenu(draft, vi.fn(), () => undefined);
    const button = menuButton('WasteStorage');
    button.focus();

    fireEvent.click(button);
    const item = await screen.findByRole('menuitem', { name: 'permits.editor.actions.deleteType' });
    item.focus();
    fireEvent.keyDown(item, { key: 'Escape' });

    await waitFor(() => expect(screen.queryByRole('menuitem')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(button));
  });

  it('клік по «⋯» не відкриває рядок', () => {
    const onRow = vi.fn();
    renderMenu(draft, vi.fn(), onRow);

    fireEvent.click(menuButton('WasteStorage'));

    expect(onRow).not.toHaveBeenCalled();
  });

  for (const [label, item] of [
    ['активний пункт', draft],
    ['вимкнений пункт', published],
  ] as const) {
    it(`клік по пункту не відкриває рядок (${label})`, async () => {
      const onRow = vi.fn();
      renderMenu(item, vi.fn(), onRow);

      fireEvent.click(menuButton(item.code));
      fireEvent.click(await screen.findByRole('menuitem', { name: /permits\.editor\.actions\.deleteType/ }));

      expect(onRow).not.toHaveBeenCalled();
    });
  }
});
