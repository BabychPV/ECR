/**
 * Дата без часу ↔ машинний рядок `yyyy-MM-dd` (L9-17).
 *
 * Сервер віддає й приймає календарні дати (`asOf`, `validFrom`, межі фільтрів) саме цим рядком, а
 * поле дати (`DateInput`) працює з `Date`. До L9-17 цей перехід був переписаний на дев'яти екранах —
 * дві функції `todayIso` з різними тілами, `parseDateOnly` то з перевіркою формату, то без.
 *
 * ⛔ Обидва напрямки — за МІСЦЕВИМ календарем, а не UTC:
 *   - `toISOString().slice(0, 10)` о 00:30 у додатному поясі дає ВЧОРАШНІЙ день;
 *   - `new Date('2026-03-01')` — північ UTC, тобто в мінусовому поясі — 28 лютого.
 * Складники дати (`getFullYear`/`getMonth`/`getDate`) для машинного формату брати руками дозволено
 * навмисно (`D15-09`, `eslint.config.js`); показ дати людині — `formatDate`, не цей модуль.
 */

const DateOnly = /^(\d{4})-(\d{2})-(\d{2})$/;

function pad(part: number, width: number): string {
  return String(part).padStart(width, '0');
}

/**
 * `Date` → `yyyy-MM-dd` за місцевим календарем; `null` → `null`.
 *
 * ⚠ Рік доповнюється до чотирьох цифр: `0099-01-01`, а не `99-01-01`, який сервер не розбере.
 */
export function formatDateOnly(date: Date): string;
export function formatDateOnly(date: Date | null): string | null;
export function formatDateOnly(date: Date | null): string | null {
  if (date === null) return null;

  return `${pad(date.getFullYear(), 4)}-${pad(date.getMonth() + 1, 2)}-${pad(date.getDate(), 2)}`;
}

/**
 * `yyyy-MM-dd` → `Date` опівночі МІСЦЕВОГО часу; `null` — порожньо, не той формат чи не дата.
 *
 * ⚠ Розбір рядком `…T00:00:00`, а не `new Date(y, m, d)`: конструктор читає роки 0–99 як 1900–1999.
 * ⚠ Неіснуючий день (`2026-02-30`) — `null`, а не мовчки друге березня: рушій перекочує його в
 * наступний місяць, і поле показало б інший день, ніж прийшов.
 */
export function parseDateOnly(value: string | null | undefined): Date | null {
  const parts = value === null || value === undefined ? null : DateOnly.exec(value);
  if (parts === null) return null;

  const parsed = new Date(`${parts[0]}T00:00:00`);

  return parsed.getMonth() + 1 === Number(parts[2]) && parsed.getDate() === Number(parts[3]) ? parsed : null;
}

/** Сьогоднішня дата клієнта `yyyy-MM-dd` — календарний день людини, а не UTC. */
export function todayDateOnly(now: Date = new Date()): string {
  return formatDateOnly(now);
}
