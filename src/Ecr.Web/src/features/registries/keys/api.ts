import { apiFetch, EcrApiError } from '@/api/client';
import type { components } from '@/api/schema';

/** Запит живої перевірки дублікатів ключа (`POST …/keys/check`, RT-11). */
export type RegistryKeyCheckRequest = components['schemas']['RegistryKeyCheckRequest'];

/** Результат перевірки: скільки записів перевірено, скільки груп дублікатів, приклади. */
export type RegistryKeyCheckResponse = components['schemas']['RegistryKeyCheckResponse'];

/** Одне значення ключа, яке вже мають кілька записів. */
export type RegistryKeyDuplicate = components['schemas']['RegistryKeyDuplicateDto'];

/*
 * ⛔ Адреса записана повністю, а не збирається з помічника: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` шукає літерал `/api/v1/…`
 * разом із методом поруч (див. `features/registries/api.ts`).
 */

/**
 * Жива перевірка дублікатів майбутнього ключа на наявних записах — ДО
 * збереження опису (FEATURE-REGISTRY-TABLES §4.5, `AC-4`).
 *
 * ⛔ Сервер перевіряє тим самим алгоритмом, що й публікація ключа: «груп 0» тут
 * означає, що публікація не відмовить `409 existingDuplicates` на тих самих
 * даних. Показувати ж приклади треба саме звідси, а не чекати відмови: людина
 * має побачити, ЯКІ записи заважають, до того, як натисне «Опублікувати».
 */
export function checkRegistryKey(
  code: string,
  body: RegistryKeyCheckRequest,
): Promise<RegistryKeyCheckResponse> {
  return apiFetch<RegistryKeyCheckResponse>(
    `/api/v1/registries/${encodeURIComponent(code)}/keys/check`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    },
  );
}

/** Код відмови «конфлікт складеного ключа» (`ECR-REG-4092`). */
const KEY_CONFLICT = 'ECR-REG-4092';

/** Випадок відмови: ключ публікується на даних, де вже є дублікати. */
const EXISTING_DUPLICATES = 'err.ECR-REG-4092.existingDuplicates';

/**
 * Приклади дублікатів із відмови публікації опису (`409 existingDuplicates`).
 *
 * ⛔ `null`, а не порожній перелік, коли відмова інша: «прикладів немає» —
 * твердження про дані, якого сервер у ТІЙ відмові не робив. Той самий код
 * `ECR-REG-4092` несе й `keyTaken` (запис запису), і там прикладів немає за
 * змістом.
 */
export function existingDuplicates(error: unknown): readonly RegistryKeyDuplicate[] | null {
  if (!(error instanceof EcrApiError) || error.problem.errorCode !== KEY_CONFLICT) {
    return null;
  }

  const extensions = error.problem.extensions2;
  if (extensions?.['messageKey'] !== EXISTING_DUPLICATES) return null;

  const sample = extensions['sample'];
  return Array.isArray(sample) ? (sample as RegistryKeyDuplicate[]) : [];
}
