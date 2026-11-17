import type { MonacoModule } from '../expressions/monaco';

/**
 * Мова `ecr-script` для Monaco (план MS, розд. 3.2, 13).
 *
 * ⚠ Правила описані чистими даними й перевіряються тестом без Monaco
 * (`monaco.editor.tokenize` у jsdom не працює).
 */

/** Ідентифікатор мови в Monaco. */
export const ScriptLanguageId = 'ecr-script';

/** Зарезервовані слова (3.2) і `table` (13.1). */
export const ScriptKeywords: readonly string[] = [
  'import', 'rows', 'from', 'where', 'active', 'when', 'const', 'var', 'visible',
  'function', 'output', 'per', 'if', 'else', 'foreach', 'substance', 'in', 'reject',
  'skip', 'true', 'false', 'null', 'row', 'period', 'header', 'key', 'entry', 'table',
];

/** Агрегати (13.1) — конструкції мови, підсвічуються як вбудовані. */
export const ScriptAggregates: readonly string[] = [
  'Sum', 'Min', 'Max', 'Avg', 'First', 'Count', 'Any',
];

/** Дія токена Monarch. */
export type ScriptTokenAction =
  | string
  | { readonly token: string; readonly next?: string }
  | { readonly cases: Readonly<Record<string, string>> };

/** Правило: зразок і дія. */
export type ScriptTokenRule = readonly [RegExp, ScriptTokenAction];

export interface ScriptMonarch {
  readonly defaultToken: string;
  readonly keywords: readonly string[];
  readonly aggregates: readonly string[];
  readonly tokenizer: Readonly<Record<string, readonly ScriptTokenRule[]>>;
}

/** Ідентифікатор із необов'язковими сегментами `{X}` (13.3). */
const Identifier = /[A-Za-z_](?:[A-Za-z0-9_]|\{[A-Za-z_][A-Za-z0-9_]*\})*/;

/** Визначення токенізатора Monarch. */
export function buildScriptMonarch(): ScriptMonarch {
  return {
    defaultToken: '',
    keywords: ScriptKeywords,
    aggregates: ScriptAggregates,
    tokenizer: {
      root: [
        [/\/\/.*$/, 'comment'],
        [/\/\*/, { token: 'comment', next: '@comment' }],
        [/"/, { token: 'string', next: '@doubleString' }],
        [/'/, { token: 'string', next: '@singleString' }],
        [/\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/, 'number'],
        [
          Identifier,
          { cases: { '@keywords': 'keyword', '@aggregates': 'predefined', '@default': 'identifier' } },
        ],
        [/[{}()[\]]/, '@brackets'],
        [/&&|\|\||==|!=|<=|>=|[+\-*/%<>!?:=]/, 'operator'],
        [/[;,.]/, 'delimiter'],
        [/\s+/, 'white'],
      ],
      comment: [
        [/[^*]+/, 'comment'],
        [/\*\//, { token: 'comment', next: '@pop' }],
        [/\*/, 'comment'],
      ],
      doubleString: [
        [/[^\\"]+/, 'string'],
        [/\\./, 'string.escape'],
        [/"/, { token: 'string', next: '@pop' }],
      ],
      singleString: [
        [/[^\\']+/, 'string'],
        [/\\./, 'string.escape'],
        [/'/, { token: 'string', next: '@pop' }],
      ],
    },
  };
}

/** Налаштування мови: дужки, автозакриття, коментарі. */
export function buildScriptLanguageConfiguration() {
  return {
    comments: { lineComment: '//', blockComment: ['/*', '*/'] as [string, string] },
    brackets: [
      ['{', '}'],
      ['[', ']'],
      ['(', ')'],
    ] as [string, string][],
    autoClosingPairs: [
      { open: '{', close: '}' },
      { open: '[', close: ']' },
      { open: '(', close: ')' },
      { open: '"', close: '"', notIn: ['string', 'comment'] },
      { open: "'", close: "'", notIn: ['string', 'comment'] },
    ],
    surroundingPairs: [
      { open: '{', close: '}' },
      { open: '[', close: ']' },
      { open: '(', close: ')' },
      { open: '"', close: '"' },
      { open: "'", close: "'" },
    ],
  };
}

const registered = new WeakSet<object>();

/**
 * Реєструє мову в Monaco. Повторний виклик нічого не робить (інакше
 * токенізатор і підказки подвоювалися б).
 */
export function registerScriptLanguage(monaco: MonacoModule): string {
  if (registered.has(monaco)) return ScriptLanguageId;
  registered.add(monaco);

  monaco.languages.register({ id: ScriptLanguageId });
  monaco.languages.setLanguageConfiguration(
    ScriptLanguageId,
    buildScriptLanguageConfiguration() as unknown as Parameters<MonacoModule['languages']['setLanguageConfiguration']>[1],
  );
  monaco.languages.setMonarchTokensProvider(
    ScriptLanguageId,
    buildScriptMonarch() as unknown as Parameters<MonacoModule['languages']['setMonarchTokensProvider']>[1],
  );

  return ScriptLanguageId;
}
