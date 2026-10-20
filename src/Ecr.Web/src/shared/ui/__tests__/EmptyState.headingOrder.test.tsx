import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { EmptyState } from '@/shared/ui/AsyncBoundary';
import { DataTable } from '@/shared/ui/DataTable/DataTable';

/**
 * P3-N8: порожній стан сторінки Permits стояв `h4` одразу після `h1` - axe `heading-order`. Рівень задає виклик, вигляд
 * лишається `h4`; без параметра - як і було (4).
 * Мутаційний доказ (вручну, відновлено): прибрати `order={headingOrder}` - тести на рівень 2 червоні.
 */
afterEach(() => cleanup());

describe('EmptyState: рівень заголовка', () => {
  it('типово h4', () => {
    renderWithMantine(<EmptyState title="Nothing here" />);

    expect(screen.getByRole('heading', { level: 4, name: 'Nothing here' })).toBeTruthy();
  });

  it('headingOrder=2 дає h2 з розміром h4', () => {
    renderWithMantine(<EmptyState title="Nothing here" headingOrder={2} />);

    const heading = screen.getByRole('heading', { level: 2, name: 'Nothing here' });
    expect(heading.getAttribute('data-order')).toBe('2');
    expect(heading.getAttribute('style') ?? '').toContain('--mantine-h4-font-size');
  });
});

describe('DataTable: emptyHeadingOrder доходить до заголовка', () => {
  const columns = [{ key: 'a', label: 'A' }];

  for (const filtered of [false, true]) {
    it(`${filtered ? 'нічого не знайдено' : 'порожньо'}: h2`, () => {
      renderWithMantine(
        <DataTable<{ id: string }>
          columns={columns}
          rows={[]}
          rowKey={(row) => row.id}
          filtered={filtered}
          emptyTitle="Empty title"
          noMatchTitle="No match title"
          emptyHeadingOrder={2}
        />,
      );

      expect(screen.getByRole('heading', { level: 2, name: filtered ? 'No match title' : 'Empty title' })).toBeTruthy();
    });
  }
});
