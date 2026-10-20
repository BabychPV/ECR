/** Ключі кешу екрана «Lists» (PM-08d, PM-08e). Власні, щоб не чіпати спільний `queryKeys.ts`. */
export const listsKeys = {
  all: ['permits', 'lists'] as const,
  /** Перелік усіх списків (`GET /permit-lists`). */
  index: ['permits', 'lists', 'index'] as const,
  /** Список зі значеннями за ключем (`GET /permit-lists/{key}`). */
  detail: (key: string) => ['permits', 'lists', 'detail', key] as const,
};
