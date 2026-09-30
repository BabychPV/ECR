import type { ExpressionFunctionDto } from '@/api/types';
import { hasText, t } from '@/shared/i18n';
import { signatureOf, type CompletionItem, type HintKey } from './completion';
import type { HoverSymbol } from './hover';

/**
 * Людський текст довідки: підпис пояснення, опис функції, картка наведення.
 *
 * ⛔ Один модуль і для переліку доповнення, і для наведення. Два місця, що
 * складають опис колонки кожне по-своєму, розійшлися б першою ж правкою — і
 * та сама колонка мала б у переліку одиницю, а при наведенні ні.
 *
 * ⚠ Monaco тут немає: модуль перевіряється в jsdom без редактора.
 */

/** Довідка: заголовок і рядки під ним. */
export interface Doc {
  readonly title: string;
  readonly lines: readonly string[];
}

/** Текст пояснення замість порожнього переліку. */
export function hintText(hint: HintKey): string {
  switch (hint) {
    case 'pickTemplateVersion':
      return t('expressions.hint.pickTemplateVersion');
    case 'pickTemplateVersionHeaders':
      return t('expressions.hint.pickTemplateVersionHeaders');
    case 'noHeaders':
      return t('expressions.hint.noHeaders');
    case 'pickMethodologyVersion':
      return t('expressions.hint.pickMethodologyVersion');
    case 'noConstants':
      return t('expressions.hint.noConstants');
    case 'noFormulas':
      return t('expressions.hint.noFormulas');
    case 'noArguments':
      return t('expressions.hint.noArguments');
  }
}

/**
 * Короткий опис функції з каталогу рядків.
 *
 * ⚠ Ключ — за ім'ям у нижньому регістрі: `ROUND` шаблону і `Round` методології
 * описує той самий рядок. Функції, якої в каталозі ще немає, опис не
 * вигадується — лишається сигнатура: сервер може додати функцію раніше, ніж
 * з'явиться її текст, і `⟦…⟧` у підказці було б гіршим за мовчання.
 */
export function functionDescription(fn: ExpressionFunctionDto): string | undefined {
  const key = `expressions.fn.${fn.name.toLowerCase()}`;
  return hasText(key) ? t(key) : undefined;
}

/** Довідка функції: сигнатура, опис, тип результату, ярус. */
export function functionDoc(fn: ExpressionFunctionDto): Doc {
  const lines: string[] = [];

  const description = functionDescription(fn);
  if (description !== undefined) lines.push(description);

  if (fn.resultType !== null) lines.push(t('expressions.resultType', { type: fn.resultType }));
  if (fn.tier === 'Extension') lines.push(t('expressions.function.extension'));

  return { title: signatureOf(fn), lines };
}

/** Довідка символу під курсором. */
export function hoverDoc(symbol: HoverSymbol): Doc {
  switch (symbol.kind) {
    case 'function':
      return functionDoc(symbol.fn);

    case 'constant':
    case 'formula':
    case 'argument':
    case 'header': {
      const { name, unit, note } = symbol.symbol;
      const lines = [symbolKindText(symbol.kind)];
      if (unit !== null) lines.push(t('expressions.doc.unit', { unit }));
      if (note !== null) lines.push(note);
      return { title: `${prefixOf(symbol.kind)}${name}`, lines };
    }

    case 'column': {
      const { column, table } = symbol;
      const lines = [t('expressions.doc.column', { table: table.code })];
      if (column.name !== '' && column.name !== column.code) lines.push(column.name);
      lines.push(t('expressions.doc.type', { type: column.dataType }));
      if (column.unit !== null) lines.push(t('expressions.doc.unit', { unit: column.unit }));
      return { title: `[${column.code}]`, lines };
    }

    case 'row': {
      const { row, table } = symbol;
      const lines = [t('expressions.doc.row', { table: table.code })];
      if (row.label !== null && row.label !== '') lines.push(row.label);
      return { title: `[${row.key}]`, lines };
    }

    case 'table': {
      const { table } = symbol;
      const lines = [t('expressions.doc.table', { sheet: table.sheetCode })];
      if (table.name !== '' && table.name !== table.code) lines.push(table.name);
      return { title: `[${table.code}]`, lines };
    }

    case 'sheet': {
      const { sheet } = symbol;
      const lines = [t('expressions.doc.sheet')];
      if (sheet.name !== '' && sheet.name !== sheet.code) lines.push(sheet.name);
      return { title: `[${sheet.code}]`, lines };
    }
  }
}

/**
 * Опис варіанта в переліку — під ним, коли на ньому затрималися.
 *
 * @returns `undefined` — додати нічого (підпис уже все сказав).
 */
export function itemDoc(item: CompletionItem): string | undefined {
  if (item.fn !== undefined) {
    const doc = functionDoc(item.fn);
    return [doc.title, ...doc.lines].join('\n');
  }

  return item.documentation;
}

function symbolKindText(kind: 'constant' | 'formula' | 'argument' | 'header'): string {
  switch (kind) {
    case 'constant':
      return t('expressions.doc.constant');
    case 'formula':
      return t('expressions.doc.formula');
    case 'argument':
      return t('expressions.doc.argument');
    case 'header':
      return t('expressions.doc.header');
  }
}

function prefixOf(kind: 'constant' | 'formula' | 'argument' | 'header'): string {
  switch (kind) {
    case 'constant':
      return 'CST.';
    case 'formula':
      return '!';
    case 'argument':
      return '@';
    case 'header':
      return 'HDR.';
  }
}

/**
 * Довідка як Markdown для Monaco — з екрануванням.
 *
 * ⛔ Назви колонок і примітки приходять із бази, де їх редагує адміністратор.
 * Без екранування `*`, `[`, `<` у назві ставали б розміткою — посиланням чи
 * жирним шрифтом, — тобто підказка редактора рендерила б чужий вміст.
 */
export function docMarkdown(doc: Doc): string {
  return [`**${escapeMarkdown(doc.title)}**`, ...doc.lines.map(escapeMarkdown)].join('\n\n');
}

export function escapeMarkdown(text: string): string {
  return text.replace(/[\\`*_{}[\]()#+\-.!|<>~]/g, '\\$&');
}
