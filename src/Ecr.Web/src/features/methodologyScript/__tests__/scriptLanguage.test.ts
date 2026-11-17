import { describe, expect, it, vi } from 'vitest';
import type { MonacoModule } from '../../expressions/monaco';
import {
  buildScriptLanguageConfiguration,
  buildScriptMonarch,
  registerScriptLanguage,
  ScriptKeywords,
  ScriptLanguageId,
  type ScriptTokenAction,
} from '../scriptLanguage';

/**
 * Мінімальний симулятор Monarch: достатньо для станів, `next` і `cases` цієї
 * мови. `monaco.editor.tokenize` у jsdom не працює, тому перевіряємо правила.
 */
function tokenize(text: string): [string, string][] {
  const monarch = buildScriptMonarch();
  const result: [string, string][] = [];
  const stack = ['root'];

  const resolve = (action: ScriptTokenAction, matched: string): { token: string; next?: string } => {
    if (typeof action === 'string') return { token: action };
    if ('cases' in action) {
      if (monarch.keywords.includes(matched)) return { token: action.cases['@keywords'] ?? '' };
      if (monarch.aggregates.includes(matched)) return { token: action.cases['@aggregates'] ?? '' };
      return { token: action.cases['@default'] ?? '' };
    }
    return action.next === undefined
      ? { token: action.token }
      : { token: action.token, next: action.next };
  };

  for (const line of text.split('\n')) {
    let pos = 0;
    while (pos < line.length) {
      const rules = monarch.tokenizer[stack[stack.length - 1] ?? 'root'] ?? [];
      let matched = false;
      for (const [regex, action] of rules) {
        const rx = new RegExp(regex.source, 'y');
        rx.lastIndex = pos;
        const m = rx.exec(line);
        if (m === null || m[0] === '') continue;

        const { token, next } = resolve(action, m[0]);
        if (token !== 'white' && token !== '@brackets') result.push([m[0], token]);
        else if (token === '@brackets') result.push([m[0], 'bracket']);
        if (next === '@pop') stack.pop();
        else if (next !== undefined) stack.push(next.replace('@', ''));
        pos += m[0].length;
        matched = true;
        break;
      }
      if (!matched) {
        result.push([line[pos] ?? '', 'invalid']);
        pos++;
      }
    }
  }
  return result;
}

const tokens = (text: string): string[] => tokenize(text).map(([, token]) => token);

describe('токенізатор ecr-script', () => {
  it('ключові слова 3.2 і table — keyword', () => {
    for (const word of ScriptKeywords) {
      expect(tokenize(word)).toEqual([[word, 'keyword']]);
    }
    expect(ScriptKeywords).toContain('table');
    expect(ScriptKeywords).toContain('foreach');
  });

  it('агрегати — predefined, не keyword', () => {
    for (const name of ['Sum', 'Min', 'Max', 'Avg', 'First', 'Count', 'Any']) {
      expect(tokenize(name)).toEqual([[name, 'predefined']]);
    }
  });

  it('звичайні імена — identifier; регістр важливий', () => {
    expect(tokenize('Rows')).toEqual([['Rows', 'identifier']]);
    expect(tokenize('k1')).toEqual([['k1', 'identifier']]);
  });

  it('числа: ціле, дробове, з експонентою', () => {
    for (const n of ['123', '1.5', '1e6', '2.5E-3', '3e+2']) {
      expect(tokenize(n)).toEqual([[n, 'number']]);
    }
  });

  it('рядки в подвійних і одинарних лапках з екрануванням', () => {
    expect(tokens('"abc"')).toEqual(['string', 'string', 'string']);
    expect(tokenize('"a\\"b"').map(([t]) => t).join('')).toBe('"a\\"b"');
    expect(tokens('"a\\"b"')).toContain('string.escape');
    expect(tokens("'x\\'y'")).toContain('string.escape');
    // Усередині рядка ключове слово — не keyword.
    expect(tokens('"rows from"')).not.toContain('keyword');
  });

  it('коментарі: рядковий і блоковий (багаторядковий)', () => {
    expect(tokenize('// rows from')).toEqual([['// rows from', 'comment']]);
    expect(tokens('/* a\nrows */ var')).toEqual(['comment', 'comment', 'comment', 'comment', 'keyword']);
    expect(tokenize('var // tail')).toEqual([
      ['var', 'keyword'],
      ['// tail', 'comment'],
    ]);
  });

  it('ідентифікатор із сегментом {X} — один токен', () => {
    expect(tokenize('ECW_EC_gsec_{S}')).toEqual([['ECW_EC_gsec_{S}', 'identifier']]);
    expect(tokenize('a{X}b{Y}')).toEqual([['a{X}b{Y}', 'identifier']]);
  });

  it('{ окремо — дужка, а не частина імені', () => {
    expect(tokenize('foreach S in (1) { x }').filter(([, t]) => t === 'bracket')).toHaveLength(4);
  });

  it('оператори й роздільники', () => {
    expect(tokenize('a && b || !c == d != e <= f ? g : h')).toEqual(
      expect.arrayContaining([
        ['&&', 'operator'],
        ['||', 'operator'],
        ['!', 'operator'],
        ['==', 'operator'],
        ['!=', 'operator'],
        ['<=', 'operator'],
        ['?', 'operator'],
        [':', 'operator'],
      ]),
    );
    expect(tokenize('a.b;')).toEqual([
      ['a', 'identifier'],
      ['.', 'delimiter'],
      ['b', 'identifier'],
      [';', 'delimiter'],
    ]);
  });

  it('приклад із плану не дає невпізнаних символів', () => {
    const sample =
      'rows from T3_00 where row.A == entry("R", "C") active when row.S == 1;\n' +
      'const EF = { Summer: { NOx: 1.2 }, };\n' +
      'var x = Sum(G.Col where G.M == period.Month);';
    expect(tokens(sample)).not.toContain('invalid');
  });
});

describe('конфігурація мови й реєстрація', () => {
  it('дужки, автозакриття й коментарі', () => {
    const cfg = buildScriptLanguageConfiguration();
    expect(cfg.comments.lineComment).toBe('//');
    expect(cfg.comments.blockComment).toEqual(['/*', '*/']);
    expect(cfg.brackets).toEqual(expect.arrayContaining([['{', '}'], ['(', ')'], ['[', ']']]));
    expect(cfg.autoClosingPairs.map((p) => p.open)).toEqual(
      expect.arrayContaining(['{', '(', '[', '"', "'"]),
    );
  });

  it('реєстрація ідемпотентна для одного модуля Monaco', () => {
    const languages = {
      register: vi.fn(),
      setLanguageConfiguration: vi.fn(),
      setMonarchTokensProvider: vi.fn(),
    };
    const monaco = { languages } as unknown as MonacoModule;

    expect(registerScriptLanguage(monaco)).toBe(ScriptLanguageId);
    registerScriptLanguage(monaco);

    expect(languages.register).toHaveBeenCalledTimes(1);
    expect(languages.setMonarchTokensProvider).toHaveBeenCalledTimes(1);
    expect(languages.setLanguageConfiguration).toHaveBeenCalledTimes(1);
  });
});
