import { formatDate, formatDateTime } from '@/shared/format';

/*
 * Момент у поясі МАЙДАНЧИКА — одне місце для всіх екранів, що показують межі
 * періоду (Periods і «closes in N days · дата» на Documents). Раніше правило
 * жило лише в `PeriodsPage`, а Documents форматувала ту саму межу поясом
 * браузера — і дати розходилися (приймальна RC8 №6, P2-1).
 */

/** Межа доби в мілісекундах. */
export const DayMs = 24 * 60 * 60 * 1000;

/**
 * Повних діб до жорсткого закриття (`endsAt` — виключна межа); `0` — останній
 * день сьогодні. `null` — рядок не розібрався. Єдина формула для «closes in N
 * days» на Periods і на Documents (P2-1).
 */
export function wholeDaysUntil(endsAt: string, now: number): number | null {
  const left = Date.parse(endsAt) - now;
  if (!Number.isFinite(left)) return null;

  return Math.max(0, Math.floor(left / DayMs));
}

/**
 * Годинник зони майданчика — для МАШИННОГО читання складників, не для екрана.
 *
 * ⚠ Локаль тут стала (`en-US`) і це не порушення `D15-09`: з цього
 * форматувальника беруться самі числа (`formatToParts`), і жоден його символ
 * на екран не потрапляє. Правило про локаль продукту стосується того, що
 * ЧИТАЄ людина.
 *
 * ⚠ `try` — бо `timeZoneId` приходить із СЕРВЕРА, а не з нашого коду:
 * невідома `Intl` зона кидає `RangeError`, і без перехоплення один поганий
 * рядок у проєкті знімав би всю сторінку. Запасний варіант названий (UTC), а
 * не прихований.
 */
const zoneClocks = new Map<string, Intl.DateTimeFormat>();

export function zoneClock(timeZoneId: string): Intl.DateTimeFormat {
  const hit = zoneClocks.get(timeZoneId);
  if (hit !== undefined) return hit;

  const options: Intl.DateTimeFormatOptions = {
    hour12: false,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  };

  let made: Intl.DateTimeFormat;

  try {
    made = new Intl.DateTimeFormat('en-US', { ...options, timeZone: timeZoneId });
  } catch {
    made = new Intl.DateTimeFormat('en-US', { ...options, timeZone: 'UTC' });
  }

  zoneClocks.set(timeZoneId, made);

  return made;
}

/**
 * Пояс, яким `Intl` справді вміє форматувати; інакше — UTC (`X-34`).
 *
 * ⚠ Та сама причина, що й `try` у `zoneClock`: `timeZoneId` приходить із
 * сервера, і невідома `Intl` зона кинула б `RangeError` посеред рендера.
 */
function formattableZone(timeZoneId: string): string {
  return zoneClock(timeZoneId).resolvedOptions().timeZone;
}

/** Момент зі зсувом: складники настінного часу майданчика — групи 1…6. */
const WallClock = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?[+-]\d{2}:\d{2}$/;

/**
 * Момент у поясі МАЙДАНЧИКА — для екрана (`X-34`/`F-20`).
 *
 * ⛔ Тут стояв `<Timestamp>`, тобто пояс БРАУЗЕРА. Межі періоду — моменти
 * майданчика (`D-68`): 202601 проєкту на `Asia/Atyrau` (+05:00) відкривається
 * 1 січня 00:00 за Актау, тобто 31 грудня 19:00 UTC, — і адміністратор у UTC
 * бачив «Dec 31, 2025» як початок січня. «Grace until: Feb 14, 9:00 PM» при
 * справжньому 15.02 00:00 +05 — та сама розбіжність, на годину, що вирішує
 * «встиг чи ні».
 *
 * `inclusiveEnd` — межа ВИКЛЮЧНА (`endsAt`), а показати треба останній ДЕНЬ.
 * `null` — рядок не розібрався (показується як є).
 */
export function siteMomentText(value: string, zone: string, dateOnly = false, inclusiveEnd = false): string | null {
  return momentText(value, zone, dateOnly, inclusiveEnd ? 1 : 0);
}

/** Хвилина — на скільки зсувається північ майданчика в `siteUntilText`. */
const MinuteMs = 60 * 1000;

/**
 * ✎ AN-169: ВИКЛЮЧНИЙ строк «до …» (перевідкриття місяця, вікно правок документа) — у поясі майданчика.
 *
 * ⛔ Строк за замовчуванням — північ майданчика (кінець доби, `endOfSiteDayUtc` / сервер при `openUntil = null`), і
 * `siteMomentText` показував його як «until Nov 12, 12:00 AM»: читач бачить 12 листопада і «12:00», тобто
 * полудень або весь день 12-го, хоча правки закриваються на ПОЧАТКУ 12-го. Тому північ показується останньою
 * хвилиною попередньої доби — «until Nov 11, 11:59 PM» / «до 11 нояб. 2026 г., 23:59»: той самий формат дати й часу
 * локалі, без нового ключа каталогу. Будь-який інший час (адміністратор задав 15:00) — як є.
 */
export function siteUntilText(value: string, zone: string): string | null {
  const at = Date.parse(value);
  if (Number.isNaN(at)) return null;

  return momentText(value, zone, false, isSiteMidnight(value, at, zone) ? MinuteMs : 0);
}

/** Чи момент — рівно 00:00:00 настінного часу майданчика (за зсувом у рядку, без нього — за поясом через `Intl`). */
function isSiteMidnight(value: string, at: number, zone: string): boolean {
  const wall = WallClock.exec(value);
  if (wall !== null) return wall[4] === '00' && wall[5] === '00' && (wall[6] ?? '00') === '00' && !/\.\d*[1-9]/.test(value);

  if (new Date(at).getUTCMilliseconds() !== 0) return false;
  const parts = zoneClock(zone).formatToParts(new Date(at));
  const part = (type: Intl.DateTimeFormatPartTypes): string => parts.find((p) => p.type === type)?.value ?? '';
  // `hour12: false` у старих рушіях дає північ як «24», а не «00».
  return (part('hour') === '00' || part('hour') === '24') && part('minute') === '00' && part('second') === '00';
}

function momentText(value: string, zone: string, dateOnly: boolean, shiftMs: number): string | null {
  const at = Date.parse(value);
  if (Number.isNaN(at)) return null;

  /*
   * ⛔ Спершу — НАСТІННИЙ час із самого рядка (`2025-01-01T00:00:00+06:00` →
   * 1 січня 00:00), і лише без зсуву в рядку — пояс проєкту через `Intl`.
   * Причина зміряна живцем: сервер рахує межі своєю базою поясів, браузер —
   * своєю, і вони розходяться (`Asia/Almaty` у свіжому ICU — уже +05:00, у
   * базі Windows-сервера без оновлення поясів 2024 року — ще +06:00). Через `Intl` межа 202501 показувалася б
   * «Dec 31, 2024». Зсув у відповіді — те, як межу порахував САМ сервер, і
   * саме за ним вона застосовується.
   */
  const wall = WallClock.exec(value);
  const wallAt = wall === null
    ? null
    : Date.UTC(Number(wall[1]), Number(wall[2]) - 1, Number(wall[3]), Number(wall[4]), Number(wall[5]), Number(wall[6] ?? '0'));
  const shown = new Date((wallAt ?? at) - shiftMs);
  const timeZone = wallAt === null ? formattableZone(zone) : 'UTC';
  return dateOnly
    ? formatDate(shown, { dateStyle: 'medium', timeZone })
    : formatDateTime(shown, { dateStyle: 'medium', timeStyle: 'short', timeZone });
}
