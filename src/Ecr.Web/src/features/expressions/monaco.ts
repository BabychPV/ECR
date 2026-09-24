import * as monaco from 'monaco-editor/editor/editor.api';

// ⛔ Внески підключаються ПОІМЕННО, і це не оптимізація, а рішення про склад
// редактора. Голий `editor.api` не має ані підказок, ані автодоповнення —
// перелік нижче і є відповіддю на питання «що вміє наш редактор виразів».
//
// ⚠ `editor.main` (усе одразу) важить 1 121 КБ gzip проти 782 КБ тут: він
// тягне ще й вісім десятків чужих мов — від ABAP до YAML, — жодна з яких у
// цьому полі ніколи не з'явиться.
import 'monaco-editor/editor/contrib/suggest/browser/suggestController';
import 'monaco-editor/editor/contrib/hover/browser/hoverContribution';
import 'monaco-editor/editor/contrib/bracketMatching/browser/bracketMatching';
import 'monaco-editor/editor/contrib/parameterHints/browser/parameterHints';

// ⛔ Внеску `comment` тут НЕМАЄ навмисно. З ним Ctrl+/ вставляв би синтаксис
// коментаря, якого в мові не існує (`02b` §1), і вираз ставав би
// неопубліковуваним одним натиском — без жодного натяку чому.
//
// ⛔ Внеску `find` теж немає: вираз однорядковий, а Ctrl+F у ньому перехопив
// би пошук сторінки браузера, нічого не давши натомість.

import {
  expressionDark,
  expressionLight,
  monacoRules,
  type ExpressionPalette,
} from '@/shared/theme/expressionTheme';
import { buildLanguageConfiguration, buildMonarchLanguage } from './language';
import {
  signatureOf,
  suggestionsAt,
  TriggerCharacters,
  type CompletionItem,
  type CompletionKind,
  type EditorSymbols,
} from './completion';
import { docMarkdown, hintText, hoverDoc, itemDoc } from './describe';
import { hoverAt } from './hover';
import { markersFor } from './markers';
import { t } from '@/shared/i18n';
import type { ExpressionDialect, ExpressionMetadataDto } from '@/api/types';
import { languageIdOf } from './dialect';
import EditorWorker from 'monaco-editor/editor/editor.worker?worker';

/**
 * Усе, що торкається Monaco. **Єдиний** модуль застосунку, який його імпортує.
 *
 * ⛔ Тому — і лише тому — Monaco опиняється в окремому чанку. Один статичний
 * імпорт із будь-якого файлу, що досяжний із маршруту, вкинув би 782 КБ gzip у
 * спільний вхідний чанк і зламав би бюджет `D-132` **одразу для всіх п'ятнадцяти
 * маршрутів**, а не лише для цього. Вхідний чанк зараз 168 КБ із дозволених 250.
 *
 * ⚠ Модуль вантажиться `await import()` із `ExpressionEditor`, і решта коду
 * редактора — токенізатор, доповнення, підкреслення — це чисті модулі без
 * жодного імпорту Monaco: вони перевіряються без нього.
 */

/** Тип, який віддає `await import()` цього модуля. */
export type MonacoModule = typeof monaco;

/** Ім'я світлої теми в Monaco. */
export const LightTheme = 'ecr-light';

/** Ім'я темної теми в Monaco. */
export const DarkTheme = 'ecr-dark';

/**
 * Джерело складу мови для провайдерів.
 *
 * ⚠ Функція, а не значення: провайдери реєструються ОДИН раз на мову, а
 * метадані приходять із сервера пізніше і змінюються при зміні контексту.
 * Замкнути тут значення означало б, що редактор довіку пропонує той перелік,
 * який був у мить реєстрації.
 */
export type MetadataSource = () => ExpressionMetadataDto | undefined;

/**
 * Джерело всього, з чого складаються підказки ОДНОГО редактора: склад мови,
 * структура версії, таблиця виразу.
 *
 * ⛔ Прив'язується до МОДЕЛІ, а не до мови. Провайдери реєструються один раз на
 * мову, і раніше вони замикали джерело ПЕРШОГО редактора, що її зареєстрував.
 * Другий редактор тієї самої мови (діалог формули після `/admin/expressions`,
 * формула іншої версії методології) отримував підказки з метаданих першого —
 * тобто імена чужої версії, без жодного натяку на причину.
 */
export type SymbolSource = () => EditorSymbols;

let themesDefined = false;
const registered = new Set<string>();

/** Джерело підказок кожної живої моделі — за її `uri`. */
const sources = new Map<string, SymbolSource>();

function symbolsOf(
  model: monaco.editor.ITextModel,
  dialect: ExpressionDialect,
  fallback: MetadataSource,
): EditorSymbols {
  return sources.get(model.uri.toString())?.() ?? { dialect, metadata: fallback() };
}

// ⛔ Воркер оголошується РАЗОМ із модулем Monaco, а не в `index.html` і не
// глобально. Без нього Monaco мовчки підміняє воркер заглушкою в головному
// потоці: помилки немає, редактор працює, і лише набір тексту в довгій формулі
// починає підгальмовувати — тобто симптом ніяк не вказує на причину.
//
// ⚠ Вантажиться тим самим `import()`, що й сам Monaco, тому в чанк маршруту не
// потрапляє (`D-132`).
(globalThis as { MonacoEnvironment?: monaco.Environment }).MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
};

/**
 * Готує Monaco до роботи з нашою мовою: теми, мова, провайдери.
 *
 * @param dialect Діалект (`D-113`).
 * @param source Звідки брати склад мови на момент виклику.
 * @returns Ідентифікатор мови для `monaco.editor.create`.
 */
export function prepare(dialect: ExpressionDialect, source: MetadataSource): string {
  defineThemes();

  const languageId = languageIdOf(dialect);

  // ⚠ Повторна реєстрація тієї самої мови додала б ДРУГИЙ провайдер
  // автодоповнення, і кожен варіант з'являвся б у переліку двічі. Помітно це
  // стає лише при другому відкритті редактора — тобто не в тому місці, де
  // шукали б причину.
  if (registered.has(languageId)) {
    return languageId;
  }

  registered.add(languageId);

  monaco.languages.register({ id: languageId });

  monaco.languages.setLanguageConfiguration(
    languageId,

    // ⚠ Приведення через `unknown` неминуче: наші визначення оголошені
    // `readonly`, щоб їх не можна було правити на місці, а Monaco приймає
    // змінювані структури. Це різниця в модифікаторі, а не у формі.
    buildLanguageConfiguration() as unknown as monaco.languages.LanguageConfiguration,
  );

  applyGrammar(languageId, dialect, source);
  registerCompletion(languageId, dialect, source);
  registerSignatureHelp(languageId, dialect, source);
  registerHover(languageId, dialect, source);

  return languageId;
}

/**
 * Перезадає підсвічування, коли перелік функцій уже відомий.
 *
 * ⛔ Викликається ПІСЛЯ відповіді сервера. Доти невідомі імена не позначаються
 * ніяк: червоне через ненадісланий запит виглядає як помилка в тексті, і
 * користувач починає правити те, що правильне.
 */
export function applyGrammar(
  languageId: string,
  dialect: ExpressionDialect,
  source: MetadataSource,
): void {
  const functionNames = source()?.functions.map((f) => f.name) ?? [];

  monaco.languages.setMonarchTokensProvider(
    languageId,
    buildMonarchLanguage({ dialect, functionNames }) as unknown as monaco.languages.IMonarchLanguage,
  );
}

/**
 * Створює редактор у заданому вузлі.
 *
 * ⛔ Налаштування нижче — не смак, а рішення про те, чим це поле НЕ є. Вираз —
 * це один рядок мови, а не файл коду: мінікарта, нумерація рядків, згортання і
 * підсвічування поточного рядка з'їдали б половину висоти поля, нічого не
 * пояснюючи.
 */
export function create(
  host: HTMLElement,
  options: {
    value: string;
    language: string;
    theme: string;
    ariaLabel: string;
    /** Звідки цей редактор бере підказки (`SymbolSource`). */
    symbols?: SymbolSource | undefined;
    readOnly?: boolean | undefined;
  },
): monaco.editor.IStandaloneCodeEditor {
  // ⛔ Перелік підказок, наведення і підказка сигнатури живуть у вузлі на рівні
  // `body`, а не всередині редактора. Редактор стоїть у модальних діалогах
  // (формула колонки, правило валідації, формула методології), а Mantine
  // лишає на вмісті діалогу `transform` після анімації появи — і
  // `position: fixed` віджетів рахується від діалогу, а не від вікна: перелік
  // з'являвся на пів екрана праворуч і нижче від курсора (виміряно на стенді,
  // зсув дорівнював лівому краю діалогу).
  //
  // ⚠ Клас `monaco-editor` на вузлі обов'язковий: стилі й кольори теми Monaco
  // прив'язані до нього, без класу перелік був би безбарвним.
  const overflow = document.createElement('div');
  overflow.className = 'monaco-editor ecr-expression-overflow';
  overflow.style.position = 'absolute';
  overflow.style.top = '0';
  overflow.style.left = '0';
  // Вище за модальний діалог Mantine (200) і його спливні списки (300).
  overflow.style.zIndex = '1000';
  document.body.appendChild(overflow);

  const editor = monaco.editor.create(host, {
    value: options.value,
    language: options.language,
    theme: options.theme,

    // ⛔ Доступна назва — обов'язкова. Monaco малює власне текстове поле без
    // підпису, і жоден `label` Mantine його не накриває: без цього рядка
    // користувач екранного читача чує «редагування тексту» і нічого більше
    // (`ФВ-14.16`).
    ariaLabel: options.ariaLabel,

    minimap: { enabled: false },
    lineNumbers: 'off',
    folding: false,
    glyphMargin: false,
    lineDecorationsWidth: 0,
    lineNumbersMinChars: 0,
    renderLineHighlight: 'none',
    overviewRulerLanes: 0,
    scrollBeyondLastLine: false,
    wordWrap: 'on',
    automaticLayout: true,
    fixedOverflowWidgets: true,
    overflowWidgetsDomNode: overflow,

    // ⚠ Контекстне меню вимкнене: у ньому дії, яких у цій мові немає, —
    // «перейти до визначення», «змінити всі входження», «форматувати».
    // Кожна з них обіцяє те, чого редактор не вміє.
    contextmenu: false,

    // ⛔ Підказки з'являються САМІ, без Ctrl+Space: і під час набору слова, і
    // після символів-тригерів (`[`, `.`, `@`, `!`). Про автодоповнення, що
    // відкривається лише за комбінацією клавіш, дізнається тільки той, хто про
    // нього вже знає. Рядкові літерали виключено: там пишуть текст, а не код.
    quickSuggestions: { other: true, comments: false, strings: false },
    suggestOnTriggerCharacters: true,
    // ⚠ Слова з самого тексту не пропонуються: у виразі на один рядок це лише
    // повтор набраного, а поруч зі справжніми іменами — шум.
    wordBasedSuggestions: 'off',
    hover: { enabled: 'on', delay: 300 },
    readOnly: options.readOnly ?? false,

    scrollbar: { vertical: 'auto', horizontal: 'auto' },
  });

  editor.onDidDispose(() => overflow.remove());

  // ⛔ Escape, що закриває перелік підказок, НЕ повинен закривати діалог. Без
  // цього Mantine отримував той самий натиск і закривав модальне вікно разом
  // із незбереженою формулою — а тепер, коли перелік відкривається сам після
  // кожного `[`, `.` і `@`, Escape став найчастішою клавішею в цьому полі.
  //
  // ⚠ `data-mantine-stop-propagation` на полі вводу — штатний спосіб Mantine
  // сказати «цей Escape не для тебе». Ставиться ЗАЗДАЛЕГІДЬ, щойно віджет
  // з'явився: Mantine слухає `keydown` на `window` у фазі перехоплення, тобто
  // раніше за будь-який обробник на самому редакторі.
  const syncEscape = (): void => {
    const open = '.suggest-widget.visible, .parameter-hints-widget.visible';
    const widgetOpen = overflow.querySelector(open) !== null || host.querySelector(open) !== null;

    for (const input of host.querySelectorAll('textarea, .native-edit-context, [contenteditable]')) {
      if (widgetOpen) input.setAttribute('data-mantine-stop-propagation', 'true');
      else input.removeAttribute('data-mantine-stop-propagation');
    }
  };

  const widgets = new MutationObserver(syncEscape);
  widgets.observe(overflow, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
  editor.onDidDispose(() => widgets.disconnect());

  const model = editor.getModel();

  if (model !== null && options.symbols !== undefined) {
    const key = model.uri.toString();
    sources.set(key, options.symbols);
    editor.onDidDispose(() => sources.delete(key));
  }

  return editor;
}

/** Перемикає мову моделі — коли на тому самому редакторі змінили діалект. */
export function switchLanguage(
  editor: monaco.editor.IStandaloneCodeEditor,
  languageId: string,
): void {
  const model = editor.getModel();
  if (model !== null) monaco.editor.setModelLanguage(model, languageId);
}

/** Перемикає тему редактора. */
export function setTheme(name: string): void {
  monaco.editor.setTheme(name);
}

/** Підкреслює зауваження сервера в моделі редактора. */
export function showDiagnostics(
  model: monaco.editor.ITextModel,
  diagnostics: Parameters<typeof markersFor>[1],
): void {
  const markers = markersFor(model.getValue(), diagnostics).map((m) => ({
    ...m,

    // ⛔ Усі зауваження — рівня `Error`. Сервер не має поля тяжкості
    // (`ExpressionDiagnostic`), і вигадати її з коду помилки означало б
    // призначити частину проблем «попередженнями» на власний розсуд — при
    // тому, що публікація відхиляє версію за БУДЬ-ЯКОЮ з них.
    severity: monaco.MarkerSeverity.Error,
  }));

  monaco.editor.setModelMarkers(model, 'ecr-expressions', markers);
}

function defineThemes(): void {
  if (themesDefined) return;
  themesDefined = true;

  define(LightTheme, 'vs', expressionLight);
  define(DarkTheme, 'vs-dark', expressionDark);
}

function define(name: string, base: 'vs' | 'vs-dark', palette: ExpressionPalette): void {
  monaco.editor.defineTheme(name, {
    base,
    inherit: true,
    rules: [...monacoRules(palette)],
    colors: {
      'editor.background': palette.background,
      'editor.foreground': palette.foreground,
    },
  });
}

function registerCompletion(
  languageId: string,
  dialect: ExpressionDialect,
  source: MetadataSource,
): void {
  monaco.languages.registerCompletionItemProvider(languageId, {
    // ⚠ Квадратна дужка, крапка, знак оклику і собака — символи, після яких
    // перелік має з'явитися САМ. Чекати на Ctrl+Space означало б, що про
    // автодоповнення дізнається лише той, хто про нього вже знає.
    triggerCharacters: [...TriggerCharacters],

    provideCompletionItems(model, position) {
      const text = model.getValue();
      const offset = model.getOffsetAt(position);
      const found = suggestionsAt(text, offset, symbolsOf(model, dialect, source));

      if (found === null) {
        return { suggestions: [] };
      }

      const { context, items } = found;
      const start = model.getPositionAt(context.replaceFrom);

      const toCursor: monaco.IRange = {
        startLineNumber: start.lineNumber,
        startColumn: start.column,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      };

      // ⚠ Автозакриття вже поставило `]` за курсором. Підстановка, що закриває
      // ланку, заміняє і її — інакше виходило б `[C1]]`.
      const throughBracket: monaco.IRange =
        context.bracket?.closed === true ? { ...toCursor, endColumn: position.column + 1 } : toCursor;

      const suggestions = items.map((item) =>
        suggestion(item, item.close === true ? throughBracket : toCursor, context.typed),
      );

      return { suggestions };
    },
  });
}

/**
 * Один варіант Monaco з нашого `CompletionItem`.
 *
 * ⚠ Необов'язкові поля саме ДОДАЮТЬСЯ, а не присвоюються `undefined`: при
 * `exactOptionalPropertyTypes` це різні речі, і другий варіант не компілюється.
 */
function suggestion(
  item: CompletionItem,
  range: monaco.IRange,
  typed: string,
): monaco.languages.CompletionItem {
  const snippet = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;

  if (item.kind === 'hint' && item.hint !== undefined) {
    // ⛔ Пояснення, а не підстановка: вибір нічого не вставляє. Фільтр — рівно
    // набране, щоб Monaco не сховав рядок як «не збігається».
    return {
      label: hintText(item.hint),
      kind: monaco.languages.CompletionItemKind.Text,
      insertText: '',
      filterText: typed,
      sortText: '!',
      range,
    };
  }

  const insert =
    item.kind === 'function'
      ? { insertText: `${item.insert}($0)`, insertTextRules: snippet }
      : item.chain === true
        ? {
            insertText: `${escapeSnippet(item.insert)}].[$0]`,
            insertTextRules: snippet,
            // Наступна ланка: перелік відкривається знову, уже для неї.
            command: { id: 'editor.action.triggerSuggest', title: '' },
          }
        : { insertText: item.close === true ? `${item.insert}]` : item.insert };

  const documentation = itemDoc(item);

  return {
    label:
      item.description === undefined || item.description === '' || item.description === item.label
        ? item.label
        : { label: item.label, description: item.description },
    kind: kindOf(item.kind),
    range,
    ...insert,
    ...(item.sort === undefined ? {} : { sortText: item.sort }),
    ...detail(item),

    // ⛔ Простий рядок, а не `IMarkdownString`. Опис приходить із бази, де
    // його редагує адміністратор; віддати його розмітці означало б
    // зробити підказку редактора місцем рендерингу чужого вмісту.
    ...(documentation === undefined ? {} : { documentation }),
  };
}

/** Екранує текст для вставки як сніпета (`$`, `}`, `\` мають там значення). */
function escapeSnippet(text: string): string {
  return text.replace(/[$}\\]/g, '\\$&');
}

function registerHover(languageId: string, dialect: ExpressionDialect, source: MetadataSource): void {
  monaco.languages.registerHoverProvider(languageId, {
    provideHover(model, position) {
      const info = hoverAt(
        model.getValue(),
        model.getOffsetAt(position),
        symbolsOf(model, dialect, source),
      );

      if (info === null) return null;

      const from = model.getPositionAt(info.from);
      const to = model.getPositionAt(info.to);

      return {
        range: {
          startLineNumber: from.lineNumber,
          startColumn: from.column,
          endLineNumber: to.lineNumber,
          endColumn: to.column,
        },
        // ⛔ Markdown тут — лише рамка: увесь текст із бази екранується
        // (`docMarkdown`), HTML вимкнено, команди недовірені.
        contents: [
          { value: docMarkdown(hoverDoc(info.symbol)), isTrusted: false, supportHtml: false },
        ],
      };
    },
  });
}

function registerSignatureHelp(
  languageId: string,
  dialect: ExpressionDialect,
  source: MetadataSource,
): void {
  monaco.languages.registerSignatureHelpProvider(languageId, {
    signatureHelpTriggerCharacters: ['(', ','],

    provideSignatureHelp(model, position) {
      const text = model.getValue();
      const name = enclosingFunction(text, model.getOffsetAt(position));

      if (name === null) return null;

      // ⛔ Спершу ТОЧНИЙ збіг, і лише потім без регістру. У діалекті методологій
      // регістр значущий (`Round` є, `ROUND` немає), і зведення до одного
      // написання показувало б підказку не тієї функції. Запасний прохід
      // потрібен діалекту шаблонів: там імена ексельні й регістронезалежні,
      // тобто `sum(` і `SUM(` — те саме слово.
      const functions = symbolsOf(model, dialect, source).metadata?.functions ?? [];
      const fn =
        functions.find((f) => f.name === name) ??
        functions.find((f) => f.name.toUpperCase() === name.toUpperCase());

      if (fn === undefined) return null;

      const documentation = itemDoc({ insert: fn.name, label: fn.name, kind: 'function', fn })
        ?.split('\n')
        .slice(1)
        .join('\n');

      return {
        value: {
          signatures: [
            {
              label: signatureOf(fn),
              parameters: [],
              ...(documentation === undefined || documentation === '' ? {} : { documentation }),
            },
          ],
          activeSignature: 0,
          activeParameter: 0,
        },
        dispose: () => {
          // Провайдер нічого не тримає: підказка — це дані, а не ресурс.
        },
      };
    },
  });
}

/**
 * Ім'я функції, усередині дужок якої стоїть курсор.
 *
 * ⚠ Рахує вкладеність назад від курсора: у `SUM(ROUND(x, 2), 3)` підказка має
 * стосуватися `SUM` або `ROUND` залежно від того, де саме стоїть курсор, а не
 * першої знайденої назви.
 *
 * ⛔ Ім'я повертається ЯК НАПИСАНЕ. Тут стояв `.toUpperCase()`, і для діалекту
 * шаблонів це було нешкідливо — там усі 12 імен у верхньому регістрі. Для
 * діалекту методологій він означав би, що підказку не знайдено НІКОЛИ: у
 * виміряному наборі всі імена змішаного регістру (`Pow`, `Round`, `if`), і
 * `POW` серед них немає.
 */
function enclosingFunction(text: string, offset: number): string | null {
  let depth = 0;

  for (let i = Math.min(offset, text.length) - 1; i >= 0; i--) {
    const symbol = text[i];

    if (symbol === ')') depth++;
    else if (symbol === '(') {
      if (depth === 0) {
        return /[A-Za-z_]\w*$/.exec(text.slice(0, i))?.[0] ?? null;
      }

      depth--;
    }
  }

  return null;
}

/**
 * Права колонка переліку підстановок.
 *
 * ⛔ Функція ярусу `Extension` позначається просто в переліку, а не в описі
 * під ним: опис бачить лише той, хто затримався на варіанті, а рішення
 * «брати цю функцію чи ні» ухвалюють у мить вибору. Позначка каже те саме, що
 * і `ECR-CALC-0433`: чинний рушій цього не вміє, тож у версії з
 * `NumericMode = Legacy` такий вираз не опублікується.
 *
 * ⚠ Ярус приходить із СЕРВЕРА. Зашитий тут перелік «наших» функцій став би
 * другою правдою: замір (`tests/Ecr.Legacy.Probe`) переніс би `Ln` у ядро, а
 * редактор далі позначав би її як розширення.
 */
function detail(item: CompletionItem): { detail?: string } {
  const mark = item.tier === 'Extension' ? t('expressions.function.extension') : undefined;

  if (mark === undefined) {
    return item.detail === undefined ? {} : { detail: item.detail };
  }

  return { detail: item.detail === undefined ? mark : `${item.detail} · ${mark}` };
}

function kindOf(kind: CompletionKind): monaco.languages.CompletionItemKind {
  switch (kind) {
    case 'function':
      return monaco.languages.CompletionItemKind.Function;
    case 'constant':
      return monaco.languages.CompletionItemKind.Constant;
    case 'argument':
      return monaco.languages.CompletionItemKind.Variable;
    case 'formula':
      return monaco.languages.CompletionItemKind.Reference;
    case 'row':
      return monaco.languages.CompletionItemKind.EnumMember;
    case 'table':
      return monaco.languages.CompletionItemKind.Struct;
    case 'sheet':
      return monaco.languages.CompletionItemKind.Module;
    case 'keyword':
      return monaco.languages.CompletionItemKind.Keyword;
    case 'hint':
      return monaco.languages.CompletionItemKind.Text;
    default:
      return monaco.languages.CompletionItemKind.Field;
  }
}
