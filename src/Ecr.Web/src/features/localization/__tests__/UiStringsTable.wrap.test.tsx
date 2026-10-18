import { readFileSync } from 'node:fs';
import path from 'node:path';
import { createRef } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithQuery } from '@/test/render';
import { UiStringsTable } from '../UiStringsTable';

/**
 * CL-19 (B-4): на ширині 500 px таблиця каталогу не має розпирати сторінку.
 *
 * ⚠ jsdom не застосовує зовнішні стилі й не міряє розкладку, тому доказ двоскладовий: таблиця
 * несе клас, а файл стилів містить правило переносу для цього класу (як `stickyTableContext.test.ts`).
 * Сам вимір (`scrollWidth <= innerWidth` на 500) — a11y/візуальний прогін у CI.
 */
const css = readFileSync(
  path.resolve(process.cwd(), 'src/features/localization/uiStringsTable.css'),
  'utf8',
).replace(/\/\*[\s\S]*?\*\//g, '');

describe('UiStringsTable: перенос довгих ключів', () => {
  it('таблиця має клас, а правило дозволяє перенос усередині слова', () => {
    renderWithQuery(
      <UiStringsTable
        visible={['grid.cellColor.scope.selection']}
        hasMore={false}
        sentinel={createRef<HTMLDivElement>()}
        strings={{ 'grid.cellColor.scope.selection': 'Selection' }}
        original={{ 'grid.cellColor.scope.selection': 'Selection' }}
        isDefault
        editingKey={null}
        draft=""
        saving={false}
        onDraftChange={vi.fn()}
        onEdit={vi.fn()}
        onCancel={vi.fn()}
        onSave={vi.fn()}
      />,
    );

    const table = screen.getByRole('table');

    expect(table.classList.contains('ecr-ui-strings-table')).toBe(true);
    // Шапка лишається липкою: клас спільний лишається поруч, обгортки з прокруткою немає.
    expect(table.classList.contains('ecr-sticky-head')).toBe(true);
    expect(css).toMatch(/\.ecr-ui-strings-table td[^{]*\{[^}]*overflow-wrap:\s*anywhere/);
    expect(css).not.toMatch(/overflow-x\s*:\s*(auto|scroll)/);
  });
});

