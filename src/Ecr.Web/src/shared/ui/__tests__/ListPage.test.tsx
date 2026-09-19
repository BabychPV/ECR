import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { ListPage, type ListPageProps } from '@/shared/ui/ListPage';
import type { StatStripItems } from '@/shared/ui/StatStrip';

/**
 * `UI-06`, шар 3: шаблон сторінки-переліку
 * (`PageHeader + StatStrip? + FilterBar + DataTable + DetailDrawer`).
 *
 * ⚠ `FilterBar` і `DataTable` сюди НЕ приходять — їх пише паралельна гілка, і
 * шаблон бере ці частини вузлами (`filters`, `table`). Тому в тестах на їхніх
 * місцях стоять звичайні вузли: перевіряється КОМПОЗИЦІЯ, а не чужа
 * реалізація.
 */

const stats: StatStripItems = [
  { id: 'running', label: 'running', value: 3 },
  { id: 'failed', label: 'failed today', value: 7, tone: 'danger' },
];

function show(props: ListPageProps, url = '/admin/jobs'): HTMLElement {
  const { container } = render(
    <MantineProvider theme={theme}>
      <MemoryRouter initialEntries={[url]}>
        <div data-probe="">
          <ListPage {...props} />
        </div>
      </MemoryRouter>
    </MantineProvider>,
  );

  const probe = container.querySelector<HTMLElement>('[data-probe]');
  if (probe === null) throw new Error('пробний вузол не змонтувався');

  return probe;
}

function table(text = 'Jobs table'): ReactNode {
  return <div data-testid="table">{text}</div>;
}

function detail(): ListPageProps['detail'] {
  return {
    panelId: 'J-10427',
    title: 'J-10427',
    closeLabel: 'Close details',
    children: <p data-testid="panel-body">Started by D. Akhmetova</p>,
  };
}

afterEach(cleanup);

describe('ListPage збирає шаблон у заданому порядку', () => {
  it('шапка → смуга → фільтри → таблиця', () => {
    const container = show({
      header: { title: 'Jobs' },
      stats: { label: 'Summary', items: stats },
      filters: <div data-testid="filters">filters</div>,
      table: table(),
    });

    const order = [...container.querySelectorAll('[data-list-page] > *')].map((node) => {
      if (node.hasAttribute('data-stat-strip')) return 'stats';
      if (node.hasAttribute('data-list-filters')) return 'filters';
      if (node.hasAttribute('data-list-table')) return 'table';

      return 'header';
    });

    expect(order).toEqual(['header', 'stats', 'filters', 'table']);
  });

  it('заголовок малює PageHeader — один h-рівень, не власний', () => {
    show({ header: { title: 'Jobs' }, table: table() });

    expect(screen.getByRole('heading', { name: 'Jobs' })).toBeDefined();
  });

  it('власного <main> шаблон НЕ малює: його дає AppLayout', () => {
    const container = show({ header: { title: 'Jobs' }, table: table() });

    // Другий `main` на сторінці — порушення `landmark-no-duplicate-main`.
    expect(container.querySelector('main')).toBeNull();
  });

  it('вміст переліку доходить як є', () => {
    show({ header: { title: 'Jobs' }, table: table('Nothing to show') });

    expect(screen.getByTestId('table').textContent).toBe('Nothing to show');
  });
});

describe('D15-06: елемент без даних не малюється', () => {
  it('немає показників — немає смуги', () => {
    const container = show({ header: { title: 'Jobs' }, table: table() });

    expect(container.querySelector('[data-stat-strip]')).toBeNull();
    expect(screen.queryByRole('group')).toBeNull();
  });

  it('є показники — смуга є (перевірка не ловить усе підряд)', () => {
    const container = show({
      header: { title: 'Jobs' },
      stats: { label: 'Summary', items: stats },
      table: table(),
    });

    expect(container.querySelector('[data-stat-strip]')).not.toBeNull();
    expect(screen.getByRole('group', { name: 'Summary' })).toBeDefined();
  });

  it('порожній перелік показників (повз типи) теж не малює смуги', () => {
    const container = show({
      header: { title: 'Jobs' },
      stats: { label: 'Summary', items: [] as unknown as StatStripItems },
      table: table(),
    });

    expect(container.querySelector('[data-stat-strip]')).toBeNull();
  });

  it('немає фільтрів — немає й порожнього рядка над таблицею', () => {
    const container = show({ header: { title: 'Jobs' }, table: table() });

    expect(container.querySelector('[data-list-filters]')).toBeNull();
  });

  it.each([null, false, undefined, ''])('порожній вузол фільтрів виду %o не малюється', (node) => {
    const container = show({
      header: { title: 'Jobs' },
      filters: node as ReactNode,
      table: table(),
    });

    expect(container.querySelector('[data-list-filters]')).toBeNull();
  });

  it('немає підвалу — немає обгортки під таблицею', () => {
    const container = show({ header: { title: 'Jobs' }, table: table() });

    expect(container.querySelector('[data-list-footer]')).toBeNull();
  });
});

describe('L2: шухляда подробиць закрита за замовчуванням', () => {
  /*
   * ⛔ Правило директиви (§0, `L2`) сформульоване саме перевіркою:
   * «після рендера `queryByRole('complementary')` = null». Одного цього
   * замало — `complementary` не з'являється й тоді, коли шухляда відкрита
   * (Mantine оголошує її `role="dialog"`, про що прямо написано в
   * `DetailDrawer.tsx`). Тому перевіряється ще й ВМІСТ: подробиць на екрані
   * немає взагалі.
   */
  it('після рендера немає ні complementary, ні dialog, ні вмісту шухляди', () => {
    show({ header: { title: 'Jobs' }, table: table(), detail: detail() });

    expect(screen.queryByRole('complementary')).toBeNull();
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.queryByTestId('panel-body')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Close details' })).toBeNull();
  });

  it('?panel= з ЧУЖИМ ключем шухляду не відкриває', () => {
    show({ header: { title: 'Jobs' }, table: table(), detail: detail() }, '/admin/jobs?panel=J-1');

    expect(screen.queryByTestId('panel-body')).toBeNull();
  });

  /*
   * ⚠ Дзеркало: без цього тесту попередні два були б зеленими й тоді, коли
   * шухляду взагалі не підключено до шаблону.
   */
  it('?panel= зі СВОЇМ ключем відкриває — шухляда справді в шаблоні', () => {
    show(
      { header: { title: 'Jobs' }, table: table(), detail: detail() },
      '/admin/jobs?panel=J-10427',
    );

    expect(screen.getByTestId('panel-body')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Close details' })).toBeDefined();
  });

  it('без пропа detail шухляди немає навіть із ?panel= в адресі', () => {
    show({ header: { title: 'Jobs' }, table: table() }, '/admin/jobs?panel=J-10427');

    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('ListPage не знає конкретних реалізацій переліку', () => {
  it('фільтри й таблиця — довільні вузли, не FilterBar/DataTable', () => {
    function Custom(): JSX.Element {
      return <section data-testid="custom">Власне подання</section>;
    }

    show({ header: { title: 'Jobs' }, table: <Custom /> });

    expect(screen.getByTestId('custom')).toBeDefined();
  });
});
