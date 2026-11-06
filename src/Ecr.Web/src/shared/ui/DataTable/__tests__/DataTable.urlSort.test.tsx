import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { JSX } from 'react';
import { renderWithMantine } from '@/test/render';
import { DataTable } from '@/shared/ui/DataTable/DataTable';
import type { DataTableColumn, SortState } from '@/shared/ui/DataTable/types';

/**
 * `ФВ-14.4`: сортування переліку живе в адресі (`urlSortParam`), а не лише в
 * локальному стані — перезавантаження, «Назад» і надіслане посилання його не гублять.
 *
 * ⛔ Мутаційний доказ (вручну, відновлено): у `UrlSortTable` замінити
 * `setRaw(formatSortParam(next, fallback))` на `setRaw(null)` — тести «адреса
 * після клацання» і «повторне монтування» червоні; прибрати перевірку
 * `isSortable`/невідомої колонки в `parseSortParam` — червоний тест про сміття.
 */
afterEach(() => cleanup());

interface Item {
  readonly code: string;
  readonly n: number;
}

const columns: DataTableColumn<Item>[] = [
  { key: 'code', label: 'Code' },
  { key: 'n', label: 'N', num: true },
  { key: 'note', label: 'Note', sortable: false, render: () => '-' },
];

const rows: Item[] = [
  { code: 'b', n: 2 },
  { code: 'c', n: 1 },
  { code: 'a', n: 3 },
];

let lastSearch = '';

function Probe(): null {
  lastSearch = useLocation().search;
  return null;
}

function Table({ defaultSort, url }: { defaultSort?: SortState | undefined; url?: boolean }): JSX.Element {
  return (
    <DataTable<Item>
      columns={columns}
      rows={rows}
      rowKey={(row) => row.code}
      defaultSort={defaultSort}
      urlSortParam={url === false ? undefined : 'sort'}
    />
  );
}

function mount(entry: string, defaultSort?: SortState): void {
  renderWithMantine(
    <MemoryRouter initialEntries={[entry]}>
      <Table defaultSort={defaultSort} />
      <Probe />
    </MemoryRouter>,
  );
}

function codes(): string[] {
  return screen
    .getAllByRole('row')
    .slice(1)
    .map((row) => row.querySelector('td')?.textContent ?? '');
}

function header(name: string): HTMLElement {
  return screen.getByRole('button', { name: new RegExp(`^${name}\\b`) });
}

describe('DataTable: сортування в адресі (ФВ-14.4)', () => {
  it('клацання пише ?sort=<key>:<dir>, третє — прибирає параметр', () => {
    mount('/list?q=x');

    fireEvent.click(header('Code'));
    expect(new URLSearchParams(lastSearch).get('sort')).toBe('code:asc');
    expect(new URLSearchParams(lastSearch).get('q')).toBe('x');
    expect(codes()).toEqual(['a', 'b', 'c']);

    fireEvent.click(header('Code'));
    expect(new URLSearchParams(lastSearch).get('sort')).toBe('code:desc');
    expect(codes()).toEqual(['c', 'b', 'a']);

    fireEvent.click(header('Code'));
    expect(new URLSearchParams(lastSearch).has('sort')).toBe(false);
    expect(codes()).toEqual(['b', 'c', 'a']);
  });

  it('повторне монтування на тій самій адресі відновлює сортування й aria-sort', () => {
    mount('/list');
    fireEvent.click(header('N'));
    fireEvent.click(header('N'));
    const saved = `/list${lastSearch}`;
    cleanup();

    mount(saved);
    expect(codes()).toEqual(['a', 'b', 'c']);
    expect(screen.getByRole('columnheader', { name: /^N\b/ }).getAttribute('aria-sort')).toBe('descending');
  });

  it.each(['code:sideways', 'missing:asc', 'note:asc', 'garbage', ':asc', ''])(
    'невідоме значення %j — defaultSort без помилки',
    (raw) => {
      mount(`/list?sort=${encodeURIComponent(raw)}`, { key: 'code', direction: 'desc' });
      expect(codes()).toEqual(['c', 'b', 'a']);
    },
  );

  it('з defaultSort: збіг прибирає параметр, «порядок сервера» пишеться як none і переживає монтування', () => {
    mount('/list', { key: 'code', direction: 'asc' });
    expect(codes()).toEqual(['a', 'b', 'c']);

    fireEvent.click(header('Code')); // asc → desc
    expect(new URLSearchParams(lastSearch).get('sort')).toBe('code:desc');

    fireEvent.click(header('Code')); // desc → порядок сервера
    expect(new URLSearchParams(lastSearch).get('sort')).toBe('none');
    expect(codes()).toEqual(['b', 'c', 'a']);

    const saved = `/list${lastSearch}`;
    cleanup();
    mount(saved, { key: 'code', direction: 'asc' });
    expect(codes()).toEqual(['b', 'c', 'a']);

    fireEvent.click(header('Code')); // назад до defaultSort — параметр зникає
    expect(new URLSearchParams(lastSearch).has('sort')).toBe(false);
  });

  it('без urlSortParam — локальний стан: адреса не змінюється, роутер не потрібен', () => {
    renderWithMantine(<Table url={false} />);
    fireEvent.click(header('Code'));
    expect(codes()).toEqual(['a', 'b', 'c']);
    cleanup();

    lastSearch = '';
    renderWithMantine(
      <MemoryRouter initialEntries={['/list?sort=code:desc']}>
        <Table url={false} />
        <Probe />
      </MemoryRouter>,
    );
    expect(codes()).toEqual(['b', 'c', 'a']);
    fireEvent.click(header('N'));
    expect(lastSearch).toBe('?sort=code:desc');
  });
});
