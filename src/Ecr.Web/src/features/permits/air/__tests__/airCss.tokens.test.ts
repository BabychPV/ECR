import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * U-8 (аудит 2026-11, ФВ-14.11): статус у `air.css` — токенами теми (`statusError` / `statusWarning`, контраст —
 * `contrast.test.ts`), а не сирими hex чи голою палітрою Mantine (`red`/`orange`). CSS лінтер не бачить, тому — цей
 * сторож. Коментарі не рахуються: у них назви колишніх відтінків пояснюють заміри.
 *
 * Мутаційний доказ: повернути `color: #a61e1e` чи `var(--mantine-color-red-4)` — тест червоний.
 */
const css = readFileSync(resolve(__dirname, '../air.css'), 'utf8');
const code = css.replace(/\/\*[\s\S]*?\*\//g, '');

describe('air.css: статусні кольори — токенами теми', () => {
  it('немає сирих hex-кольорів поза коментарями', () => {
    expect(code.match(/#[0-9a-f]{3,8}\b/gi) ?? []).toEqual([]);
  });

  it('немає голої палітри red/orange Mantine', () => {
    expect(code.match(/--mantine-color-(red|orange)-/g) ?? []).toEqual([]);
  });

  it('позначка серйозності бере статусні токени', () => {
    expect(code).toMatch(/\.pi-sev\[data-severity='Error'\]\s*\{\s*color:\s*var\(--mantine-color-statusError-text\)/);
    expect(code).toMatch(/\.pi-sev\[data-severity='Warning'\]\s*\{\s*color:\s*var\(--mantine-color-statusWarning-text\)/);
  });
});
