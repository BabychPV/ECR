import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  UnusedMantineComponents,
  findPrunedClassesInUse,
  mantineClassesIn,
  pruneCss,
} from '../mantineCssPrune';

const stylesDir = path.resolve(process.cwd(), 'node_modules/@mantine/core/styles');
const fullCss = readFileSync(
  path.resolve(process.cwd(), 'node_modules/@mantine/core/styles.css'),
  'utf8',
);

const classesOf = (component: string): Set<string> =>
  mantineClassesIn(readFileSync(path.join(stylesDir, `${component}.css`), 'utf8'));

describe('pruneCss', () => {
  const dead = new Set(['m_dead0001']);

  it('прибирає правило, кожен селектор якого мертвий, і лишає решту дослівно', () => {
    const css = '.m_live0001 { color: red; }\n.m_dead0001, .m_dead0001:hover { color: blue; }\n';

    expect(pruneCss(css, dead)).toBe('.m_live0001 { color: red; }\n');
  });

  it('лишає змішане правило цілим — текст не переписується', () => {
    const css = '.m_live0001, .m_dead0001 { color: red; }';

    expect(pruneCss(css, dead)).toBe(css);
  });

  it('заходить у @media і прибирає блок, що спорожнів', () => {
    const css =
      '@media (hover: hover) { .m_dead0001:hover { color: red; } }\n' +
      '@media (min-width: 1px) { .m_dead0001 { a: b; } .m_live0001 { c: d; } }';

    expect(pruneCss(css, dead)).toBe('\n@media (min-width: 1px) { .m_live0001 { c: d; } }');
  });

  it('прибирає @keyframes із мертвим ім’ям і не чіпає живих', () => {
    const css = '@keyframes m_dead0001 { 0% { a: b; } }@keyframes m_live0001 { 0% { a: b; } }';

    expect(pruneCss(css, dead)).toBe('@keyframes m_live0001 { 0% { a: b; } }');
  });

  it('не плутає клас із сімома знаками з довшим', () => {
    expect([...mantineClassesIn('.m_a8645c2 .m_a8645c2x .m_12345678')]).toEqual([
      'm_a8645c2',
      'm_12345678',
    ]);
  });
});

describe('відсікання на справжньому @mantine/core/styles.css', () => {
  const dead = new Set(UnusedMantineComponents.flatMap((c) => [...classesOf(c)]));
  const pruned = pruneCss(fullCss, dead);
  const left = mantineClassesIn(pruned);

  it('розбір за дужками коректний: у файлі немає рядків із дужками', () => {
    expect(/(['"])[^'"\n]*[{}][^'"\n]*\1/.test(fullCss)).toBe(false);
  });

  it('кожен компонент із переліку існує і має класи', () => {
    for (const component of UnusedMantineComponents) {
      expect(classesOf(component).size, component).toBeGreaterThan(0);
    }
  });

  it('класи виключених компонентів зникають повністю', () => {
    expect([...dead].filter((cls) => left.has(cls))).toEqual([]);
  });

  it('жоден клас НЕвиключеного компонента не втрачено', () => {
    const kept = readdirSync(stylesDir)
      .filter((f) => f.endsWith('.css') && !f.endsWith('.layer.css'))
      .map((f) => f.replace(/\.css$/, ''))
      .filter((c) => !UnusedMantineComponents.includes(c));

    const lost = kept.flatMap((c) =>
      [...classesOf(c)].filter((cls) => mantineClassesIn(fullCss).has(cls) && !left.has(cls)),
    );

    expect(lost).toEqual([]);
  });

  it('скидання стилів і змінні теми лишаються', () => {
    expect(pruned).toContain('--mantine-scale');
    expect(pruned).toContain('box-sizing: border-box');
  });
});

describe('findPrunedClassesInUse', () => {
  it('знаходить клас відсіченого компонента в коді чанка', () => {
    const slider = classesOf('Slider');
    const [cls] = [...slider];

    expect([...findPrunedClassesInUse([`const c = { root: "${String(cls)}" };`], slider)]).toEqual([
      cls,
    ]);
    expect(findPrunedClassesInUse(['const c = { root: "m_live0001" };'], slider).size).toBe(0);
  });
});
