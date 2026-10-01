/**
 * Перелік поясів IANA.
 *
 * ⛔ `supportedValuesOf('timeZone')` віддає САМЕ ідентифікатори IANA
 * (`Asia/Atyrau`) — інших сервер не приймає (`ECR-CFG-4221`, директива ПК-1
 * №06 §3). Вільного введення тут немає навмисно: пояс вічний (`ФВ-1.1a`), і
 * опечатка в ньому стала б вічною властивістю проєкту.
 *
 * ⚠ `supportedValuesOf` є не всюди (і немає в старих середовищах). Без
 * запасного варіанта поле лишалося б порожнім, а проєкт — нествореним: сервер
 * відхиляє створення без поясу.
 */
export function timeZones(): string[] {
  const supported = (Intl as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
  const all = typeof supported === 'function' ? supported('timeZone') : [];

  // Атирау першим: майданчик NCOC (+05:00 без переходів, F-4).
  const fallback = ['Asia/Atyrau', 'Asia/Aqtau', 'Asia/Almaty', 'Asia/Oral', 'Europe/London', 'UTC'];

  return all.length > 0 ? all : fallback;
}
