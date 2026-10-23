import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { editsDeferToServer, isEditable } from '@/features/workflow/SheetActions';
import { rowsLockedBySheet, sheetLockClass } from '../cellState';

/*
 * ✎ D-442 (BRIEF-1021 П.3): поданий аркуш править лише погоджувач поточного кроку; штриховку прибрано
 * для затвердженого аркуша (і на поданому в погоджувача — там комірки редаговні), а «лише читання»
 * лишається видимим другим носієм (`D-128`, замок у куті).
 *
 * ⛔ Мутації: `sheetLockClass` для `Submitted` — червоніє «поданий лишає штриховку решті»;
 * `editsDeferToServer('Approved') === true` — червоніє «затверджений замикає сітку»; правило
 * `.ecr-sheet-locked` знімає `::after` (замок) — червоніє «замок лишається».
 */

const css = readFileSync(path.resolve(process.cwd(), 'src/shared/theme/cell-states.css'), 'utf8');

/** Тіло ПЕРШОГО правила з точно цим селектором (до `}`). */
function ruleBody(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  expect(start, `правило «${selector}» є в cell-states.css`).toBeGreaterThanOrEqual(0);
  const rule = css.slice(start);
  return rule.slice(0, rule.indexOf('}'));
}

describe('D-442: поданий аркуш — правку вирішує сервер по комірці', () => {
  it('поданий аркуш сітку цілком не замикає, затверджений і чернетка — як доти', () => {
    expect(editsDeferToServer('Submitted')).toBe(true);
    expect(editsDeferToServer('Approved')).toBe(false);
    expect(editsDeferToServer('Draft')).toBe(false);

    // Дії зі зміни даних на поданому лишаються закриті (`readOnly` сторінки).
    expect(isEditable('Submitted')).toBe(false);
  });

  it('склад рядків поданого й затвердженого аркуша закритий — погоджувач править лише значення', () => {
    expect(rowsLockedBySheet('Submitted')).toBe(true);
    expect(rowsLockedBySheet('Approved')).toBe(true);
    expect(rowsLockedBySheet('Draft')).toBe(false);
    expect(rowsLockedBySheet('Rejected')).toBe(false);
    expect(rowsLockedBySheet(undefined)).toBe(false);
  });
});

describe('D-442: затверджений аркуш — без штриховки, замок лишається (D-128)', () => {
  it('аркушевий клас — лише для затвердженого', () => {
    expect(sheetLockClass('Approved')).toBe('ecr-sheet-locked');

    // ⚠ Поданий: погоджувачеві комірки редаговні (штриховки немає й так), решті штриховка лишається.
    expect(sheetLockClass('Submitted')).toBeUndefined();
    expect(sheetLockClass('Draft')).toBeUndefined();
    expect(sheetLockClass(undefined)).toBeUndefined();
  });

  it('CSS знімає лише штриховку, а не замок і не курсор', () => {
    expect(ruleBody('.ecr-sheet-locked .ecr-cell--read-only')).toContain('background-image: none');
    expect(css).not.toMatch(/\.ecr-sheet-locked \.ecr-cell--read-only::after/);

    // Другий носій стану — форма: замок у куті й курсор у самого стану.
    expect(ruleBody('.ecr-cell--read-only::after')).toContain('mask');
    expect(ruleBody('.ecr-cell--read-only')).toContain('cursor: not-allowed');
  });
});
