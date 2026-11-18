import { describe, expect, it } from 'vitest';
import {
  buildPreviewRequest,
  defaultPeriodKey,
  MaxPreviewRows,
  singleTemplateCode,
  tableOfRow,
} from '../scriptPreviewRequest';
import { outline, outlineTable, previewRow, previewTable } from './fixtures';

describe('запит перегляду', () => {
  it('без документа або періоду запиту немає', () => {
    expect(buildPreviewRequest('x', { documentId: null, periodKey: 202604, tableInstanceId: null })).toBeNull();
    expect(buildPreviewRequest('x', { documentId: 5, periodKey: null, tableInstanceId: null })).toBeNull();
  });

  it('несе ПОТОЧНИЙ текст редактора; порожній текст — null (збережений текст версії)', () => {
    const request = buildPreviewRequest('output A : t = 1;', {
      documentId: 5,
      periodKey: 202604,
      tableInstanceId: 9,
    });
    expect(request).toEqual({
      documentId: 5,
      periodKey: 202604,
      tableInstanceId: 9,
      maxRows: 50,
      text: 'output A : t = 1;',
    });

    expect(buildPreviewRequest('  \n', { documentId: 5, periodKey: 1, tableInstanceId: null })?.text).toBeNull();
  });

  it('кількість рядків тримається в межах 1…200', () => {
    const base = { documentId: 1, periodKey: 1, tableInstanceId: null };
    expect(buildPreviewRequest('x', { ...base, maxRows: 1000 })?.maxRows).toBe(MaxPreviewRows);
    expect(buildPreviewRequest('x', { ...base, maxRows: 0 })?.maxRows).toBe(1);
    expect(buildPreviewRequest('x', { ...base, maxRows: 12.9 })?.maxRows).toBe(12);
  });

  it('документи звужуються за шаблоном лише коли він єдиний', () => {
    expect(singleTemplateCode(null)).toBeNull();
    expect(singleTemplateCode(outline({ tables: [outlineTable('T')] }))).toBeNull();
    expect(
      singleTemplateCode(outline({ tables: [outlineTable('A', null, 'HSE'), outlineTable('B', 'b', 'HSE')] })),
    ).toBe('HSE');
    expect(
      singleTemplateCode(outline({ tables: [outlineTable('A', null, 'HSE'), outlineTable('B', null, 'OTHER')] })),
    ).toBeNull();
  });

  it('період за замовчуванням — перший відкритий, інакше перший, інакше нічого', () => {
    expect(defaultPeriodKey(undefined)).toBeNull();
    expect(defaultPeriodKey([])).toBeNull();
    expect(
      defaultPeriodKey([
        { periodKey: 202601, state: 'Closed' },
        { periodKey: 202602, state: 'Open' },
      ]),
    ).toBe(202602);
    expect(defaultPeriodKey([{ periodKey: 202601, state: 'Closed' }])).toBe(202601);
  });

  it('екземпляр таблиці рядка знаходиться за самим рядком', () => {
    const row = previewRow({ rowKey: 'r', rowNumber: 1 });
    const tables = [previewTable({ tableCode: 'A', rows: [] }), previewTable({ tableCode: 'B', rows: [row] })];
    expect(tableOfRow(tables, row)?.tableCode).toBe('B');
  });
});
