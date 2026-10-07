import { formatDate, formatDateTime } from '@/shared/format';

/*
 * Момент у поясі МАЙДАНЧИКА — одне місце для всіх екранів, що показують межі
 * періоду (Periods і «closes in N days · дата» на Documents). Раніше правило
 * жило лише в `PeriodsPage`, а Documents форматувала ту саму межу поясом
 * браузера — і дати розходилися (приймальна RC8 №6, P2-1).
 */

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
  const shown = new Date((wallAt ?? at) - (inclusiveEnd ? 1 : 0));
  const timeZone = wallAt === null ? formattableZone(zone) : 'UTC';
  return dateOnly
    ? formatDate(shown, { dateStyle: 'medium', timeZone })
    : formatDateTime(shown, { dateStyle: 'medium', timeStyle: 'short', timeZone });
}
