import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * AN-39 / L8-18: сторож подвійного кодування (UTF-8, прочитане як cp1252).
 *
 * ⛔ `keyCommitGate.ts` мав 35 рядків коментарів, де кирилиця була закодована
 * двічі й читалась як нісенітниця з латинських літер з діакритикою.
 * Компілятор такого не бачить, а коментар для людини втрачено.
 * Ознака: лідер UTF-8 кирилиці (байти D0/D1), прочитаний як cp1252, + наступний символ.
 * Шаблон записано escape-послідовностями, щоб файл не знаходив сам себе.
 */
const Mojibake =
  /[ÐÑ][\u0080-¿ŒœŠšŸŽžƒˆ˜–-›€™]/;

const SrcRoot = path.resolve(__dirname, '../..');

function* sources(dir: string): Generator<string> {
  for (const name of readdirSync(dir)) {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) yield* sources(full);
    else if (/\.tsx?$/.test(name)) yield full;
  }
}

describe('L8-18: вихідні файли клієнта без подвійного кодування', () => {
  it('жоден src/**/*.ts(x) не містить mojibake кирилиці', () => {
    const offenders: string[] = [];

    for (const file of sources(SrcRoot)) {
      const lines = readFileSync(file, 'utf8').split(/\r?\n/);
      const at = lines.findIndex((line) => Mojibake.test(line));
      if (at >= 0) offenders.push(`${path.relative(SrcRoot, file)}:${String(at + 1)}`);
    }

    expect(offenders).toEqual([]);
  });

  it('контроль: шаблон упізнає справжнє подвійне кодування', () => {
    const doubled = Buffer.from('Серіалізація', 'utf8').toString('latin1');

    expect(Mojibake.test(doubled)).toBe(true);
    expect(Mojibake.test('Серіалізація')).toBe(false);
  });
});
