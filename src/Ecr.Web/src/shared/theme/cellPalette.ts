/**
 * Палітра користувацького кольору комірки (L4): це ДАНІ вибору, а не кольори
 * інтерфейсу, тож літерали живуть тут, у каталозі теми, а не в компонентах.
 */

// Як в Excel: рядок теми, п'ять рядків відтінків, стандартні кольори.

const ThemeBase = [
  '#ffffff', '#000000', '#e7e6e6', '#44546a', '#4472c4',
  '#ed7d31', '#a5a5a5', '#ffc000', '#5b9bd5', '#70ad47',
] as const;

export const StandardColours = [
  '#c00000', '#ff0000', '#ffc000', '#ffff00', '#92d050',
  '#00b050', '#00b0f0', '#0070c0', '#002060', '#7030a0',
] as const;

/** Змішування з білим (`amount` > 0) чи чорним (< 0), як відтінки теми Excel. */
function shade(hex: string, amount: number): string {
  const mix = amount >= 0 ? 255 : 0;
  const k = Math.abs(amount);
  const part = (shift: number): string =>
    Math.round(((Number.parseInt(hex.slice(1), 16) >> shift) & 0xff) * (1 - k) + mix * k)
      .toString(16)
      .padStart(2, '0');

  return `#${part(16)}${part(8)}${part(0)}`;
}

/** Рядки відтінків під рядком теми: світліші на 80/60/40 %, темніші на 25/50 %. */
export const ThemeShades: readonly (readonly string[])[] = [0.8, 0.6, 0.4, -0.25, -0.5].map((amount) =>
  ThemeBase.map((hex) => shade(hex, amount)),
);

export const ThemeColours: readonly string[] = ThemeBase;

export function isHexColour(value: string): boolean {
  return /^#[0-9a-f]{6}$/.test(value);
}
