/**
 * Код типу дозволу з його назви (майстер «New permit type», PM-08a).
 *
 * Макет (`screens-permits.js`, `onApply`): прибрати все, крім латинських літер, цифр і пробілів, і
 * зробити кожне слово з великої літери. Сервер вимагає `^[A-Za-z][A-Za-z0-9_]{0,63}$`
 * (`CreatePermitTypeRequest.code`), тому пробіли між словами зникають (`Noise permit` → `NoisePermit`).
 */

/** Допустимий код типу (дзеркало серверного правила). */
export const TypeCodePattern = /^[A-Za-z][A-Za-z0-9_]{0,63}$/;

/** Код із назви; порожній рядок, якщо в назві немає жодної латинської літери чи цифри. */
export function deriveTypeCode(name: string): string {
  return name
    .replace(/[^A-Za-z0-9 ]/g, '')
    .replace(/(?:^| +)([A-Za-z0-9])/g, (_match, first: string) => first.toUpperCase())
    .replace(/ +/g, '');
}

/** Чи підходить код серверу. */
export function isValidTypeCode(code: string): boolean {
  return TypeCodePattern.test(code);
}

/** Що не так з назвою нового типу; `null` — все гаразд. */
export type NewTypeProblem = 'required' | 'codeInvalid' | 'nameTaken' | 'codeTaken';

/** Тип, який уже є: код і назви всіма мовами. */
export interface ExistingType {
  readonly code: string;
  readonly nameL10n: Readonly<Record<string, string>>;
}

/**
 * Перевірка назви перед заведенням типу (макет: назва обов'язкова, дубль назви — «A type with this name
 * exists»). Додатково: код з назви мусить підходити серверу й бути вільним — інакше сервер відповів би
 * `409 ECR-PRM-4091` уже на кроці підсумку.
 */
export function checkNewTypeName(name: string, existing: readonly ExistingType[]): NewTypeProblem | null {
  const trimmed = name.trim();
  if (trimmed.length === 0) return 'required';

  const wanted = normalizeTypeName(trimmed);
  if (existing.some((item) => Object.values(item.nameL10n).some((other) => normalizeTypeName(other) === wanted))) {
    return 'nameTaken';
  }

  const code = deriveTypeCode(trimmed);
  if (!isValidTypeCode(code)) return 'codeInvalid';

  return existing.some((item) => item.code.toLocaleLowerCase() === code.toLocaleLowerCase()) ? 'codeTaken' : null;
}

/** Назва для порівняння «така вже є»: без крайніх пробілів і регістру. */
export function normalizeTypeName(name: string): string {
  return name.trim().toLocaleLowerCase();
}
