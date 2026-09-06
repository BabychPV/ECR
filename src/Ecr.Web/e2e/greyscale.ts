import { PNG } from 'pngjs';

/**
 * Структурна різниця двох знімків після знеколірення (`D-140`, гейт 4).
 *
 * ⛔ Це `ФВ-14.18`, записана як предикат. «Колір не є єдиним носієм» —
 * твердження про те, що лишиться, коли колір прибрати; отже прибираємо його
 * буквально і дивимося, чи лишилася різниця.
 *
 * ⚠ Нормалізація яскравості — ключовий крок і найлегший для пропуску. Без неї
 * дві комірки різного тону дадуть різницю в кожному пікселі, і гейт буде
 * зеленим на палітрі, де форми немає взагалі: він міряв би колір, який щойно
 * оголосили несуттєвим.
 */

/** Порядок кроків — той, що в директиві №04 §7.2. */
export interface Sample {
  name: string;
  png: Buffer;
}

/** Яскравість за Rec.709 — та сама, що в `filter: grayscale()`. */
function luminance(r: number, g: number, b: number): number {
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/**
 * Знеколірює і нормалізує: середнє → 128, стандартне відхилення → 40.
 *
 * ⛔ Нормалізується КОЖНА обрізка окремо. Спільна нормалізація лишила б
 * різницю загального тону — тобто той самий колір, тільки в сірому.
 */
export function normalize(png: Buffer): { data: Uint8Array; width: number; height: number } {
  const image = PNG.sync.read(png);
  const grey = new Float64Array(image.width * image.height);

  for (let i = 0; i < grey.length; i++) {
    const offset = i * 4;
    grey[i] = luminance(
      image.data[offset] ?? 0,
      image.data[offset + 1] ?? 0,
      image.data[offset + 2] ?? 0,
    );
  }

  const mean = grey.reduce((sum, value) => sum + value, 0) / grey.length;
  const variance = grey.reduce((sum, value) => sum + (value - mean) ** 2, 0) / grey.length;
  const deviation = Math.sqrt(variance);

  // ⚠ Однорідна обрізка (жодного носія) має нульове відхилення; ділити на
  // нуль не можна, і множник лишається одиничним — така обрізка й має
  // лишитися однорідною, щоб гейт її спіймав.
  const scale = deviation < 1e-6 ? 1 : 40 / deviation;

  const data = new Uint8Array(grey.length);
  for (let i = 0; i < grey.length; i++) {
    data[i] = Math.min(255, Math.max(0, Math.round(128 + ((grey[i] ?? 0) - mean) * scale)));
  }

  return { data, width: image.width, height: image.height };
}

/**
 * Частка пікселів, що змінилися, **без нормалізації**.
 *
 * ⛔ Окрема функція, а не параметр `structuralDifference`. Вони міряють
 * протилежне, і плутати їх дорого:
 *   — `structuralDifference` прибирає тон навмисно (`ФВ-14.18`: колір не
 *     є єдиним носієм) і відповідає на питання «чи лишиться різниця без
 *     кольору»;
 *   — ця відповідає на питання «чи змінилося щось видиме взагалі».
 *
 * ⚠ Написана після реального падіння: кільце фокуса — це ЗМІНА ТОНУ межі
 * (`rgb(206,212,218)` → `rgb(84,116,180)`), і нормалізація прибирала її
 * дочиста. Вимір давав рівно 0.00 %, тобто перевірка доповідала про
 * відсутнє кільце там, де воно є, — і це був би дефект перевірки, який
 * змусив би «виправляти» справний код.
 */
export function rawDifference(first: Buffer, second: Buffer): number {
  const a = PNG.sync.read(first);
  const b = PNG.sync.read(second);

  if (a.width !== b.width || a.height !== b.height) {
    throw new Error(
      `Обрізки різного розміру: ${a.width}×${a.height} проти ${b.width}×${b.height}.`,
    );
  }

  let different = 0;
  const pixels = a.width * a.height;

  for (let i = 0; i < pixels; i++) {
    const offset = i * 4;
    const first709 = luminance(a.data[offset] ?? 0, a.data[offset + 1] ?? 0, a.data[offset + 2] ?? 0);
    const second709 = luminance(b.data[offset] ?? 0, b.data[offset + 1] ?? 0, b.data[offset + 2] ?? 0);

    if (Math.abs(first709 - second709) > 12) different++;
  }

  return different / pixels;
}

/**
 * Частка пікселів, що відрізняються більш ніж на 12 з 255.
 *
 * ⚠ Поріг 12 — це помітна оку різниця в сірому, але вища за шум згладжування.
 * Нижчий поріг зарахував би різницю в антиаліасингу тексту, і гейт був би
 * зеленим на двох однакових комірках із різним кернінгом.
 */
export function structuralDifference(first: Buffer, second: Buffer): number {
  const a = normalize(first);
  const b = normalize(second);

  if (a.width !== b.width || a.height !== b.height) {
    throw new Error(
      `Обрізки різного розміру: ${a.width}×${a.height} проти ${b.width}×${b.height}. ` +
        'Фікстура має давати однакову геометрію — інакше вимір нічого не означає.',
    );
  }

  let different = 0;
  for (let i = 0; i < a.data.length; i++) {
    if (Math.abs((a.data[i] ?? 0) - (b.data[i] ?? 0)) > 12) different++;
  }

  return different / a.data.length;
}
