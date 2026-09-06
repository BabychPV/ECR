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
import { completionAt, completionsFor, signatureOf, type CompletionItem } from './completion';
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

let themesDefined = false;
const registered = new Set<string>();

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
  registerCompletion(languageId, source);
  registerSignatureHelp(languageId, source);

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
  options: { value: string; language: string; theme: string; ariaLabel: string },
): monaco.editor.IStandaloneCodeEditor {
  return monaco.editor.create(host, {
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

    // ⚠ Контекстне меню вимкнене: у ньому дії, яких у цій мові немає, —
    // «перейти до визначення», «змінити всі входження», «форматувати».
    // Кожна з них обіцяє те, чого редактор не вміє.
    contextmenu: false,

    scrollbar: { vertical: 'auto', horizontal: 'auto' },
  });
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

function registerCompletion(languageId: string, source: MetadataSource): void {
  monaco.languages.registerCompletionItemProvider(languageId, {
    // ⚠ Крапка, знак оклику і собака — символи, після яких перелік має
    // з'явитися САМ. Чекати на Ctrl+Space означало б, що про автодоповнення
    // дізнається лише той, хто про нього вже знає.
    triggerCharacters: ['.', '!', '@'],

    provideCompletionItems(model, position) {
      const text = model.getValue();
      const offset = model.getOffsetAt(position);
      const context = completionAt(text, offset);

      if (context === null) {
        return { suggestions: [] };
      }

      const start = model.getPositionAt(context.replaceFrom);

      const range: monaco.IRange = {
        startLineNumber: start.lineNumber,
        startColumn: start.column,
        endLineNumber: position.lineNumber,
        endColumn: position.column,
      };

      // ⚠ Необов'язкові поля саме ДОДАЮТЬСЯ, а не присвоюються `undefined`:
      // при `exactOptionalPropertyTypes` це різні речі, і другий варіант не
      // компілюється.
      const suggestions = completionsFor(context, source()).map((item) => ({
        label: item.label,
        kind: kindOf(item.kind),
        insertText: item.kind === 'function' ? `${item.insert}($0)` : item.insert,
        range,
        ...(item.kind === 'function'
          ? { insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet }
          : {}),
        ...detail(item),

        // ⛔ Простий рядок, а не `IMarkdownString`. Опис приходить із бази, де
        // його редагує адміністратор; віддати його розмітці означало б
        // зробити підказку редактора місцем рендерингу чужого вмісту.
        ...(item.documentation === undefined ? {} : { documentation: item.documentation }),
      }));

      return { suggestions };
    },
  });
}

function registerSignatureHelp(languageId: string, source: MetadataSource): void {
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
      const functions = source()?.functions ?? [];
      const fn =
        functions.find((f) => f.name === name) ??
        functions.find((f) => f.name.toUpperCase() === name.toUpperCase());

      if (fn === undefined) return null;

      return {
        value: {
          signatures: [{ label: signatureOf(fn), parameters: [] }],
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

function kindOf(kind: string): monaco.languages.CompletionItemKind {
  switch (kind) {
    case 'function':
      return monaco.languages.CompletionItemKind.Function;
    case 'constant':
      return monaco.languages.CompletionItemKind.Constant;
    case 'argument':
      return monaco.languages.CompletionItemKind.Variable;
    case 'formula':
      return monaco.languages.CompletionItemKind.Reference;
    default:
      return monaco.languages.CompletionItemKind.Field;
  }
}
