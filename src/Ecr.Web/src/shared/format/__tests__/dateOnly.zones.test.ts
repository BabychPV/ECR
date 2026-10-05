// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * T7-01: `parseDateOnly` у справжніх поясах браузера — північ проєкту ≠ північ UTC.
 *
 * ⚠ Пояс у потоці `vmThreads` не задати (`process.env.TZ` до рушія не доходить, див.
 * `dateOnly.test.ts`), тому модуль розбирається в ОКРЕМОМУ процесі Node із `TZ` у середовищі —
 * той самий файл `dateOnly.ts`, без копії (типи прибирає сам Node: `--experimental-strip-types`).
 *
 * Пояси — крайні: Kiritimati (+14) і Pago_Pago (−11) розходяться з UTC на добу в протилежні боки,
 * Kyiv — пояс людей проєкту. Розбір через UTC (`new Date('2026-10-07')`, хвіст із `Z`) дав би в
 * Pago_Pago 6 жовтня, а строгий `yyyy-MM-dd` без хвоста — `null` в усіх трьох (сам дефект T7-01).
 */
const Module = pathToFileURL(fileURLToPath(new URL('../dateOnly.ts', import.meta.url))).href;

const Probe = `
const { parseDateOnly, formatDateOnly } = await import(process.env.ECR_DATE_ONLY_MODULE);
const out = {};
for (const value of JSON.parse(process.env.ECR_DATE_ONLY_VALUES)) {
  const parsed = parseDateOnly(value);
  out[value] = parsed === null ? null : [formatDateOnly(parsed), parsed.getHours()];
}
process.stdout.write(JSON.stringify(out));
`;

const Values = ['2026-10-07', '2026-10-07T00:00:00', '2026-10-07T00:00:00.0000000', '2026-10-07T00:00:00Z', '2026-10-07T10:30:00'];

function probe(zone: string): Record<string, [string, number] | null> {
  const output = execFileSync(
    process.execPath,
    ['--experimental-strip-types', '--no-warnings', '--input-type=module', '-e', Probe],
    {
      env: { ...process.env, TZ: zone, ECR_DATE_ONLY_MODULE: Module, ECR_DATE_ONLY_VALUES: JSON.stringify(Values) },
      encoding: 'utf8',
    },
  );

  return JSON.parse(output) as Record<string, [string, number] | null>;
}

describe('parseDateOnly у поясах браузера (T7-01)', () => {
  it.each(['Europe/Kyiv', 'Pacific/Pago_Pago', 'Pacific/Kiritimati'])(
    '%s: дата шапки з сервера — той самий день опівночі; час чи Z — null',
    (zone) => {
      expect(probe(zone)).toEqual({
        '2026-10-07': ['2026-10-07', 0],
        '2026-10-07T00:00:00': ['2026-10-07', 0],
        '2026-10-07T00:00:00.0000000': ['2026-10-07', 0],
        '2026-10-07T00:00:00Z': null,
        '2026-10-07T10:30:00': null,
      });
    },
  );
});
