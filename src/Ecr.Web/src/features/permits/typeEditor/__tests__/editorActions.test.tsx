import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import {
  EditorDialogIds,
  TypeEditorActionsContext,
  readDialog,
  useEditorActions,
  withDialog,
  withoutDialog,
  type EditorActions,
} from '../editorActions';

/**
 * Точка підключення діалогів PM-08c.
 *
 * Мутаційний доказ (виконано вручну, відновлено): у `withDialog` прибране `next.delete(target)` валить
 * «відкриття без таблиці прибирає чужу ціль»; у `readDialog` прибрана перевірка переліку валить
 * «невідомий ідентифікатор».
 */

afterEach(cleanup);

const methods: { label: string; id: string; call: (a: EditorActions) => void }[] = [
  { label: 'openAddTable', id: 'add-table', call: (a) => a.openAddTable() },
  { label: 'openAddColumn', id: 'add-column', call: (a) => a.openAddColumn() },
  { label: 'openAddSeries', id: 'add-series', call: (a) => a.openAddSeries() },
  { label: 'openDeleteTable', id: 'delete-table', call: (a) => a.openDeleteTable('Storage') },
  { label: 'openAddRule', id: 'add-rule', call: (a) => a.openAddRule() },
  { label: 'openPublish', id: 'publish-type', call: (a) => a.openPublish() },
  { label: 'openNewVersion', id: 'new-version', call: (a) => a.openNewVersion() },
  { label: 'openDeleteType', id: 'delete-type', call: (a) => a.openDeleteType() },
];

function Probe({ call }: { call: (a: EditorActions) => void }): JSX.Element {
  const actions = useEditorActions();
  const location = useLocation();
  return (
    <>
      <button onClick={() => call(actions)}>go</button>
      <output data-testid="search">{location.search}</output>
    </>
  );
}

describe('типові дії: ставлять ?dialog= і зберігають решту адреси', () => {
  it.each(methods)('$label → dialog=$id', ({ id, call }) => {
    render(
      <MemoryRouter initialEntries={['/x?tab=structure&t=Discharge&panel=Annual']}>
        <Probe call={call} />
      </MemoryRouter>,
    );
    fireEvent.click(screen.getByText('go'));
    const params = new URLSearchParams(screen.getByTestId('search').textContent ?? '');
    expect(params.get('dialog')).toBe(id);
    expect(params.get('tab')).toBe('structure');
    expect(params.get('t')).toBe('Discharge');
    expect(params.get('panel')).toBe('Annual');
    expect(params.get('table')).toBe(id === 'delete-table' ? 'Storage' : null);
  });

  it('усі вісім ідентифікаторів покриті', () => {
    expect(methods.map((m) => m.id).sort()).toEqual([...EditorDialogIds].sort());
  });
});

describe('провайдер PM-08c підміняє типову реалізацію', () => {
  it('кличе надані дії, адреса не змінюється', () => {
    const provided = { openAddTable: vi.fn() } as unknown as EditorActions;
    render(
      <MemoryRouter initialEntries={['/x']}>
        <TypeEditorActionsContext.Provider value={provided}>
          <Probe call={(a) => a.openAddTable()} />
        </TypeEditorActionsContext.Provider>
      </MemoryRouter>,
    );
    fireEvent.click(screen.getByText('go'));
    expect(provided.openAddTable).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('search').textContent).toBe('');
  });
});

describe('читання й зняття діалогу', () => {
  it('readDialog повертає ідентифікатор і ціль', () => {
    expect(readDialog(new URLSearchParams('dialog=delete-table&table=Storage'))).toEqual({ id: 'delete-table', tableCode: 'Storage' });
    expect(readDialog(new URLSearchParams('dialog=add-table'))).toEqual({ id: 'add-table', tableCode: null });
    expect(readDialog(new URLSearchParams(''))).toBeNull();
  });

  it('невідомий ідентифікатор — немає діалогу', () => {
    expect(readDialog(new URLSearchParams('dialog=hack'))).toBeNull();
  });

  it('відкриття без таблиці прибирає чужу ціль', () => {
    const next = withDialog(new URLSearchParams('dialog=delete-table&table=Storage'), 'add-column');
    expect(next.get('table')).toBeNull();
    expect(next.get('dialog')).toBe('add-column');
  });

  it('withoutDialog знімає dialog і ціль, решту лишає', () => {
    const next = withoutDialog(new URLSearchParams('tab=rules&dialog=delete-table&table=Storage&panel=A'));
    expect(next.toString()).toBe('tab=rules&panel=A');
  });
});
