import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { DataTable } from '@/shared/ui/DataTable/DataTable';

/**
 * ✎ DOCUX (огляд UI 27.11): на Projects під таблицею стояв видимий підпис «Projects» — та сама назва, що в заголовку
 * сторінки, читався як забутий рядок. `captionHidden` лишає `<caption>` читалці (назва таблиці), але ховає з екрана.
 */
afterEach(() => cleanup());

function mount(captionHidden?: boolean): void {
  renderWithMantine(
    <DataTable<{ id: string }>
      caption="Projects"
      captionHidden={captionHidden}
      columns={[{ key: 'id', label: 'Id' }]}
      rows={[{ id: 'a' }]}
      rowKey={(row) => row.id}
    />,
  );
}

describe('DataTable: підпис таблиці', () => {
  it('captionHidden — підпис є для читалки (назва таблиці), але візуально прихований', () => {
    mount(true);
    expect(screen.getByRole('table', { name: 'Projects' })).toBeTruthy();
    expect(document.querySelector('caption')?.classList.contains('mantine-visually-hidden')).toBe(true);
  });

  it('за замовчуванням підпис видимий, як і доти', () => {
    mount();
    expect(document.querySelector('caption')?.classList.contains('mantine-visually-hidden')).toBe(false);
  });
});
