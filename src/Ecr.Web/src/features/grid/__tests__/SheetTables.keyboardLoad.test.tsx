import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { DocumentTableDto } from '@/api/types';
import { SheetTables } from '../SheetTables';

/**
 * T3-10: ліниві таблиці були недосяжні клавіатурою — у заглушці «This table loads when you scroll to
 * it» не було фокусованого елемента, і оператор без миші мусив прокручувати сторінку. Тепер у заглушці
 * є кнопка: Tab → Enter монтує сітку, а фокус зниклої заглушки переходить на заголовок таблиці.
 *
 * ⚠ `DocumentGrid` і `IntersectionObserver` підмінені (див. `SheetTables.lazy.test.tsx`): перевіряється
 * рішення про монтування і фокус, а не сітка.
 */
vi.mock('../DocumentGrid', () => ({
  DocumentGrid: ({ tableInstanceId }: { tableInstanceId: number }) => (
    <div data-testid={`grid-${String(tableInstanceId)}`} />
  ),
}));

function tableFixture(index: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'S1',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet' } },
    sheetOrdinal: 1,
    tableCode: `T${String(index)}`,
    tableDefId: index,
    tableInstanceId: 1000 + index,
    tableNameL10n: { values: { en: `Table ${String(index)}` } },
    tableOrdinal: index,
  } as DocumentTableDto;
}

describe('SheetTables: клавіатурний шлях до лінивої таблиці', () => {
  beforeEach(() => {
    class InertObserver {
      observe(): void {}
      unobserve(): void {}
      disconnect(): void {}
      takeRecords(): IntersectionObserverEntry[] {
        return [];
      }
    }
    vi.stubGlobal('IntersectionObserver', InertObserver);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('Tab до кнопки заглушки й Enter монтують саме цю таблицю; фокус — на її заголовку', async () => {
    const user = userEvent.setup();
    render(
      <MantineProvider>
        <SheetTables
          documentId={7}
          periodKey={202609}
          readOnly={false}
          tables={[tableFixture(1), tableFixture(2)]}
        />
      </MantineProvider>,
    );

    expect(screen.queryByTestId('grid-1001')).toBeNull();

    // ⛔ Мутація «прибрати `onLoad` у заглушці» — кнопки немає, Tab нікуди не потрапляє, тест червоний.
    await user.tab();
    const focused = document.activeElement;
    expect(focused?.tagName).toBe('BUTTON');
    expect(focused?.textContent).toContain('grid.tableLoadNow');

    await user.keyboard('{Enter}');

    expect(await screen.findByTestId('grid-1001')).toBeTruthy();
    expect(screen.queryByTestId('grid-1002')).toBeNull();

    // Фокус не загубився на BODY: він на заголовку змонтованої таблиці.
    await waitFor(() => {
      expect(document.activeElement?.hasAttribute('data-table-title')).toBe(true);
    });
    expect(document.activeElement?.textContent).toBe('Table 1');
  });
});
