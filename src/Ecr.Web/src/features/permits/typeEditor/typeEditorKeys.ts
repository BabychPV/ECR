/*
 * Ключі react-query редактора типу дозволу (Permits, PM-08b). Корінь — `['permits','types',code]`:
 * чужий код у ключі не змішується, а скидання `['permits','types']` чіпає і перелік, і всі відкриті
 * типи.
 */
export const typeEditorKeys = {
  all: ['permits', 'types'] as const,
  detail: (code: string) => ['permits', 'types', code] as const,
} as const;
