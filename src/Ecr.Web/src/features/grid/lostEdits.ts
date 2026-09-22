import { safeReturnPath } from '@/shared/safeReturnPath';
import { currentDocumentId, pendingCount, pendingSlices } from './pendingStore';
import type { PendingEdit } from './useCellPatch';

/**
 * Незбережені правки, які загинули разом із сесією, — і самі ПРАВКИ, а не
 * лише факт втрати (`ФВ-3.6` «відновлення після втрати з'єднання», `D14-12`).
 *
 * ⛔ Що ламалося. На `401` клієнт робить `window.location.assign('/login?…')`
 * (`api/client.ts`) — повне перезавантаження. Незбережені правки живуть лише в
 * пам'яті (`pendingStore.ts`), і зберегти їх на сервері вже нема чим: сесію
 * відхилено. Вони зникали МОВЧКИ — людина бачила форму входу й не знала, що
 * введене пропало.
 *
 * ✎ Перший крок клав сюди лише ФАКТ (документ, кількість, адреса повернення):
 * сторінка входу казала «N змін втрачено», і єдине, що лишалося людині, —
 * ввести їх заново. Тепер тут лежать самі значення разом із адресою зрізу
 * (`tableInstanceId`, `periodKey`) і `baseVersion` кожного рядка, тож
 * `RestoreEditsBanner` на документі може їх ПОВЕРНУТИ — а де версія рядка за
 * цей час змінилася, чесно показати конфлікт замість мовчазного перезапису.
 *
 * ⛔ `sessionStorage`, не `localStorage`: слід має зникнути разом із вкладкою,
 * а не чекати наступного, хто сяде за цей комп'ютер. Це не стиль: у сліді
 * тепер лежать ВВЕДЕНІ ЧИСЛА звітності, і переживати вкладку вони не мають
 * права.
 *
 * ⛔ Ключ містить `userId` власника правок. Сторінка входу читає слід лише
 * ПІСЛЯ входу і лише свого користувача — інший на спільному комп'ютері не
 * дізнається навіть, що в чужому документі щось втрачено, і тим паче не
 * застосує чужих значень до свого документа.
 *
 * ⚠ Версія ключа `v2`, бо форма запису змінилася несумісно. Стара (`v1`)
 * лишилася б у вкладці, у якій встиг попрацювати попередній бандл, і читалася
 * б як «правок нуль» — банер без жодної правки гірший за відсутній.
 */
export const LostEditsKeyPrefix = 'ecr.lostEdits.v2:';

/** Ключ сховища для користувача. */
export function lostEditsKey(userId: number): string {
  return `${LostEditsKeyPrefix}${String(userId)}`;
}

/**
 * Скільки правок щонайбільше лягає у слід.
 *
 * ⚠ Число назване, а не «скільки вийде», і ось із чого воно взялося. Одна
 * правка в JSON — це ~110 байт (`rowKey` — GUID у форматі `N`, `baseVersion` —
 * base64 восьми байтів), тобто 500 правок ≈ 55 КБ при типовій квоті
 * `sessionStorage` 5 МБ на походження. Запас двократний навіть якщо у вкладці
 * лежать сліди кількох користувачів і решта наших ключів.
 *
 * ⛔ Межа саме на ПРАВКИ, а не на байти: `setItem` кидає `QuotaExceededError`
 * уже після того, як ми зібрали й серіалізували весь слід, і ловити квоту
 * означало б лишити людину без сліду взагалі (гілка `catch` нижче). Реальний
 * обсяг незбереженого за 500 мс тиші — одиниці комірок; сотні бувають лише
 * після вставки з Excel, і 500 покриває вставку 100×5.
 *
 * ⚠ Що при переповненні. Слід несе ПЕРШІ 500 правок (порядок сховища —
 * порядок уведення), а `count` лишається ПОВНИМ числом незбереженого, тож
 * банер каже «відновлено X із Y», а не вдає, що втрачено рівно стільки,
 * скільки вміщено. Мовчки зрізати різницю не можна: це та сама мовчазна
 * втрата, від якої весь цей модуль.
 */
export const MaxRestoredEdits = 500;

/** Правки одного зрізу — адреса плюс самі значення. */
export interface RestoreSlice {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly edits: readonly PendingEdit[];
}

/** Що втрачено. */
export interface LostEdits {
  readonly documentId: number;
  /** Скільки правок було незбережено НАСПРАВДІ (може бути більше за вміщені). */
  readonly count: number;
  /** Звідки перенаправили на вхід — щоб повернути людину до документа. */
  readonly from: string;
  /** Правки, які вдалося зберегти, — не більше `MaxRestoredEdits`. */
  readonly slices: readonly RestoreSlice[];
}

/** Скільки правок справді можна відновити (вміщені у слід). */
export function restorableCount(lost: LostEdits): number {
  let total = 0;
  for (const slice of lost.slices) total += slice.edits.length;

  return total;
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
 * Обрізає правки документа до `MaxRestoredEdits`, не ламаючи зрізів.
 *
 * ⚠ Зріз, який не вмістився ЦІЛКОМ, обрізається по комірках, а не
 * відкидається: половина таблиці краще за жодної, а що саме не вмістилося,
 * видно з різниці `count` і `restorableCount`.
 */
function capSlices(slices: ReturnType<typeof pendingSlices>): RestoreSlice[] {
  const capped: RestoreSlice[] = [];
  let left = MaxRestoredEdits;

  for (const slice of slices) {
    if (left <= 0) break;

    const edits = slice.edits.slice(0, left);
    left -= edits.length;

    capped.push({
      tableInstanceId: slice.tableInstanceId,
      periodKey: slice.periodKey,
      edits,
    });
  }

  return capped;
}

/**
 * Записує слід, якщо є що втрачати. Кличеться перед перенаправленням на вхід.
 *
 * ⚠ Власник невідомий (профіль ще не завантажився) — НЕ пишемо нічого: слід
 * без власника показався б будь-кому, а тепер ще й дав би застосувати чужі
 * числа.
 */
export function recordLostEdits(ownerUserId: number | undefined, from: string): void {
  const documentId = currentDocumentId();
  const count = pendingCount();
  if (ownerUserId === undefined || documentId === null || count === 0) return;

  const value: LostEdits = { documentId, count, from, slices: capSlices(pendingSlices()) };
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

/**
 * Читає слід користувача, НЕ стираючи його.
 *
 * ⛔ Саме читання без стирання, і це не дрібниця. Поки слід ніс лише факт,
 * показати його один раз було достатньо — ніщо інше з ним не робили. Тепер
 * слід — це самі дані, і між сторінкою входу й документом він мусить
 * ДОЖИТИ: стирання на вході знищило б правки рівно тоді, коли людина
 * погодилася їх повернути. Стирає його той, хто розпорядився правками, —
 * `clearLostEdits` (застосували, відхилили).
 */
export function peekLostEdits(userId: number): LostEdits | null {
  const store = storage();
  if (store === null) return null;

  const raw = store.getItem(lostEditsKey(userId));
  if (raw === null) return null;

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
      slices: parseSlices(value.slices),
    };
  } catch {
    return null;
  }
}

/** Стирає слід користувача: правки застосовано або свідомо відхилено. */
export function clearLostEdits(userId: number): void {
  storage()?.removeItem(lostEditsKey(userId));
}

/**
 * Розбір збережених правок.
 *
 * ⛔ Перевіряється КОЖНЕ поле, а не сама лише наявність масиву. Вміст
 * `sessionStorage` — це рядок, який пережив перезавантаження сторінки й міг
 * бути записаний іншим бандлом або відредагований руками; правка з `rowKey`
 * типу `object` доїхала б звідси аж до тіла `PATCH`, і сервер відмовив би
 * чимось неочікуваним замість того, щоб ми просто її не взяли.
 */
function parseSlices(raw: unknown): RestoreSlice[] {
  if (!Array.isArray(raw)) return [];

  const slices: RestoreSlice[] = [];

  for (const item of raw as readonly Partial<RestoreSlice>[]) {
    if (typeof item?.tableInstanceId !== 'number' || typeof item.periodKey !== 'number') continue;
    if (!Array.isArray(item.edits)) continue;

    const edits = (item.edits as readonly unknown[]).filter(isEdit);
    if (edits.length === 0) continue;

    slices.push({
      tableInstanceId: item.tableInstanceId,
      periodKey: item.periodKey,
      edits,
    });
  }

  return slices;
}

function isEdit(value: unknown): value is PendingEdit {
  if (typeof value !== 'object' || value === null) return false;

  const edit = value as Partial<PendingEdit>;

  return (
    typeof edit.rowKey === 'string' &&
    typeof edit.columnCode === 'string' &&
    typeof edit.isEmpty === 'boolean' &&
    (edit.baseVersion === null || typeof edit.baseVersion === 'string') &&
    // ⚠ Саме наявність КЛЮЧА, а не істинність значення: `null` — це «стерти
    // комірку» (`R-B4`), законний намір, а відсутнє поле поїхало б у `PATCH`
    // як «комірку не чіпати» всередині переліку змін.
    'value' in edit
  );
}
