import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { UnmatchedRowsBanner } from '@/features/documents/UnmatchedRowsBanner';
import { testTheme } from '@/test/render';

/**
 * D14: рядок без правила розрахунку (напр. статус Removed) - інформаційна позначка на аркуші, не помилка.
 *
 * Мутація: прибрати `role="status"`/ключі каталогу або список рядків - червоніють перевірки нижче.
 */

function show(rows: Parameters<typeof UnmatchedRowsBanner>[0]['rows']): void {
  render(
    <MantineProvider theme={testTheme}>
      <UnmatchedRowsBanner rows={rows} />
    </MantineProvider>,
  );
}

describe('UnmatchedRowsBanner', () => {
  it('називає ключ рядка без правила, це status а не alert, без кнопок дій', () => {
    show({ count: 1, rows: [{ tableInstanceId: 5, rowNumber: 2, rowKey: 'R-REMOVED' }] });

    const banner = screen.getByTestId('document-unmatched-rows');
    expect(banner.getAttribute('role')).toBe('status');
    expect(banner.textContent).toContain('document.unmatchedRows.title');
    const hint = screen.getByTestId('document-unmatched-rows-hint').textContent ?? '';
    expect(hint).toContain('document.unmatchedRows.hint');
    expect(hint).toContain('2 (R-REMOVED)');
    expect(screen.queryAllByRole('button')).toHaveLength(0);
  });

  it('понад показані ключі - хвіст позначено трьома крапками й повною кількістю', () => {
    const rows = Array.from({ length: 10 }, (_, i) => ({ tableInstanceId: 5, rowNumber: i + 1, rowKey: `K${String(i + 1)}` }));
    show({ count: 14, rows });

    const hint = screen.getByTestId('document-unmatched-rows-hint').textContent ?? '';
    expect(hint).toContain('10 (K10), …');
    expect(hint).toContain('14');
  });
});