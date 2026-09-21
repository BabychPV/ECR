import { safeReturnPath } from '@/pages/safeReturnPath';
import { currentDocumentId, pendingCount } from './pendingStore';

/**
 * Слід незбережених правок, які загинули разом із сесією.
 *
 * ⛔ Що ламалося. На `401` клієнт робить `window.location.assign('/login?…')`
 * (`api/client.ts`) — повне перезавантаження. Незбережені правки живуть лише в
 * пам'яті (`pendingStore.ts`), і зберегти їх на сервері вже нема чим: сесію
 * відхилено. Вони зникали МОВЧКИ — людина бачила форму входу й не знала, що
 * введене пропало.
 *
 * ⚠ Тут лежить лише ФАКТ втрати (скільки, у якому документі), а не самі
 * значення: відновлення з базовою версією і показом конфліктів — наступний
 * крок, і воно вимагає змін у ядрі сітки.
 *
 * ⛔ `sessionStorage`, не `localStorage`: слід має зникнути разом із вкладкою,
 * а не чекати наступного, хто сяде за цей комп'ютер.
 *
 * ⛔ Ключ містить `userId` власника правок. Сторінка входу читає слід лише
 * ПІСЛЯ входу і лише свого користувача — інший на спільному комп'ютері не
 * дізнається навіть, що в чужому документі щось втрачено.
 */
export const LostEditsKeyPrefix = 'ecr.lostEdits.v1:';

/** Ключ сховища для користувача. */
export function lostEditsKey(userId: number): string {
  return `${LostEditsKeyPrefix}${String(userId)}`;
}

/** Що втрачено. */
export interface LostEdits {
  readonly documentId: number;
  readonly count: number;
  /** Звідки перенаправили на вхід — щоб повернути людину до документа. */
  readonly from: string;
}

function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.sessionStorage;
  } catch {
    // Приватний режим / заблоковані дані сайту: сліду не буде, але й падіння.
    return null;
  }
}

/**
 * Записує слід, якщо є що втрачати. Кличеться перед перенаправленням на вхід.
 *
 * ⚠ Власник невідомий (профіль ще не завантажився) — НЕ пишемо нічого: слід
 * без власника показався б будь-кому.
 */
export function recordLostEdits(ownerUserId: number | undefined, from: string): void {
  const documentId = currentDocumentId();
  const count = pendingCount();
  if (ownerUserId === undefined || documentId === null || count === 0) return;

  const value: LostEdits = { documentId, count, from };
  try {
    storage()?.setItem(lostEditsKey(ownerUserId), JSON.stringify(value));
  } catch {
    // Переповнене сховище — не привід зірвати перенаправлення.
  }
}

/** Чи лежить у вкладці бодай один слід (без читання чужого вмісту). */
export function anyLostEdits(): boolean {
  const store = storage();
  if (store === null) return false;

  for (let i = 0; i < store.length; i++) {
    if (store.key(i)?.startsWith(LostEditsKeyPrefix) === true) return true;
  }

  return false;
}

/** Забирає слід користувача: читає і стирає, щоб показати рівно раз. */
export function takeLostEdits(userId: number): LostEdits | null {
  const store = storage();
  if (store === null) return null;

  const key = lostEditsKey(userId);
  const raw = store.getItem(key);
  if (raw === null) return null;
  store.removeItem(key);

  try {
    const value = JSON.parse(raw) as Partial<LostEdits>;
    if (typeof value.documentId !== 'number' || typeof value.count !== 'number') return null;

    return {
      documentId: value.documentId,
      count: value.count,
      // ⛔ Одна перевірка адреси повернення на весь застосунок — та сама, що
      // й для `?from=` на сторінці входу: власна копія тут пропускала `/\evil`
      // і керівні символи, які `safeReturnPath` відсікає.
      from: safeReturnPath(typeof value.from === 'string' ? value.from : null),
    };
  } catch {
    return null;
  }
}
