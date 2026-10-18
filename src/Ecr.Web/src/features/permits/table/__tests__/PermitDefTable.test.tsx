import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { PermitDefTable, type PermitDefTableProps } from '../PermitDefTable';
import { dischargeColumns, dischargeRows } from '../../__tests__/dischargeFixture';
import type { PermitColumn, PermitRow } from '../../lib/columns';

/**
 * Широка таблиця перміта (PM-06): 31-стовпцева «Discharge volumes» з макета.
 *
 * Що доводиться: групи згорнуті за замовчуванням у широкій таблиці й розгортаються кнопкою
 * (`aria-expanded`); перемикач «Grouped / All columns» є лише в широкій; обовʼязкове порожнє
 * позначене словом; підсумки, попередження, клік/клавіатура на рядку; перший стовпець — заголовок
 * рядка.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? `text of ${key}` : `text of ${key} ${Object.values(params).join(' ')}`,
}));

afterEach(cleanup);

function show(props: Partial<PermitDefTableProps> = {}): void {
  render(
    <MantineProvider>
      <PermitDefTable columns={dischargeColumns} rows={dischargeRows} caption="Discharge volumes" {...props} />
    </MantineProvider>,
  );
}

function columnHeaders(): string[] {
  const table = screen.getByRole('table', { name: 'Discharge volumes' });
  const headerRow = table.querySelector('thead tr.pm-cols') as HTMLElement;
  return within(headerRow)
    .getAllByRole('columnheader')
    .map((th) => th.textContent ?? '');
}

describe('PermitDefTable: групи й перемикач', () => {
  it('широка таблиця починається згорнутою: 31 стовпець → 11 колонок', () => {
    show();
    expect(columnHeaders()).toHaveLength(11);
  });

  it('кнопки груп мають aria-expanded, і згорнуті лише групи з ≥ 5 стовпців', () => {
    show();
    const expanded = (name: RegExp): string | null =>
      screen.getByRole('button', { name }).getAttribute('aria-expanded');

    expect(expanded(/Location of the point/)).toBe('false');
    expect(expanded(/Tributaries/)).toBe('false');
    expect(expanded(/Estimated volume by month/)).toBe('false');
    expect(expanded(/^Dates/)).toBe('true');
    expect(expanded(/Contaminated and clean water/)).toBe('true');
  });

  it('згорнута група показує, скільки в ній стовпців', () => {
    show();
    expect(screen.getByRole('button', { name: /Location of the point/ }).textContent).toContain(
      'text of permits.table.groupCount 6',
    );
  });

  it('клік по кнопці групи розгортає її стовпці й повертає назад', () => {
    show();
    const group = screen.getByRole('button', { name: /Tributaries/ });

    fireEvent.click(group);
    expect(group.getAttribute('aria-expanded')).toBe('true');
    // 11 − 1 підсумок + 5 стовпців приток.
    expect(columnHeaders()).toHaveLength(15);
    expect(columnHeaders().some((h) => h.startsWith('Tributary 3'))).toBe(true);

    fireEvent.click(group);
    expect(group.getAttribute('aria-expanded')).toBe('false');
    expect(columnHeaders()).toHaveLength(11);
  });

  it('перемикач «All columns» показує всі 31, «Grouped» повертає згортання', () => {
    show();
    fireEvent.click(screen.getByLabelText('text of permits.table.allColumns'));
    expect(columnHeaders()).toHaveLength(31);
    expect(screen.getByRole('button', { name: /Location of the point/ }).getAttribute('aria-expanded')).toBe('true');

    fireEvent.click(screen.getByLabelText('text of permits.table.grouped'));
    expect(columnHeaders()).toHaveLength(11);
  });

  it('вузька таблиця (≤ 12) — без перемикача й без згортання', () => {
    const narrow = dischargeColumns.slice(0, 12);
    show({ columns: narrow });
    expect(screen.queryByLabelText('text of permits.table.allColumns')).toBeNull();
    expect(columnHeaders()).toHaveLength(12);
    expect(screen.getByRole('button', { name: /Location of the point/ }).getAttribute('aria-expanded')).toBe('true');
  });

  it('initialMode="all" відкриває широку таблицю повністю', () => {
    show({ initialMode: 'all' });
    expect(columnHeaders()).toHaveLength(31);
  });

  it('таблиця без груп не малює рядка груп', () => {
    const flat: PermitColumn[] = [
      { code: 'a', label: 'A', type: 'text' },
      { code: 'b', label: 'B', type: 'number' },
    ];
    show({ columns: flat, rows: [{ id: '1', a: 'x', b: 2 }] });
    expect(document.querySelector('tr.pm-grp')).toBeNull();
  });
});

describe('PermitDefTable: вміст', () => {
  it('має caption і прокручувану область з іменем та фокусом', () => {
    show();
    expect(screen.getByRole('table', { name: 'Discharge volumes' })).toBeTruthy();
    const region = screen.getByRole('region', { name: 'Discharge volumes' });
    expect(region.getAttribute('tabindex')).toBe('0');
  });

  it('перша комірка рядка — його заголовок (th scope=row), липка', () => {
    show();
    const first = screen.getAllByRole('rowheader')[0]!;
    expect(first.textContent).toBe('Caspian Sea (north)');
    expect(first.className).toContain('pm-first');
  });

  it('обовʼязкове порожнє позначене словом, заповнене — ні', () => {
    show();
    const marked = document.querySelectorAll('[data-required-empty="true"]');
    expect(marked).toHaveLength(1);
    expect(marked[0]?.textContent).toBe('text of permits.table.required');
    expect(marked[0]?.className).toContain('pm-bad');
  });

  it('обовʼязкові стовпці в шапці мають зірочку для зору й слово для читалки', () => {
    show();
    const annual = screen.getByRole('columnheader', { name: /Annual stated/ });
    expect(annual.textContent).toContain('*');
    expect(annual.textContent).toContain('text of permits.table.required');
  });

  it('похідний обовʼязковий стовпець порожнім не вважається', () => {
    const cols: PermitColumn[] = [
      { code: 'a', label: 'A', type: 'text' },
      { code: 'code', label: 'Code', type: 'text', required: true, derived: true },
    ];
    show({ columns: cols, rows: [{ id: '1', a: 'x', code: null }] });
    expect(document.querySelectorAll('[data-required-empty="true"]')).toHaveLength(0);
    expect(document.querySelector('td.pm-derived')).not.toBeNull();
  });

  it('Σ згорнутої числової групи — сума місяців рядка; текстова група — «…»', () => {
    show();
    const row = screen.getAllByRole('row')[2]!; // 0, 1 — шапка; 2 — перший рядок даних
    const cells = within(row).getAllByRole('cell');
    // [Location group (…), Tributaries (…), Sub_Start, Sub_End, 4 числа, Σ місяців, Annual]
    expect(cells[0]?.textContent).toBe('…');
    expect(cells[1]?.textContent).toBe('…');
    expect(cells.some((cell) => cell.textContent === '192.312')).toBe(true);
  });

  it('числа менше за 1 — до 5 знаків, решта — до 3; булеве — Yes/No', () => {
    const cols: PermitColumn[] = [
      { code: 'k', label: 'K', type: 'text' },
      { code: 'small', label: 'Small', type: 'number' },
      { code: 'big', label: 'Big', type: 'number' },
      { code: 'flag', label: 'Flag', type: 'bool' },
      { code: 'off', label: 'Off', type: 'bool' },
      { code: 'when', label: 'When', type: 'date' },
    ];
    show({
      columns: cols,
      rows: [{ id: '1', k: 'r', small: 0.123456789, big: 1234.56789, flag: true, off: false, when: '2026-01-05' }],
    });
    const cells = within(screen.getAllByRole('row')[1]!).getAllByRole('cell').map((c) => c.textContent);
    expect(cells).toEqual(['0.12346', '1,234.568', 'text of permits.table.yes', 'text of permits.table.no', 'Jan 5, 2026']);
  });

  it('порожнє необовʼязкове — тире', () => {
    const cols: PermitColumn[] = [
      { code: 'k', label: 'K', type: 'text' },
      { code: 'opt', label: 'Opt', type: 'text' },
    ];
    show({ columns: cols, rows: [{ id: '1', k: 'r', opt: '  ' }] });
    expect(within(screen.getAllByRole('row')[1]!).getAllByRole('cell')[0]?.textContent).toBe('—');
  });

  it('рядок підсумків: підпис у першому стовпці, суми — під числовими', () => {
    show({
      initialMode: 'all',
      totals: { label: 'Σ all 2 rows', sums: { Annual: 192.312, Jan: 24.5 } },
    });
    const footer = document.querySelector('tfoot tr') as HTMLElement;
    expect(footer.querySelector('th')?.textContent).toBe('Σ all 2 rows');
    const texts = Array.from(footer.querySelectorAll('td')).map((td) => td.textContent);
    expect(texts).toContain('192.312');
    expect(texts).toContain('24.5');
    expect(footer.querySelectorAll('td')).toHaveLength(30);
  });

  it('без totals підсумків немає', () => {
    show();
    expect(document.querySelector('tfoot')).toBeNull();
  });

  it('попередження комірки: клас, title і текст для читалки', () => {
    show({
      initialMode: 'all',
      warnCell: (row, column) => (row.id === 'd1' && column.code === 'Annual' ? 'Months do not add up' : null),
    });
    const warn = document.querySelector('td.pm-warn') as HTMLElement;
    expect(warn.getAttribute('title')).toBe('Months do not add up');
    expect(warn.textContent).toContain('Months do not add up');
    expect(document.querySelectorAll('.pm-warn')).toHaveLength(1);
  });

  it('обовʼязкове порожнє не дублюється попередженням', () => {
    show({ initialMode: 'all', warnCell: () => 'always' });
    const bad = document.querySelector('[data-required-empty="true"]') as HTMLElement;
    expect(bad.className).not.toContain('pm-warn');
  });
});

describe('PermitDefTable: рядок', () => {
  const rows: PermitRow[] = [...dischargeRows];

  it('без onRowClick рядки не фокусуються й не клікабельні', () => {
    show();
    const row = screen.getAllByRole('row')[2]!;
    expect(row.getAttribute('tabindex')).toBeNull();
    expect(row.hasAttribute('data-clickable')).toBe(false);
  });

  it('клік, Enter і Пробіл на рядку викликають onRowClick із цим рядком', () => {
    const onRowClick = vi.fn();
    show({ onRowClick });
    const row = screen.getAllByRole('row')[3]!;
    expect(row.getAttribute('tabindex')).toBe('0');

    fireEvent.click(row);
    fireEvent.keyDown(row, { key: 'Enter' });
    fireEvent.keyDown(row, { key: ' ' });
    fireEvent.keyDown(row, { key: 'a' });

    expect(onRowClick).toHaveBeenCalledTimes(3);
    expect(onRowClick).toHaveBeenLastCalledWith(rows[1]);
  });

  it('Enter на вкладеному елементі рядка не відкриває рядок', () => {
    const onRowClick = vi.fn();
    show({ onRowClick, warnCell: () => null });
    const cell = screen.getAllByRole('row')[2]!.querySelector('td') as HTMLElement;
    fireEvent.keyDown(cell, { key: 'Enter' });
    expect(onRowClick).not.toHaveBeenCalled();
  });
});
