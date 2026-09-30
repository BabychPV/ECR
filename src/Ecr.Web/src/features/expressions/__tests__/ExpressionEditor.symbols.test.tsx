import { describe, it, expect, vi } from 'vitest';
import { act, render, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { JSX } from 'react';
import { ExpressionEditor } from '../ExpressionEditor';
import type { EditorSymbols } from '../completion';
import type { ExpressionMetadataDto, TemplateStructureDto } from '@/api/types';
import { testTheme } from '@/test/render';

/**
 * Звідки провайдери підказок беруть дані — і що їх НЕ запитують на кожну клавішу.
 *
 * ⛔ Два дефекти, які цей файл тримає:
 *   — провайдери реєструються раз на мову і замикали джерело ПЕРШОГО
 *     редактора; тепер кожен редактор передає власне (`create({ symbols })`),
 *     і воно мусить нести структуру версії й таблицю виразу, інакше `[` не
 *     підкаже колонок;
 *   — набір тексту не повинен запитувати склад мови: провайдер викликається
 *     на кожну літеру, і мережа там означала б відчутну затримку друку.
 */

const metadata: ExpressionMetadataDto = {
  functions: [{ name: 'SUM', minArgs: 1, maxArgs: null, acceptsRange: true, resultType: 'Number', tier: 'Core' }],
  constants: [],
  formulas: [],
  arguments: [],
  headers: [],
};

const expressionMetadataMock = vi.fn(() => Promise.resolve(metadata));

vi.mock('../api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api')>();
  return {
    ...actual,
    validateExpression: vi.fn(() => Promise.resolve({ diagnostics: [], resultType: null, skippedChecks: [] })),
    expressionMetadata: () => expressionMetadataMock(),
  };
});

const created: { symbols?: () => EditorSymbols }[] = [];

vi.mock('../monaco', () => {
  const model = { getValue: () => '', dispose: () => {} };

  return {
    LightTheme: 'ecr-light',
    DarkTheme: 'ecr-dark',
    prepare: () => 'ecr-template',
    applyGrammar: () => {},
    setTheme: () => {},
    showDiagnostics: () => {},
    create: (_host: HTMLElement, options: { symbols?: () => EditorSymbols }) => {
      created.push(options);
      let value = '';
      let changeHandler: (() => void) | null = null;

      return {
        getValue: () => value,
        setValue: (next: string) => {
          value = next;
          changeHandler?.();
        },
        getModel: () => model,
        onDidChangeModelContent: (handler: () => void) => {
          changeHandler = handler;
        },
        dispose: () => {},
      };
    },
  };
});

const structure = {
  templateVersionId: 3,
  isEditable: false,
  presentationRevision: 0,
  groupRules: [],
  sheets: [
    {
      id: 1,
      code: 'FSM',
      nameL10n: { values: { en: 'Summary' } },
      ordinal: 1,
      isMandatory: true,
      isVisible: true,
      sheetGroup: null,
      tables: [
        {
          id: 185,
          code: 'FT1',
          nameL10n: { values: { en: 'Fixed' } },
          ordinal: 1,
          layoutKind: 'Flat',
          rowMode: 'Fixed',
          maxDynamicRows: null,
          columns: [
            {
              id: 1,
              code: 'CDEC',
              headerL10n: { values: { en: 'Decimal amount' } },
              dataType: 'Decimal',
              unitSymbol: null,
              ordinal: 1,
            },
          ],
          rows: [{ rowKey: 'R1', label: 'Row one', ordinal: 1 }],
        },
      ],
    },
  ],
} as unknown as TemplateStructureDto;

const placement = { templateVersionId: 3, tableDefId: 185 };

function Editor({ value }: { value: string }): JSX.Element {
  return (
    <MantineProvider theme={testTheme}>
      <ExpressionEditor
        value={value}
        onChange={() => {}}
        dialect="Template"
        placement={placement}
        structure={structure}
        ariaLabel="вираз"
      />
    </MantineProvider>
  );
}

describe('ExpressionEditor: джерело підказок', () => {
  it('редактор віддає провайдерам структуру, таблицю і склад мови', async () => {
    render(<Editor value="" />);

    await waitFor(() => expect(created).toHaveLength(1));
    await waitFor(() => expect(created[0]?.symbols?.().metadata).toBe(metadata));

    const symbols = created[0]?.symbols?.();

    expect(symbols?.dialect).toBe('Template');
    expect(symbols?.tableDefId).toBe(185);
    expect(symbols?.hasTemplateVersion).toBe(true);
    expect(symbols?.structure?.tables.map((t) => t.code)).toEqual(['FT1']);
    expect(symbols?.structure?.tables[0]?.columns[0]).toEqual({
      code: 'CDEC',
      name: 'Decimal amount',
      dataType: 'Decimal',
      unit: null,
    });
  });

  it('набір тексту не запитує склад мови ще раз', async () => {
    expressionMetadataMock.mockClear();
    const { rerender } = render(<Editor value="" />);

    await waitFor(() => expect(expressionMetadataMock).toHaveBeenCalledTimes(1));

    for (const text of ['[', '[C', '[CD', '[CDE', '[CDEC]']) {
      rerender(<Editor value={text} />);
      await act(async () => {
        await Promise.resolve();
      });
    }

    expect(expressionMetadataMock).toHaveBeenCalledTimes(1);
  });
});
