import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * `U-17`, друга половина: самі ТЕКСТИ в каталозі.
 *
 * ⛔ Навіщо окремо від `DocumentGrid.saveRefusalOnce.test.tsx`. Той доводить,
 * що два місця малюються РІЗНИМИ ключами, — і лишився б зеленим, якби обидва
 * ключі несли те саме речення «Not saved — see the error above»: на екрані це
 * знову була б та сама відмова двічі, з тим самим посиланням «вище» на те,
 * що насправді нижче. Ключі — не текст; текст перевіряється по `09-seed.sql`.
 *
 * ⚠ Читається лише блок `MERGE` — так само, як у `a11yFixtures.tsx`: секції
 * «змінені тексти» й «прибрані ключі» над ним несуть СТАРІ значення, і саме
 * старе значення тут і шукається як помилка.
 */
function mergeBlock(): string {
  const seed = readFileSync(
    path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
    'utf8',
  );

  const start = seed.indexOf('MERGE sys_ecr.UiString AS t');
  const end = seed.indexOf(') AS s ([Key], Lang, Val, Scope)', start);

  expect(start, '09-seed.sql: не знайдено блоку MERGE sys_ecr.UiString').toBeGreaterThan(-1);
  expect(end, '09-seed.sql: не знайдено кінця блоку MERGE').toBeGreaterThan(start);

  return seed.slice(start, end);
}

function value(key: string): string {
  const escaped = key.replace(/\./gu, '\\.');
  const row = new RegExp(`\\(N'${escaped}',\\s*N'en',\\s*N'([^']*)'`, 'u').exec(mergeBlock());

  expect(row, `09-seed.sql: у MERGE немає ключа ${key} (en)`).not.toBeNull();

  return row?.[1] ?? '';
}

describe('U-17 · тексти відмови збереження', () => {
  it('позначка й заголовок банера — два різні тексти', () => {
    const mark = value('grid.saveFailedMark');
    const banner = value('grid.saveError');

    expect(mark).not.toBe('');
    expect(banner).not.toBe('');

    // ⛔ Рівно та вада: одне речення, надруковане двічі поруч.
    expect(mark.toLowerCase()).not.toBe(banner.toLowerCase());
  });

  it('жоден із них не відсилає «вище» — причина стоїть НИЖЧЕ позначки', () => {
    // ⛔ «see the error above» у рядку кнопок вказувало на банер, який
    // насправді під ним. Напрямок, названий неправильно, гірший за
    // неназваний: людина шукає причину там, де її немає.
    for (const key of ['grid.saveFailedMark', 'grid.saveError']) {
      expect(value(key).toLowerCase(), key).not.toContain('above');
    }
  });
});
