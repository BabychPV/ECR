import { describe, expect, it } from 'vitest';
import type { ExpressionFunctionDto, ExpressionMetadataDto } from '@/api/types';
import { functionSnippet, signatureOf, suggestionsAt } from '../completion';
import { functionDoc } from '../describe';

/**
 * `SHEETNAME()` у редакторі виразів шаблону: функція без аргументів приходить із
 * сервера (`GET /expressions/metadata`), пропонується в переліку доповнення,
 * має сигнатуру `SHEETNAME()` і підставляється з курсором ПІСЛЯ дужок.
 *
 * ⚠ Мутаційний доказ (локально): повернути в `functionSnippet` курсор між
 * дужками для `maxArgs === 0` — червоніє «підставляється з курсором після дужок».
 */

const sheetName: ExpressionFunctionDto = {
  name: 'SHEETNAME',
  minArgs: 0,
  maxArgs: 0,
  acceptsRange: false,
  resultType: 'Text',
  tier: 'Core',
};

const sum: ExpressionFunctionDto = {
  name: 'SUM',
  minArgs: 1,
  maxArgs: null,
  acceptsRange: true,
  resultType: 'Number',
  tier: 'Core',
};

const metadata: ExpressionMetadataDto = {
  functions: [sum, sheetName],
  constants: [],
  formulas: [],
  arguments: [],
  headers: [],
};

describe('SHEETNAME: редактор виразів', () => {
  it('пропонується в переліку функцій шаблону за першими літерами', () => {
    const result = suggestionsAt('SHEE', 4, { dialect: 'Template', metadata });

    expect(result?.items.map((i) => i.label)).toEqual(['SHEETNAME']);
    expect(result?.items[0]?.detail).toBe('SHEETNAME()');
  });

  it('підставляється з курсором після дужок, а функція з аргументами — між ними', () => {
    expect(functionSnippet(sheetName)).toBe('SHEETNAME()$0');
    expect(functionSnippet(sum)).toBe('SUM($0)');
  });

  it('довідка показує сигнатуру без аргументів і текстовий результат', () => {
    expect(signatureOf(sheetName)).toBe('SHEETNAME()');

    const doc = functionDoc(sheetName);
    expect(doc.title).toBe('SHEETNAME()');
    expect(doc.lines.join('\n')).toContain('Text');
  });
});
