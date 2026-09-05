/**
 * Вимірювання кольору: CIEDE2000 і симуляції дихромазії.
 *
 * ⛔ Потрібне тому, що `ФВ-14.18` не можна перевірити оком. Людина з
 * нормальним зором **не бачить того, що бачить оператор із дейтеранопією** — а
 * це близько 6 % чоловіків. Директива №04 §6 показала це числом: рамки
 * `calculated` і `rounded` у первісній палітрі різнилися на ΔE00 = 24.1 для
 * нормального зору і на **5.9** для дейтеранопії, тобто були майже однакові
 * для кожного шістнадцятого користувача.
 *
 * ⚠ Обидва алгоритми — стандартні й перевіряються тестом на відомих
 * значеннях: без такого калібрування вони показували б власну помилку, а всі
 * гейти над ними були б зеленими на будь-якій палітрі.
 */

/** Колір у лінійному RGB, 0…1. */
type Linear = readonly [number, number, number];

/** Колір у CIELAB. */
export interface Lab {
  L: number;
  a: number;
  b: number;
}

/** Вид дихромазії. */
export type Dichromacy = 'protanopia' | 'deuteranopia' | 'tritanopia';

/** Розбирає `#rrggbb` у складові 0…1. */
function channels(hex: string): [number, number, number] {
  const clean = hex.replace('#', '');
  const full =
    clean.length === 3
      ? clean
          .split('')
          .map((c) => c + c)
          .join('')
      : clean;

  return [0, 2, 4].map((i) => parseInt(full.slice(i, i + 2), 16) / 255) as [
    number,
    number,
    number,
  ];
}

/** Знімає гамму sRGB. */
function linearize(value: number): number {
  return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
}

/** Накладає гамму sRGB назад. */
function encode(value: number): number {
  const clamped = Math.min(1, Math.max(0, value));

  return clamped <= 0.0031308 ? clamped * 12.92 : 1.055 * clamped ** (1 / 2.4) - 0.055;
}

function toLinear(hex: string): Linear {
  const [r, g, b] = channels(hex);

  return [linearize(r), linearize(g), linearize(b)];
}

function toHex(linear: Linear): string {
  return (
    '#' +
    linear
      .map((c) => Math.round(encode(c) * 255).toString(16).padStart(2, '0'))
      .join('')
  );
}

/**
 * Симуляція дихромазії за Viénot–Brettel–Mollon (1999).
 *
 * ⚠ Працює на ЛІНІЙНОМУ RGB. Застосувати матриці до значень із гаммою — типова
 * помилка, і вона дає правдоподібні, але неправильні кольори: перевірка
 * лишається зеленою там, де користувач бачить два однакові стани.
 *
 * ⚠ Це модель, а не істина: дихромазія буває неповною, і монітори різні.
 * Число з неї — не «скільки саме бачить людина», а порівнюваний показник, за
 * яким одну палітру можна поставити поруч з іншою.
 */
export function simulate(hex: string, kind: Dichromacy): string {
  const [r, g, b] = toLinear(hex);

  // Матриці Хант–Пойнтер–Естевеза у нормуванні VBM.
  const l = 17.8824 * r + 43.5161 * g + 4.11935 * b;
  const m = 3.45565 * r + 27.1554 * g + 3.86714 * b;
  const s = 0.0299566 * r + 0.184309 * g + 1.46709 * b;

  // Проєкція на площину, доступну дихромату: втрачений канал відновлюється з
  // двох інших.
  let [ls, ms, ss] = [l, m, s];
  if (kind === 'protanopia') ls = 2.02344 * m - 2.52581 * s;
  if (kind === 'deuteranopia') ms = 0.494207 * l + 1.24827 * s;
  if (kind === 'tritanopia') ss = -0.395913 * l + 0.801109 * m;

  return toHex([
    0.080944478 * ls - 0.130504409 * ms + 0.116721066 * ss,
    -0.0102485335 * ls + 0.0540193266 * ms - 0.113614708 * ss,
    -0.000365296938 * ls - 0.00412161469 * ms + 0.693511405 * ss,
  ]);
}

/** Переводить у CIELAB для білої точки D65. */
export function toLab(hex: string): Lab {
  const [r, g, b] = toLinear(hex);

  // sRGB → XYZ (D65).
  const x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
  const y = 0.2126729 * r + 0.7151522 * g + 0.072175 * b;
  const z = 0.0193339 * r + 0.119192 * g + 0.9503041 * b;

  const white = { x: 0.95047, y: 1.0, z: 1.08883 };
  const f = (t: number): number => (t > 216 / 24389 ? Math.cbrt(t) : (841 / 108) * t + 4 / 29);

  const fx = f(x / white.x);
  const fy = f(y / white.y);
  const fz = f(z / white.z);

  return { L: 116 * fy - 16, a: 500 * (fx - fy), b: 200 * (fy - fz) };
}

/**
 * CIEDE2000 — відстань між кольорами, узгоджена зі сприйняттям.
 *
 * ⚠ Не евклідова відстань у Lab: вона переоцінює різницю в насичених жовтих і
 * недооцінює в синіх. Саме тому для порогів беруть ΔE00, а не ΔE76.
 */
export function deltaE00(first: string, second: string): number {
  const one = toLab(first);
  const two = toLab(second);

  const kL = 1;
  const kC = 1;
  const kH = 1;

  const c1 = Math.hypot(one.a, one.b);
  const c2 = Math.hypot(two.a, two.b);
  const meanC = (c1 + c2) / 2;

  const g = 0.5 * (1 - Math.sqrt(meanC ** 7 / (meanC ** 7 + 25 ** 7)));

  const a1 = (1 + g) * one.a;
  const a2 = (1 + g) * two.a;

  const cp1 = Math.hypot(a1, one.b);
  const cp2 = Math.hypot(a2, two.b);

  const hp1 = angle(one.b, a1);
  const hp2 = angle(two.b, a2);

  const dL = two.L - one.L;
  const dC = cp2 - cp1;

  let dhp = 0;
  if (cp1 * cp2 !== 0) {
    dhp = hp2 - hp1;
    if (dhp > 180) dhp -= 360;
    if (dhp < -180) dhp += 360;
  }

  const dH = 2 * Math.sqrt(cp1 * cp2) * Math.sin(rad(dhp) / 2);

  const meanL = (one.L + two.L) / 2;
  const meanCp = (cp1 + cp2) / 2;

  let meanHp = hp1 + hp2;
  if (cp1 * cp2 !== 0) {
    if (Math.abs(hp1 - hp2) > 180) meanHp += hp1 + hp2 < 360 ? 360 : -360;
    meanHp /= 2;
  }

  const t =
    1 -
    0.17 * Math.cos(rad(meanHp - 30)) +
    0.24 * Math.cos(rad(2 * meanHp)) +
    0.32 * Math.cos(rad(3 * meanHp + 6)) -
    0.2 * Math.cos(rad(4 * meanHp - 63));

  const sL = 1 + (0.015 * (meanL - 50) ** 2) / Math.sqrt(20 + (meanL - 50) ** 2);
  const sC = 1 + 0.045 * meanCp;
  const sH = 1 + 0.015 * meanCp * t;

  const rt =
    -2 *
    Math.sqrt(meanCp ** 7 / (meanCp ** 7 + 25 ** 7)) *
    Math.sin(rad(60 * Math.exp(-(((meanHp - 275) / 25) ** 2))));

  return Math.sqrt(
    (dL / (kL * sL)) ** 2 +
      (dC / (kC * sC)) ** 2 +
      (dH / (kH * sH)) ** 2 +
      rt * (dC / (kC * sC)) * (dH / (kH * sH)),
  );
}

/** Кут у градусах 0…360. */
function angle(b: number, a: number): number {
  if (a === 0 && b === 0) return 0;

  const degrees = (Math.atan2(b, a) * 180) / Math.PI;

  return degrees >= 0 ? degrees : degrees + 360;
}

function rad(degrees: number): number {
  return (degrees * Math.PI) / 180;
}
