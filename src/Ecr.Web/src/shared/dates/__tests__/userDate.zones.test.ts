// @vitest-environment node
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * A1-02: `parseUserDate` у справжніх поясах браузера — той самий прийом, що `dateOnly.zones.test.ts`
 * (T7-01): пояс у потоці `vmThreads` не задати, тож модуль вантажиться в ОКРЕМОМУ процесі Node із `TZ`
 * (типи прибирає сам Node: `--experimental-strip-types`, Node ≥ 22.6).
 *
 * Пояси — крайні: Kiritimati (+14) і Pago_Pago (−11) розходяться з UTC на добу в протилежні боки,
 * Kyiv — пояс людей проєкту. Розбір через UTC (`new Date('2026-10-05')`) дав би в Pago_Pago 4 жовтня.
 */
const UserDate = pathToFileURL(fileURLToPath(new URL('../userDate.ts', import.meta.url))).href;
const DateOnly = pathToFileURL(fileURLToPath(new URL('../../format/dateOnly.ts', import.meta.url))).href;

const Probe = `
const { parseUserDate } = await import(process.env.ECR_USER_DATE_MODULE);
const { formatDateOnly } = await import(process.env.ECR_DATE_ONLY_MODULE);
const out = {};
for (const [text, lang] of JSON.parse(process.env.ECR_USER_DATE_VALUES)) {
  const parsed = parseUserDate(text, lang);
  out[lang + ' ' + text] = parsed === null ? null : [formatDateOnly(parsed), parsed.getHours()];
}
process.stdout.write(JSON.stringify(out));
`;

const Values = [
  ['05.10.2026', 'ru'],
  ['05.10.2026', 'kz'],
  ['05.10.2026', 'en'],
  ['2026-10-05', 'en'],
  ['2026-13-45', 'ru'],
  ['31.12.2026', 'kz'],
  ['01.01.2027', 'ru'],
];

function probe(zone: string): Record<string, [string, number] | null> {
  const output = execFileSync(
    process.execPath,
    ['--experimental-strip-types', '--no-warnings', '--input-type=module', '-e', Probe],
    {
      env: {
        ...process.env,
        TZ: zone,
        ECR_USER_DATE_MODULE: UserDate,
        ECR_DATE_ONLY_MODULE: DateOnly,
        ECR_USER_DATE_VALUES: JSON.stringify(Values),
      },
      encoding: 'utf8',
    },
  );

  return JSON.parse(output) as Record<string, [string, number] | null>;
}

describe('parseUserDate у поясах браузера (A1-02)', () => {
  it.each(['Europe/Kyiv', 'Pacific/Pago_Pago', 'Pacific/Kiritimati'])(
    '%s: набраний день — той самий день опівночі; неіснуючий і неоднозначний — null',
    (zone) => {
      expect(probe(zone)).toEqual({
        'ru 05.10.2026': ['2026-10-05', 0],
        'kz 05.10.2026': ['2026-10-05', 0],
        'en 05.10.2026': null,
        'en 2026-10-05': ['2026-10-05', 0],
        'ru 2026-13-45': null,
        'kz 31.12.2026': ['2026-12-31', 0],
        'ru 01.01.2027': ['2027-01-01', 0],
      });
    },
  );
});
