import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderWithMantine } from '@/test/render';
import { ConstTableDialog } from '../ConstTableDialog';
import { ScriptOutlineView } from '../ScriptOutlineView';
import { ScriptPreviewTable } from '../ScriptPreviewTable';
import { ScriptProblemsList } from '../ScriptProblemsList';
import type { ScriptPreviewDto } from '../types';
import { diagnostic, outline as outlineOf, outlineTable, previewRow, previewTable, previewValue } from './fixtures';

/*
 * ⚠ Каталог рядків у тестах не завантажується, тому підписи відображаються як
 * `⟦ключ⟧`. Тести перевіряють структуру, дані й колбеки, а не переклади.
 */

describe('ScriptProblemsList', () => {
  it('порожній список — повідомлення; клік по проблемі веде до рядка й колонки', () => {
    const onJump = vi.fn();
    const { rerender } = renderWithMantine(<ScriptProblemsList diagnostics={[]} onJump={onJump} />);
    expect(screen.queryByTestId('script-problems')).toBeNull();

    rerender(
      <ScriptProblemsList
        diagnostics={[
          diagnostic({
            code: 'script.unknownName',
            line: 4,
            column: 7,
            length: 3,
            message: 'Unknown name "abc".',
          }),
        ]}
        onJump={onJump}
      />,
    );
    expect(screen.getByText('Unknown name "abc".')).toBeTruthy();
    fireEvent.click(screen.getByRole('button'));
    expect(onJump).toHaveBeenCalledWith(4, 7);
  });
});

describe('ScriptOutlineView', () => {
  const outline = outlineOf({
    tables: [outlineTable('T3_00'), outlineTable('T10_03', 'Gtu')],
    inputs: [{ name: 'Land_Hours', optional: true }],
    outputs: [
      { code: 'OUT_A', unit: 't', perSubstance: false },
      { code: 'OUT_B', unit: 't', perSubstance: true },
    ],
    imports: ['Common'],
  });

  it('показує входи, виходи, таблиці, імпорти; «немає колонки» лише для виходів із набору', () => {
    renderWithMantine(
      <ScriptOutlineView outline={outline} outputsWithoutColumn={new Set(['OUT_B'])} />,
    );
    for (const text of ['Land_Hours', 'OUT_A', 'OUT_B', 'T3_00', 'T10_03', 'Gtu', 'Common']) {
      expect(screen.getByText(text)).toBeTruthy();
    }
    expect(screen.getAllByText(/outline\.noColumn/)).toHaveLength(1);
  });
});

describe('ScriptPreviewTable', () => {
  const preview: ScriptPreviewDto = {
    diagnostics: [],
    tables: [
      previewTable({
        tableCode: 'T3_00',
        totalRows: 3,
        selectedRows: 1,
        inactiveRows: 1,
        rows: [
          previewRow({
            rowKey: 'a',
            rowNumber: 1,
            values: [
              previewValue({ name: 'hours', kind: 'var', text: '12' }),
              previewValue({ name: 'OUT', text: '3.5' }),
            ],
          }),
          previewRow({ rowKey: 'b', rowNumber: 2, state: 'inactive' }),
          previewRow({
            rowKey: 'c',
            rowNumber: 3,
            state: 'rejected',
            issue: 'missingInputs',
            missing: ['Land_X'],
          }),
        ],
      }),
    ],
  };

  it('виходи — колонками; var — у розгортанні рядка; рядки без значень не розгортаються', () => {
    const onCreateTest = vi.fn();
    renderWithMantine(
      <ScriptPreviewTable preview={preview} outputNames={['OUT']} onCreateTest={onCreateTest} />,
    );
    expect(screen.getByText('T3_00')).toBeTruthy();
    expect(screen.getByText('3.5')).toBeTruthy();
    expect(screen.getByText('Land_X')).toBeTruthy();
    expect(screen.queryByText('hours')).toBeNull();

    const toggles = screen.getAllByRole('button');
    expect(toggles).toHaveLength(1);
    fireEvent.click(toggles[0] as HTMLElement);
    expect(screen.getByText('hours')).toBeTruthy();
    expect(toggles[0]?.getAttribute('aria-expanded')).toBe('true');

    fireEvent.click(screen.getByRole('button', { name: /preview\.createTest/ }));
    expect(onCreateTest).toHaveBeenCalledWith(preview.tables[0]?.rows[0], preview.tables[0]);
  });

  it('вихід по речовинах дає колонку на кожну речовину', () => {
    renderWithMantine(
      <ScriptPreviewTable
        outputNames={['S']}
        preview={{
          diagnostics: [],
          tables: [
            previewTable({
              tableInstanceId: 2,
              tableCode: 'T',
              totalRows: 1,
              selectedRows: 1,
              rows: [
                previewRow({
                  rowKey: 'r',
                  rowNumber: 1,
                  values: [
                    previewValue({ name: 'S', substance: '301', text: '1' }),
                    previewValue({ name: 'S', substance: '304', text: '2' }),
                  ],
                }),
              ],
            }),
          ],
        }}
      />,
    );
    expect(screen.getByText('S [301]')).toBeTruthy();
    expect(screen.getByText('S [304]')).toBeTruthy();
  });
});

describe('ConstTableDialog', () => {
  it('«Застосувати» повертає канонічний літерал після правки комірки', () => {
    const onApply = vi.fn();
    renderWithMantine(
      <ConstTableDialog
        opened
        onClose={() => undefined}
        literal={'{ A: { x: 1, y: 2 } }'}
        constName="EF"
        onApply={onApply}
      />,
    );

    const cell = screen.getAllByRole('textbox').find((input) => (input as HTMLInputElement).value === '1');
    expect(cell).toBeDefined();
    fireEvent.change(cell as HTMLElement, { target: { value: '1,5' } });

    fireEvent.click(screen.getByText(/constTable\.apply/));
    expect(onApply).toHaveBeenCalledWith('{\n    A: { x: 1.5, y: 2 },\n}');
  });

  it('літерал не мапа — повідомлення замість сітки, без «Застосувати»', () => {
    renderWithMantine(
      <ConstTableDialog opened onClose={() => undefined} literal="42" onApply={() => undefined} />,
    );
    expect(screen.getByRole('alert')).toBeTruthy();
    expect(screen.queryByText(/constTable\.apply/)).toBeNull();
  });

  it('повторний ключ блокує «Застосувати»', () => {
    renderWithMantine(
      <ConstTableDialog
        opened
        onClose={() => undefined}
        literal={'{ A: 1, B: 2 }'}
        onApply={() => undefined}
      />,
    );
    const keyB = screen.getAllByRole('textbox').find((i) => (i as HTMLInputElement).value === 'B');
    fireEvent.change(keyB as HTMLElement, { target: { value: 'A' } });
    const apply = screen.getByText(/constTable\.apply/).closest('button');
    expect(apply?.hasAttribute('disabled')).toBe(true);
  });

  it('F13c: коментарі й вихідний текст чисел переживають «Застосувати»', () => {
    const onApply = vi.fn();
    const literal = ['{', '    // зима', '    A: { x: 1.0, y: 0.10 }, // холодно', '    B: { x: 2,   y: 3 },', '}'].join('\n');
    renderWithMantine(
      <ConstTableDialog opened onClose={() => undefined} literal={literal} onApply={onApply} />,
    );
    // нічого не чіпали — ні попередження, ні втрат
    expect(screen.queryByTestId('const-comments-lost')).toBeNull();
    fireEvent.click(screen.getByText(/constTable\.apply/));
    expect(onApply).toHaveBeenCalledWith(literal);
  });

  it('F13c: правка комірки лишає коментарі рядка; запис, якого не торкались, не змінюється', () => {
    const onApply = vi.fn();
    const literal = ['{', '    // зима', '    A: { x: 1.0 }, // холодно', '    B: { x: 2.50 },', '}'].join('\n');
    renderWithMantine(
      <ConstTableDialog opened onClose={() => undefined} literal={literal} onApply={onApply} />,
    );
    const cell = screen.getAllByRole('textbox').find((i) => (i as HTMLInputElement).value === '1.0');
    fireEvent.change(cell as HTMLElement, { target: { value: '7' } });
    fireEvent.click(screen.getByText(/constTable\.apply/));
    expect(onApply).toHaveBeenCalledWith(
      ['{', '    // зима', '    A: { x: 7 }, // холодно', '    B: { x: 2.50 },', '}'].join('\n'),
    );
    expect(screen.queryByTestId('const-comments-lost')).toBeNull();
  });

  it('F13c: коментар усередині запису, який змінюють, — попередження ДО «Застосувати»', () => {
    const literal = ['{', '    A: { x: 1, // про x', '         y: 2 },', '}'].join('\n');
    renderWithMantine(
      <ConstTableDialog opened onClose={() => undefined} literal={literal} onApply={() => undefined} />,
    );
    expect(screen.queryByTestId('const-comments-lost')).toBeNull();
    const cell = screen.getAllByRole('textbox').find((i) => (i as HTMLInputElement).value === '1');
    fireEvent.change(cell as HTMLElement, { target: { value: '5' } });
    expect(screen.getByTestId('const-comments-lost').textContent).toContain('constTable.commentsLost');
  });

  it('F13c: неоднозначне число (`1,234.56`) — помилка комірки, «Застосувати» заблоковано', () => {
    const onApply = vi.fn();
    renderWithMantine(
      <ConstTableDialog opened onClose={() => undefined} literal="{ A: { x: 1 } }" onApply={onApply} />,
    );
    const cell = screen.getAllByRole('textbox').find((i) => (i as HTMLInputElement).value === '1');
    fireEvent.change(cell as HTMLElement, { target: { value: '1,234.56' } });
    expect(screen.getAllByText(/error\.ambiguousNumber/).length).toBeGreaterThan(0);
    const apply = screen.getByText(/constTable\.apply/).closest('button');
    expect(apply?.hasAttribute('disabled')).toBe(true);
    fireEvent.click(apply as HTMLElement);
    expect(onApply).not.toHaveBeenCalled();

    // у лапках — свідомий рядок
    fireEvent.change(cell as HTMLElement, { target: { value: '"1,234.56"' } });
    expect(screen.queryAllByText(/error\.ambiguousNumber/)).toHaveLength(0);
    fireEvent.click(screen.getByText(/constTable\.apply/));
    expect(onApply).toHaveBeenCalledWith('{\n    A: { x: "1,234.56" },\n}');
  });
});
