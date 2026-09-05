import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';
import { deltaE00, simulate, toLab, type Dichromacy } from '@/shared/theme/colorScience';
import { contrast } from '@/shared/theme/contrast';
import { cellState, themeSurface } from '@/shared/theme/theme';

/**
 * `ФВ-14.18` перевіряється ВИМІРЯНО (`D-140`).
 *
 * ⛔ «Я подивився — стани розрізняються» не є доказом: людина з нормальним
 * зором не бачить того, що бачить оператор із дейтеранопією, а це близько
 * 6 % чоловіків. Директива №04 §6 порахувала первісну палітру і знайшла три
 * дефекти, жоден із яких не помітний оком.
 *
 * ⚠ Гейтяться чотири виміри; ще два йдуть у звіт і збірку не валять. Причина
 * названа в `ПП1-03`: потрібного значення для симуляцій **не існує для жодної
 * палітри** — п'ять станів у принципі не розділяються кольором для дихромата.
 * Гейт на неможливе змушує або підробити поріг, або викинути перевірку.
 */
const NAMES = Object.keys(cellState) as (keyof typeof cellState)[];
const KINDS: Dichromacy[] = ['protanopia', 'deuteranopia', 'tritanopia'];
const THEMES = ['light', 'dark'] as const;

/** Усі пари станів — по них і рахується найгірший випадок. */
function pairs(): [keyof typeof cellState, keyof typeof cellState][] {
  const out: [keyof typeof cellState, keyof typeof cellState][] = [];
  for (let i = 0; i < NAMES.length; i++) {
    for (let j = i + 1; j < NAMES.length; j++) out.push([NAMES[i]!, NAMES[j]!]);
  }

  return out;
}

describe('Гейт 1: другий носій унікальний', () => {
  it('ФВ-14.18: у кожного стану власна форма', () => {
    const shapes = NAMES.map((name) => cellState[name].shape);

    // ⛔ Найнадійніша частина всієї конструкції: детермінована, помилитися
    // неможливо, коштує три рядки. Саме вона, а не колір, доводить, що стани
    // розрізняються для КОЖНОГО користувача (`ПК1-03`).
    expect(new Set(shapes).size).toBe(NAMES.length);
  });

  it('форма в темі описує те, що справді малює CSS', () => {
    const css = readFileSync(
      path.resolve(process.cwd(), 'src/shared/theme/cell-states.css'),
      'utf8',
    );

    // ⚠ Без цієї перевірки `shape` був би рядком, який ні до чого не
    // зобов'язує: п'ять різних слів у файлі теми і жодної різниці на екрані.
    for (const name of NAMES) {
      const slug = name.replace(/[A-Z]/g, (letter) => `-${letter.toLowerCase()}`);
      const rule = css.slice(css.indexOf(`.ecr-cell--${slug}`));
      const body = rule.slice(0, rule.indexOf('}'));
      const marker = css.includes(`.ecr-cell--${slug}::`);

      const shape = cellState[name].shape;

      if (shape.includes('hatch')) {
        expect(body, `${name}: обіцяно штрихування`).toContain('repeating-linear-gradient');
      }

      if (shape.includes('solid-bar')) expect(body).toContain('border-left: 3px solid');
      if (shape.includes('dashed-bar')) expect(body).toContain('border-left: 3px dashed');
      if (shape.includes('double-bar')) expect(body).toContain('border-left: 3px double');
      if (shape.includes('dotted-bar')) expect(body).toContain('border-left: 3px dotted');

      const promisesMarker = /corner|formula|warning|dot$/.test(shape);
      if (promisesMarker) expect(marker, `${name}: обіцяно маркер`).toBe(true);
    }
  });
});

describe('Гейт 2: рамки різняться в нормальному зорі', () => {
  it.each(THEMES)('ФВ-14.18: %s — ΔE00 між рамками ≥ 20', (theme) => {
    // ⚠ Поріг 20 не з голови: виміряний мінімум чинної палітри — 22.8 у
    // світлій і 22.1 у темній, тобто вимога виконувана із запасом.
    for (const [a, b] of pairs()) {
      const distance = deltaE00(cellState[a][theme].line, cellState[b][theme].line);

      expect(distance, `${a}/${b} у ${theme}`).toBeGreaterThanOrEqual(20);
    }
  });
});

describe('Гейт 3: рамка видима на обох фонах', () => {
  it.each(THEMES)('ФВ-14.17: %s — контраст рамки ≥ 3:1', (theme) => {
    // ⛔ Рамка межує з ДВОМА фонами: власною заливкою і тлом сторінки.
    // Перевірка лише проти одного пропустила б первісний `#f0b429`, який давав
    // проти білого 1.8:1 — тобто носій інформації був майже невидимий.
    for (const name of NAMES) {
      const token = cellState[name][theme];

      expect(contrast(token.line, token.bg), `${name} проти заливки`).toBeGreaterThanOrEqual(3);
      expect(
        contrast(token.line, themeSurface[theme].body),
        `${name} проти фону`,
      ).toBeGreaterThanOrEqual(3);
    }
  });
});

describe('Звіт без гейта: дихромазія і заливки', () => {
  it('ΔE00 між рамками у трьох симуляціях', () => {
    // ⚠ НЕ гейт (`ПК1-03`): значення ≥ 20 при всіх трьох симуляціях не існує
    // для жодної палітри — стеля близько 14.7. Число тут для того, щоб різке
    // погіршення між прогонами було видно.
    const report: string[] = [];
    let worst = Infinity;

    for (const theme of THEMES) {
      for (const [a, b] of pairs()) {
        for (const kind of KINDS) {
          const distance = deltaE00(
            simulate(cellState[a][theme].line, kind),
            simulate(cellState[b][theme].line, kind),
          );

          worst = Math.min(worst, distance);
          if (distance < 12) report.push(`${theme} ${a}/${b} ${kind}: ${distance.toFixed(1)}`);
        }
      }
    }

    // ⚠ М'який поріг: 10 — це «пара ще розрізнима», і його чинна палітра
    // тримає (11.7 у світлій, 13.7 у темній). Якщо впаде нижче — це не
    // катастрофа, але привід подивитися.
    expect(worst, `найгірша пара: ${report.join('; ')}`).toBeGreaterThan(10);
  });

  it('ΔL* між заливками — довідково', () => {
    // ⛔ Заливки НЕ можуть нести розрізнення, і це не лагодиться: вони
    // навмисно бліді, бо таблиця з насичених комірок непридатна для
    // восьмигодинної роботи. Перевірка фіксує факт, а не вимогу.
    let minimum = Infinity;

    for (const theme of THEMES) {
      for (const [a, b] of pairs()) {
        minimum = Math.min(
          minimum,
          Math.abs(toLab(cellState[a][theme].bg).L - toLab(cellState[b][theme].bg).L),
        );
      }
    }

    // Саме тому гейт стоїть на формі: у градаціях сірого дві заливки можуть
    // бути тим самим пікселем.
    expect(minimum).toBeLessThan(5);
  });
});
