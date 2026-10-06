import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { Button, MantineProvider, SegmentedControl } from '@mantine/core';
import { theme } from '../theme';

/**
 * Сторож правила кнопок (`docs/design/ui-conventions.md`, «Кнопки»;
 * `src/shared/theme/controls.css`).
 *
 * ⛔ Чому він потрібен. Шкалу тримає CSS (`controls.css`), але CSS не бачить,
 * ЯКИЙ розмір обрав екран. До 2026-10-06 у коді жило чотири висоти кнопок
 * (`sm`, `xs`, `compact-sm`, `compact-xs`) і в одній панелі документа сусіди
 * були різні (знімок людини: «кнопка експорту виглядає більшою»). Тут —
 * заборона того, що знову розвело б їх: розмір поза шкалою і ручні висота,
 * відступ, шрифт чи радіус на самій кнопці.
 *
 * ⚠ Розбір — не регулярним виразом «до `>`», а лічильником дужок: атрибут
 * `onClick={() => a > b}` містить `>`, і регулярка обрізала б тег на ньому,
 * мовчки пропустивши решту атрибутів (хибне «чисто»).
 */

const SrcRoot = path.resolve(process.cwd(), 'src');

/** Розміри, дозволені шкалою; відсутній `size` = `sm` (типовий у темі). */
const AllowedSize: Readonly<Record<string, readonly string[]>> = {
  Button: ['"xs"', '"md"'],
  SegmentedControl: ['"xs"'],
  // `input-*` — службовий розмір Mantine для іконок усередині поля (`PeriodPicker`).
  ActionIcon: ['"sm"', '{`input-${size}`}'],
};

/** Пропи, якими екран вирізав би кнопку зі шкали. */
const ForbiddenProps = ['styles', 'h', 'mih', 'mah', 'fz', 'fw', 'px', 'py', 'p', 'radius', 'lh'];

/** У `style` можна розкладку (`flexShrink`), але не розмір, відступ чи шрифт. */
const ForbiddenStyle = /\b(height|minHeight|padding\w*|font\w*|lineHeight|borderRadius)\s*:/;

interface Tag {
  readonly file: string;
  readonly line: number;
  readonly component: string;
  readonly attrs: string;
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sourceFiles(full);
    return name.endsWith('.tsx') ? [full] : [];
  });
}

/** Атрибути відкривного тегу: до `>` або `/>` на нульовій глибині дужок і поза рядками. */
function attrsFrom(text: string, start: number): string {
  let depth = 0;
  let quote: string | null = null;

  for (let i = start; i < text.length; i += 1) {
    const ch = text[i];

    if (quote !== null) {
      if (ch === quote && text[i - 1] !== '\\') quote = null;
      continue;
    }
    if (ch === '"' || ch === "'" || ch === '`') quote = ch;
    else if (ch === '{') depth += 1;
    else if (ch === '}') depth -= 1;
    else if (ch === '>' && depth === 0) return text.slice(start, text[i - 1] === '/' ? i - 1 : i);
  }

  return text.slice(start);
}

function tagsOf(file: string, text: string): Tag[] {
  const tags: Tag[] = [];

  for (const match of text.matchAll(/<(Button|ActionIcon|SegmentedControl)(?=[\s/>])/g)) {
    const at = match.index ?? 0;
    tags.push({
      file: path.relative(SrcRoot, file),
      line: text.slice(0, at).split('\n').length,
      component: match[1] ?? '',
      attrs: attrsFrom(text, at + match[0].length),
    });
  }

  return tags;
}

/**
 * Пропи ВЕРХНЬОГО рівня тегу: `name` → `"…"` | `{…}` (з дужками) | `true`.
 *
 * ⚠ Лише нульова глибина: `leftSection={<Icon size={14} />}` несе `size`
 * усередині, і він стосується іконки, а не кнопки.
 */
function propsOf(attrs: string): Map<string, string> {
  const props = new Map<string, string>();
  let i = 0;

  while (i < attrs.length) {
    const name = /^[A-Za-z_][\w-]*/.exec(attrs.slice(i));
    if (name === null) {
      i += 1;
      continue;
    }

    i += name[0].length;
    if (attrs[i] !== '=') {
      props.set(name[0], 'true');
      continue;
    }

    i += 1;
    const from = i;
    if (attrs[i] === '"' || attrs[i] === "'") {
      i = attrs.indexOf(attrs[i] ?? '"', i + 1) + 1;
    } else if (attrs[i] === '{') {
      let depth = 0;
      let quote: string | null = null;
      for (; i < attrs.length; i += 1) {
        const ch = attrs[i];
        if (quote !== null) {
          if (ch === quote && attrs[i - 1] !== '\\') quote = null;
        } else if (ch === '"' || ch === "'" || ch === '`') quote = ch;
        else if (ch === '{') depth += 1;
        else if (ch === '}') {
          depth -= 1;
          if (depth === 0) {
            i += 1;
            break;
          }
        }
      }
    }
    props.set(name[0], attrs.slice(from, i));
  }

  return props;
}

/** Порушення правила в одному тегу, людською мовою; порожньо — чисто. */
function violations(tag: Tag): string[] {
  const where = `${tag.file}:${tag.line} <${tag.component}>`;
  const found: string[] = [];
  const props = propsOf(tag.attrs);

  const size = props.get('size') ?? null;
  if (size !== null && !(AllowedSize[tag.component] ?? []).includes(size)) {
    found.push(`${where}: size=${size} поза шкалою (дозволено: без пропа, ${(AllowedSize[tag.component] ?? []).join(', ')})`);
  }

  for (const prop of ForbiddenProps) {
    if (props.has(prop)) found.push(`${where}: ручний проп «${prop}» — розмір задає шкала`);
  }

  const style = props.get('style') ?? null;
  if (style !== null && ForbiddenStyle.test(style)) {
    found.push(`${where}: style задає розмір/відступ/шрифт — розмір задає шкала`);
  }

  return found;
}

const AllTags = sourceFiles(SrcRoot).flatMap((file) => tagsOf(file, readFileSync(file, 'utf8')));

describe('правило кнопок: одна шкала розмірів', () => {
  it('сторож справді бачить кнопки застосунку (не порожній обхід)', () => {
    // ⚠ Нуль тегів зеленів би на будь-якому коді. Станом на 2026-10-06 їх ~450.
    expect(AllTags.filter((tag) => tag.component === 'Button').length).toBeGreaterThan(300);
  });

  it('жодна кнопка, іконка-кнопка чи перемикач не виходить зі шкали', () => {
    expect(AllTags.flatMap(violations)).toEqual([]);
  });

  it.each([
    ['size="lg"', true],
    ['size="compact-xs"', true],
    ['size="sm"', true],
    ['h={40}', true],
    ['fz="md"', true],
    ['style={{ padding: 4 }}', true],
    ['styles={{ root: { height: 20 } }}', true],
    ['onClick={() => a > b} size="lg"', true],
    ['size="xs"', false],
    ['size="md"', false],
    ['style={{ flexShrink: 0 }}', false],
    ['variant="default" onClick={() => go({ px: 1 })}', false],
    ['leftSection={<Icon size={14} h={2} />} fullWidth', false],
  ])('розбір тегу: %s → порушення %s', (attrs, bad) => {
    // ⛔ Сам розбір — частина гейта: без цих рядків «чисто» могло б означати
    // «регулярка не дочитала тег».
    const text = `<Button ${attrs}>Ok</Button>`;
    const [tag] = tagsOf('/x.tsx', text);
    expect(tag).toBeDefined();
    expect(violations(tag!).length > 0).toBe(bad);
  });
});

describe('шкала доходить до справжніх компонентів Mantine', () => {
  const ControlsCss = readFileSync(path.resolve(SrcRoot, 'shared/theme/controls.css'), 'utf8');
  const TokensCss = readFileSync(path.resolve(SrcRoot, 'shared/theme/tokens.css'), 'utf8');

  afterEach(() => {
    cleanup();
    for (const style of document.querySelectorAll('style[data-ecr-test-controls]')) style.remove();
  });

  function loadCss(): void {
    for (const css of [TokensCss, ControlsCss]) {
      const style = document.createElement('style');
      style.dataset['ecrTestControls'] = 'true';
      style.textContent = css;
      document.head.append(style);
    }
  }

  it('кнопка і перемикач типового розміру беруть висоту контролу (`--ecr-ctl-height`)', () => {
    loadCss();
    render(
      <MantineProvider theme={theme}>
        <Button>Export</Button>
        <SegmentedControl aria-label="format" data={['Excel', 'CSV']} />
      </MantineProvider>,
    );

    const button = screen.getByRole('button', { name: 'Export' });
    // ⚠ jsdom не підставляє `var()` (див. `density.test.tsx`), тож перевіряємо
    // два кроки: правило долетіло до РЕАЛЬНОГО класу Mantine і вказує на ту
    // саму змінну, що й поле вводу, а змінна — число щільності.
    expect(button.getAttribute('data-size')).toBe('sm');
    expect(getComputedStyle(button).getPropertyValue('--button-height-sm').trim()).toBe('var(--ecr-ctl-height)');
    expect(getComputedStyle(button).getPropertyValue('--button-height-xs').trim()).toBe('24px');
    expect(getComputedStyle(document.documentElement).getPropertyValue('--ecr-ctl-height').trim()).toBe('28px');

    const label = document.querySelector('.mantine-SegmentedControl-label');
    expect(label).not.toBeNull();
    expect(getComputedStyle(label as Element).lineHeight).toBe('calc(var(--ecr-ctl-height) - 4px)');
  });

  it('`controls.css` підключено ПІСЛЯ стилів Mantine (інакше рівна специфічність програє)', () => {
    const main = readFileSync(path.resolve(SrcRoot, 'main.tsx'), 'utf8');
    const mantine = main.indexOf("import '@mantine/core/styles.css'");
    const controls = main.indexOf("import './shared/theme/controls.css'");

    expect(mantine).toBeGreaterThanOrEqual(0);
    expect(controls).toBeGreaterThan(mantine);
  });
});
