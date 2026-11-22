import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { DocumentToolbar } from '@/features/documents/DocumentToolbar';
import { testTheme } from '@/test/render';

/**
 * e2e-stand 14.11 (`submissionTargetLive.spec.ts`, «перемкнули період…»): «More» стрибала на рядок (y 148 → 124),
 * бо на межі переносу напис збереження («All changes saved» ~88 px / «Read-only» ~50 px) вирішував, чи вміщається
 * група дій поруч зі станом. Правило: місце групи дій не залежить від вмісту стану.
 *
 * jsdom верстки не має, тож правило доводимо в CSS і в розмітці (класи на безпосередніх дітях рядка), а живу
 * геометрію міряє e2e-сценарій вище.
 *
 * ⛔ Мутаційний доказ (локально, відновлено): `flex: 1 1 auto` або без `flex` у `div.ecr-doc-status`, без
 * `align-self` у `div.ecr-doc-actions`, без `className` на стані — відповідний тест червоний.
 */
const css = readFileSync(path.resolve(process.cwd(), 'src/features/documents/documentToolbar.css'), 'utf8').replace(
  /\/\*[\s\S]*?\*\//g,
  '',
);

function body(selector: string): string {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const found = new RegExp(`(?:^|\\})\\s*${escaped}\\s*\\{([^}]*)\\}`).exec(css)?.[1];
  if (found === undefined) throw new Error(`правила ${selector} немає в documentToolbar.css`);

  return found;
}

describe('рядок дій документа: місце групи дій не залежить від вмісту стану', () => {
  it('стан — стала flex-база (не ширина вмісту), росте й стискається, перенос — усередині нього', () => {
    const rule = body('div.ecr-doc-status');

    // База — довжина: `auto`/`content` повернули б залежність переносу дій від напису збереження.
    expect(rule).toMatch(/flex:\s*1\s+1\s+\d+(?:\.\d+)?(?:rem|px)\s*;/);
    expect(rule).toMatch(/min-width:\s*0\s*;/);
    expect(rule).not.toMatch(/flex-wrap:\s*nowrap/);
  });

  it('група дій — біля верху рядка: стан у два поверхи не зсуває її по вертикалі', () => {
    expect(body('div.ecr-doc-actions')).toMatch(/align-self:\s*flex-start\s*;/);
  });

  it('стан і дії — безпосередні діти рядка з класами правил; ширину стану розмітка не задає', () => {
    render(
      <MantineProvider theme={testTheme}>
        <DocumentToolbar status={<span>status</span>} more={[]} primary={<button type="button">primary</button>} />
      </MantineProvider>,
    );
    const toolbar = screen.getByTestId('document-toolbar');
    const status = screen.getByTestId('document-status');
    const actions = screen.getByTestId('document-actions');

    expect(status.parentElement).toBe(toolbar);
    expect(actions.parentElement).toBe(toolbar);
    expect(status.classList.contains('ecr-doc-status')).toBe(true);
    expect(actions.classList.contains('ecr-doc-actions')).toBe(true);
    // Перенос групи дій цілою (знімок людини 1290 px) лишається: сама група кнопок не переносить.
    expect(actions.style.getPropertyValue('--group-wrap')).toBe('nowrap');
    expect(status.style.flex).toBe('');
    expect(status.style.width).toBe('');
    expect(status.style.minWidth).toBe('');
  });
});
