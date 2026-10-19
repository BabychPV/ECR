/**
 * Ключі кешу типів дозволів (PM-08a). Корінь `['permits', 'types']` один, тож `invalidateQueries`
 * з `typesKeys.all` скидає і перелік, і окремі типи.
 */
export const typesKeys = {
  all: ['permits', 'types'] as const,
  /** Увесь перелік типів (усі сторінки, без фільтрів: пошук і фільтр категорій — на клієнті). */
  list: () => ['permits', 'types', 'list'] as const,
  /** Один тип за кодом. */
  one: (code: string) => ['permits', 'types', 'one', code] as const,
};
