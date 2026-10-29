import { describe, expect, it } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { gridReadOnlyFor } from '../cellState';

/*
 * AN-142 (Q-17): поданий аркуш не замикається цілком, коли СЕРВЕР віддав у зрізі бодай одну редаговну комірку
 * (текст якоря року). Клієнт не вгадує права: лише `cellPermissions`.
 *
 * ⛔ Мутація: прибрати перевірку `sheetState !== 'Submitted'` - червоніє «затверджений лишається замкненим»;
 * замінити `return false` на `return true` - червоніє «текст якоря відкриває сітку».
 */

function column(code: string): ColumnDto {
  return { code, dataType: 'String', isReadOnly: false } as unknown as ColumnDto;
}

function slice(permissions: Record<string, string>): TableSliceDto {
  return {
    columns: [column('STR'), column('NUM')],
    rows: [{ rowKey: 'r1' }],
    cellPermissions: permissions,
    cellConfirmations: {},
  } as unknown as TableSliceDto;
}

describe('AN-142: gridReadOnlyFor', () => {
  it('поданий аркуш, де сервер дозволив текст якоря (число - DocumentSubmitted), не замкнений цілком', () => {
    expect(gridReadOnlyFor(true, 'Submitted', slice({ 'r1:NUM': 'DocumentSubmitted' }))).toBe(false);
  });

  it('поданий аркуш, де сервер відмовив усе, лишається лише читання', () => {
    const denied = { 'r1:STR': 'DocumentSubmitted', 'r1:NUM': 'DocumentSubmitted' };
    expect(gridReadOnlyFor(true, 'Submitted', slice(denied))).toBe(true);
  });

  it('затверджений і закритий період не відкриваються навіть із «дозволом» у зрізі', () => {
    expect(gridReadOnlyFor(true, 'Approved', slice({}))).toBe(true);
    expect(gridReadOnlyFor(true, undefined, slice({}))).toBe(true);
  });

  it('зріз ще не прийшов - лишається лише читання; сторінка не readOnly - false', () => {
    expect(gridReadOnlyFor(true, 'Submitted', undefined)).toBe(true);
    expect(gridReadOnlyFor(false, 'Draft', slice({}))).toBe(false);
  });
});
