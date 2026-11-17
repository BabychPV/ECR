import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { renderWithMantine } from '@/test/render';
import { ConstTableDialog } from '../ConstTableDialog';
import { ScriptOutlineView } from '../ScriptOutlineView';
import { ScriptPreviewTable } from '../ScriptPreviewTable';
import { ScriptProblemsList } from '../ScriptProblemsList';
import type { ScriptOutlineDto, ScriptPreviewDto } from '../types';

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
          {
            code: 'script.unknownName',
            messageKey: null,
            params: null,
            line: 4,
            column: 7,
            length: 3,
            message: 'Unknown name "abc".',
          },
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
  const outline: ScriptOutlineDto = {
    tables: [{ code: 'T3_00' }, { code: 'T10_03', alias: 'Gtu' }],
    inputs: [{ name: 'Land_Hours', optional: true }],
    outputs: [
      { code: 'OUT_A', unit: 't', perSubstance: false },
      { code: 'OUT_B', unit: 't', perSubstance: true },
    ],
    substances: [],
    imports: ['Common'],
    constants: [],
  };

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
      {
        tableInstanceId: 1,
        tableCode: 'T3_00',
        totalRows: 3,
        selectedRows: 1,
        inactiveRows: 1,
        rows: [
          {
            rowKey: 'a',
            rowNumber: 1,
            state: 'selected',
            values: [
              { name: 'hours', kind: 'number', text: '12' },
              { name: 'OUT', kind: 'number', text: '3.5' },
            ],
          },
          { rowKey: 'b', rowNumber: 2, state: 'inactive', values: [] },
          {
            rowKey: 'c',
            rowNumber: 3,
            state: 'rejected',
            issue: 'missingInputs',
            missing: ['Land_X'],
            values: [],
          },
        ],
      },
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

    const create = screen.getAllByRole('button').find((b) => b.textContent === '+');
    expect(create).toBeDefined();
    fireEvent.click(create as HTMLElement);
    expect(onCreateTest).toHaveBeenCalledWith(preview.tables[0]?.rows[0], preview.tables[0]);
  });

  it('вихід по речовинах дає колонку на кожну речовину', () => {
    renderWithMantine(
      <ScriptPreviewTable
        outputNames={['S']}
        preview={{
          diagnostics: [],
          tables: [
            {
              tableInstanceId: 2,
              tableCode: 'T',
              totalRows: 1,
              selectedRows: 1,
              inactiveRows: 0,
              rows: [
                {
                  rowKey: 'r',
                  rowNumber: 1,
                  state: 'selected',
                  values: [
                    { name: 'S', substance: '301', kind: 'number', text: '1' },
                    { name: 'S', substance: '304', kind: 'number', text: '2' },
                  ],
                },
              ],
            },
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
});
