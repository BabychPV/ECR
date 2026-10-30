import { describe, expect, it } from 'vitest';
import { buildMonarchLanguage, type TokenRule } from '@/features/expressions/language';
import { ForbiddenExamples, PeriodProperties, SyntaxExamples } from '../formulaSyntax';

/**
 * Приклади довідки синтаксису — крізь ЛЕКСИКУ редактора методологій.
 *
 * ⛔ Клієнт не валідує вирази (`features/expressions/api.ts`: перевіряє сервер),
 * тож справжній доказ «сервер приймає кожен приклад» — e2e `methodologyUi.spec.ts`
 * (`POST /api/v1/expressions/validate`). Тут — те, що можна довести без сервера:
 * токенізатор Monarch, яким редактор фарбує текст, не має на жодному прикладі
 * класу `invalid` і бачить посилання саме так, як їх описує довідка
 * (`!Name` — формула, `!@X` — заперечення, `@A.B` — ОДНЕ ім'я, `CST.X` — константа).
 *
 * ⚠ Мінімальний інтерпретатор Monarch нижче повторює лише те, що використовує
 * `buildMonarchLanguage`: стани, `next`/`switchTo`/`@pop`, групи, `cases`.
 */

/** Функції діалекту для `@functions` — з `DialectCatalog` (сервер); тут — лише ті, що в прикладах. */
const Functions = ['if', 'Pow', 'Round', 'in'];

interface Token {
  readonly text: string;
  readonly type: string;
}

/** Дія-масив: класи груп регулярного виразу. `Array.isArray` не звужує `readonly string[]`. */
function isGroups(action: TokenRule[1]): action is readonly string[] {
  return Array.isArray(action);
}

function tokenize(code: string): Token[] {
  const language = buildMonarchLanguage({ dialect: 'Methodology', functionNames: Functions });
  const flags = language.ignoreCase ? 'iy' : 'y';
  const stack: string[] = ['root'];
  const tokens: Token[] = [];
  let at = 0;

  const resolveCases = (cases: Readonly<Record<string, string>>, text: string): string => {
    for (const [guard, type] of Object.entries(cases)) {
      if (guard === '@default') continue;
      const list = language[guard.slice(1) as 'keywords' | 'literals' | 'functions'];
      if (list.some((word) => word.toUpperCase() === text.toUpperCase())) return type;
    }

    return cases['@default'] ?? 'identifier';
  };

  while (at < code.length) {
    const state = stack[stack.length - 1] ?? 'root';
    const rules: readonly TokenRule[] = language.tokenizer[state] ?? [];
    let matched = false;

    for (const [pattern, action] of rules) {
      const sticky = new RegExp(pattern.source, flags);
      sticky.lastIndex = at;
      const match = sticky.exec(code);
      if (match === null || match[0].length === 0) continue;

      const text = match[0];
      if (typeof action === 'string') {
        tokens.push({ text, type: action });
      } else if (isGroups(action)) {
        action.forEach((type, index) => tokens.push({ text: match[index + 1] ?? '', type }));
      } else if ('cases' in action) {
        tokens.push({ text, type: resolveCases(action.cases, text) });
      } else {
        tokens.push({ text, type: action.token });
        const target = action.next ?? action.switchTo;
        if (target === '@pop') stack.pop();
        else if (target !== undefined) {
          if (action.switchTo !== undefined) stack.pop();
          stack.push(target.replace(/^@/, ''));
        }
      }

      at += text.length;
      matched = true;
      break;
    }

    if (!matched) throw new Error(`Жодне правило стану ${state} не збіглося на «${code.slice(at)}»`);
  }

  return tokens.filter((token) => token.type !== 'white');
}

describe('Довідка синтаксису: приклади крізь лексику редактора методологій', () => {
  it.each(SyntaxExamples.map((example) => [example.id, example.code] as const))(
    'приклад %s — жодного недопустимого символу',
    (_id, code) => {
      const invalid = tokenize(code).filter((token) => token.type === 'invalid');

      expect(invalid).toEqual([]);
    },
  );

  it('ідентифікатори прикладів унікальні — кожен має власний опис', () => {
    const ids = SyntaxExamples.map((example) => example.id);

    expect(new Set(ids).size).toBe(ids.length);
  });

  it('«!Name» — посилання на формулу, а «!@X» — заперечення (`Parser.State.IsFormulaRef`)', () => {
    const refs = tokenize('!E_CO2 + !E_CH4 * CST.GWP_CH4');
    expect(refs.filter((token) => token.type === 'tag').map((token) => token.text)).toEqual(['!E_CO2', '!E_CH4']);

    const negation = tokenize('if(!@IsPilot, @Total, 0)');
    expect(negation.some((token) => token.type === 'tag')).toBe(false);
    expect(negation.find((token) => token.text === '!')?.type).toBe('operator');
  });

  it('«@GCV.Methane» — ОДНЕ посилання на аргумент, «CST.NCV» — константа', () => {
    expect(tokenize('@GCV.Methane * 0.5')[0]).toEqual({ text: '@GCV.Methane', type: 'variable' });
    expect(tokenize('@FuelUse * CST.NCV / 1000').find((token) => token.text === 'CST.NCV')?.type).toBe('constant');
  });

  it('«[Period].Days» — ключове слово періоду, а не код аркуша', () => {
    const tokens = tokenize('[Period].Days * 24');

    expect(tokens.find((token) => token.text === 'Period')?.type).toBe('keyword');
    expect(PeriodProperties).toContain('Days');
  });

  it('заборонені приклади — не плутати з прийнятими: жоден не збігається з прийнятим', () => {
    const accepted = new Set(SyntaxExamples.map((example) => example.code));

    for (const forbidden of ForbiddenExamples) {
      expect(accepted.has(forbidden.code)).toBe(false);
    }
  });
});
