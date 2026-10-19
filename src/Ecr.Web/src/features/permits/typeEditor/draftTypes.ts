import type { PermitTypeRule, PermitTypeTable } from '../api/types';

/* Спільні типи й константи моделі (без залежностей від решти typeEditor, щоб модулі не ходили по колу). */

/** Робоча копія: усе, що замінює `PUT`. */
export interface TypeDraft {
  readonly nameL10n: Readonly<Record<string, string>>;
  readonly category: string;
  readonly rules: readonly PermitTypeRule[];
  readonly tables: readonly PermitTypeTable[];
}

/** Типи стовпців, які приймає сервер. */
export const ColumnDataTypes = ['String', 'Int', 'Decimal', 'Bool', 'Date', 'Lookup'] as const;
export type ColumnDataType = (typeof ColumnDataTypes)[number];

/** Область списку стовпця-посилання (значення — рядки контракту `PermitListScope`). */
export const ListScopes = {
  shared: 'SharedRegistry',
  type: 'TypeOnly',
  permit: 'SamePermitTable',
} as const;
export type ListScopeKey = keyof typeof ListScopes;

/** Код стовпця посилання на дозвіл у таблицях — зарезервований сервером. */
export const ReservedColumnCode = 'PERMIT';

/** Дзеркало `EcrCode.Pattern` сервера: літера, далі літери/цифри/підкреслення, до 64. */
const CodePattern = /^[A-Za-z][A-Za-z0-9_]{0,63}$/;

/** Чи код допустимий для таблиці, стовпця, групи й перевірки. */
export function isValidCode(code: string | null | undefined): code is string {
  return typeof code === 'string' && CodePattern.test(code);
}
