/** Ключі кешу екрана «Lists» (PM-08d). Власні, щоб не чіпати спільний `queryKeys.ts`. */
export const listsKeys = {
  all: ['permits', 'lists'] as const,
  /** Визначення всіх типів (відбиток — коди типів). */
  definitions: (codes: readonly string[]) => ['permits', 'lists', 'definitions', codes.join('|')] as const,
  /** Чинні значення спільного довідника для шторки. */
  entries: (code: string) => ['permits', 'lists', 'entries', code] as const,
};
