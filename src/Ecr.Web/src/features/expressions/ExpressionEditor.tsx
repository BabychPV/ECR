import { Alert, Stack, useComputedColorScheme } from '@mantine/core';
import { useCallback, useEffect, useRef, useState, type JSX } from 'react';
import type { ExpressionDialect, ExpressionMetadataDto, ExpressionValidationDto } from '@/api/types';
import { expressionMetadata, validateExpression, type ExpressionPlacement } from './api';
import { languageIdOf } from './dialect';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { MonacoModule } from './monaco';

/**
 * Редактор виразів (`ФВ-9.15a`, `D-113`).
 *
 * ⛔ Monaco вантажиться `await import()` і НІКОЛИ статично. Один статичний
 * імпорт вкинув би 782 КБ gzip у спільний вхідний чанк і зламав би бюджет
 * `D-132` одразу для всіх маршрутів, а не лише для цього.
 *
 * ⚠ Компонент навмисно тонкий: уся логіка — токенізатор, доповнення,
 * підкреслення — живе в чистих модулях поруч. Рендер Mantine у jsdom іде
 * хвилинами (`D1-12`), і те, що можна перевірити лише через цей файл, було б
 * практично неперевіреним.
 */

/** Затримка перед перевіркою на сервері, мс. */
const ValidateDelay = 400;

/**
 * Чи це відмова через `AbortController.abort()`, а не справжня помилка.
 *
 * ⚠ `fetch` кидає `DOMException` з `name === 'AbortError'` (стандарт
 * `AbortSignal`); `apiFetch` (`api/client.ts`) нічого тут не перехоплює й не
 * підміняє — відмова доходить до викликача як є.
 */
function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError';
}

/** Властивості редактора. */
export interface ExpressionEditorProps {
  /** Текст виразу. */
  readonly value: string;
  /** Викликається на кожну зміну тексту. */
  readonly onChange: (value: string) => void;
  /**
   * Діалект — **параметр**, а не зашита константа (`D-113`): один компонент
   * обслуговує методології, правила валідації і формули шаблону.
   */
  readonly dialect: ExpressionDialect;
  /** Де живе вираз — визначає повноту перевірки й склад підказок. */
  readonly placement?: ExpressionPlacement | undefined;
  /**
   * Доступна назва поля.
   *
   * ⛔ Обов'язкова. Monaco малює власне текстове поле без підпису, і жоден
   * `label` Mantine його не накриває: без цього рядка користувач екранного
   * читача чує «редагування тексту» і нічого більше (`ФВ-14.16`).
   */
  readonly ariaLabel: string;
  /** Висота поля; за замовчуванням — під однорядкову формулу з запасом. */
  readonly height?: string | undefined;
  /** Повідомляє результат перевірки — щоб показати його поруч. */
  readonly onValidated?: ((result: ExpressionValidationDto) => void) | undefined;
}

/** Редактор виразів із підсвічуванням, підказками і перевіркою при введенні. */
export function ExpressionEditor(props: ExpressionEditorProps): JSX.Element {
  const { value, onChange, dialect, placement, ariaLabel, height, onValidated } = props;

  const host = useRef<HTMLDivElement | null>(null);
  const editor = useRef<ReturnType<MonacoModule['editor']['create']> | null>(null);
  const api = useRef<typeof import('./monaco') | null>(null);

  // ⚠ Метадані в ref, а не в стані: провайдери Monaco замикають ФУНКЦІЮ
  // читання, і перерендер їх не переставляє. Стан тут означав би, що перелік
  // назавжди лишається тим, який був у мить реєстрації.
  const metadata = useRef<ExpressionMetadataDto | undefined>(undefined);

  // ⛔ Ефект створення редактора (нижче) виконується ОДИН раз і ніколи не
  // перезапускається — тож `onDidChangeModelContent`, зареєстрований
  // усередині нього, замкнув би саме ТОЙ `onChange`, що існував на момент
  // монтування, назавжди. Викликач (наприклад, форма формули) типово передає
  // інлайн-стрілку, що читає поточний стан ЗІ СВОГО замикання (`editing`) —
  // застаріла версія такої стрілки записувала б назад застарілий стан,
  // стираючи все, що змінилося в інших полях після монтування редактора.
  // Ref завжди тримає НАЙСВІЖІШУ функцію, без цього ефект мусив би або
  // перестворювати редактор на кожен рендер (скидаючи курсор), або взагалі
  // не міг би обійтися без застарілого замикання.
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;

  // ⛔ Аудит 2026-09-16 §10.3: те саме, і з тієї самої причини, для ТЕКСТУ.
  // Ефект створення (нижче) читав `value` зі свого замикання, тобто значення
  // НА МОМЕНТ МОНТУВАННЯ, а створює редактор асинхронно — після
  // `await import('./monaco')`. Чанк Monaco важить ~818 КБ gzip і через
  // корпоративний канал легко приходить ПІЗНІШЕ за короткий запит формули:
  // форма відкривається з `value=""`, текст доїжджає, редактор створюється з
  // застарілою порожнечею — і перший же натиск клавіші віддає батькові майже
  // порожній рядок, стираючи правильну формулу. Ref завжди тримає найсвіжіший
  // текст, тож `create` сідується тим, що є ЗАРАЗ, а не тим, що було.
  const valueRef = useRef(value);
  valueRef.current = value;

  const scheme = useComputedColorScheme('light');
  const [ready, setReady] = useState(false);
  const [failed, setFailed] = useState(false);

  // ── Відмова запиту складу мови ────────────────────────────────────────────
  //
  // ⛔ Директива №15 §0, правило `L10`: **відмова ≠ порожньо**. Відповідь на
  // `GET /api/v1/expressions/metadata` — це ЄДИНЕ джерело переліку функцій,
  // констант, аргументів і формул. Доки її немає, `metadata.current` лишається
  // `undefined`, а `completionsFor` на `undefined` повертає ПОРОЖНІЙ перелік —
  // тобто той самий екран, що й у діалекті, де функцій справді нема. Автор
  // формули тисне `Ctrl+Space`, не бачить нічого й робить єдиний можливий
  // висновок: підказок тут не буває. Далі він пише імена напам'ять, а
  // підсвічування невідомих імен теж не вмикається (`applyGrammar` не
  // викликано) — отже навіть друкарська помилка в імені лишається без ознаки.
  //
  // ⚠ Помилка тримається як `unknown`, а не як прапорець: `ErrorAlert` показує
  // код відмови й кореляцію, а `403` (немає доступу до версії методології) і
  // `500` — різні розмови з підтримкою.
  const [metadataError, setMetadataError] = useState<unknown>(null);

  // Лічильник спроб: зміна значення перезапускає ефект складу мови.
  const [metadataAttempt, setMetadataAttempt] = useState(0);

  const retryMetadata = useCallback(() => {
    setMetadataError(null);
    setMetadataAttempt((n) => n + 1);
  }, []);

  // ── Відмова запиту перевірки ──────────────────────────────────────────────
  //
  // ⛔ Те саме правило `L10` (**відмова ≠ порожньо**) і для другого запиту.
  // `POST /api/v1/expressions/validate` — ЄДИНЕ джерело підкреслень: доки
  // відповіді немає, `showDiagnostics` не викликано, і редактор виглядає
  // точнісінько так, як на бездоганній формулі. Автор бачить чисте поле й
  // робить єдиний можливий висновок — «помилок немає», — тоді як насправді
  // ніхто нічого не перевіряв.
  //
  // ⛔ До цього тут стояв `throw error` усередині `void (async () => …)()`.
  // Кинуте звідти не доходить до ЖОДНОЇ межі помилок React: async-IIFE нікому
  // не віддає свій проміс, тож це просто неопрацьоване відхилення
  // (`unhandledrejection` у браузері, «шум», що нічого не валить, у тестах).
  // На екрані — рівно ніщо, як і до сусіднього фіксу складу мови.
  //
  // ⚠ `prev ?? error` — з тієї ж причини, що й у складі мови: новий об'єкт
  // помилки в стані дає новий рендер, а рендер перезапускає цей самий ефект.
  // Тотожне значення React відкидає без рендера, і цикл не починається.
  const [validateError, setValidateError] = useState<unknown>(null);

  // Лічильник спроб: зміна значення перезапускає ефект перевірки.
  const [validateAttempt, setValidateAttempt] = useState(0);

  const retryValidate = useCallback(() => {
    setValidateError(null);
    setValidateAttempt((n) => n + 1);
  }, []);

  // ── Створення редактора ───────────────────────────────────────────────────
  useEffect(() => {
    let disposed = false;

    void (async () => {
      try {
        const monaco = await import('./monaco');
        if (disposed || host.current === null) return;

        api.current = monaco;
        const languageId = monaco.prepare(dialect, () => metadata.current);

        editor.current = monaco.create(host.current, {
          // ⛔ З ref, не із замикання (§10.3): між монтуванням і цим рядком
          // проходить усе завантаження чанка редактора, і текст за цей час
          // цілком міг приїхати з сервера.
          value: valueRef.current,
          language: languageId,
          theme: scheme === 'dark' ? monaco.DarkTheme : monaco.LightTheme,
          ariaLabel,
        });

        editor.current.onDidChangeModelContent(() => {
          onChangeRef.current(editor.current?.getValue() ?? '');
        });

        setReady(true);
      } catch {
        // ⛔ Чанк редактора важить 818 КБ gzip, і через корпоративний канал він
        // може не дійти. Порожній прямокутник у цьому місці — найгірше з
        // можливого: людина бачить поле, друкує в нього і не розуміє, чому
        // нічого не відбувається.
        //
        // ⚠ Запасного поля вводу тут НЕМАЄ навмисно. Звичайна textarea
        // виглядала б як той самий редактор і мовчки приймала б текст без
        // підсвічування, підказок і перевірки — тобто обіцяла б перевірку,
        // якої не сталося.
        if (!disposed) setFailed(true);
      }
    })();

    return () => {
      disposed = true;
      editor.current?.dispose();
      editor.current = null;
    };

    // ⛔ Порожній перелік залежностей навмисно: редактор створюється ОДИН раз.
    // Перестворення на зміну тексту скидало б курсор на початок після кожної
    // набраної літери — тобто зробило б поле непридатним для введення.
  }, []);

  // ── Зовнішня зміна тексту ─────────────────────────────────────────────────
  //
  // ⛔ `ready` у залежностях — не про перестраховку (§10.3). Доки Monaco не
  // завантажено, `editor.current === null`, і цей ефект тихо не робить нічого:
  // без `ready` він більше не перезапускався б НІКОЛИ (`value` після
  // завантаження вже не змінюється сам), і текст, що приїхав під час
  // завантаження чанка, лишався б невидимим назавжди. Сідування `create` з
  // `valueRef` вище закриває цю саму щілину з іншого боку; обидва разом
  // означають, що показане завжди дорівнює переданому — незалежно від того,
  // хто прийшов першим.
  useEffect(() => {
    const current = editor.current;
    if (current === null || current.getValue() === value) return;

    current.setValue(value);
  }, [value, ready]);

  // ── Тема ──────────────────────────────────────────────────────────────────
  useEffect(() => {
    const monaco = api.current;
    if (monaco === null) return;

    monaco.setTheme(scheme === 'dark' ? monaco.DarkTheme : monaco.LightTheme);
  }, [scheme, ready]);

  // ── Склад мови ────────────────────────────────────────────────────────────
  useEffect(() => {
    if (!ready) return;
    let disposed = false;

    void (async () => {
      try {
        const loaded = await expressionMetadata(dialect, placement ?? {});
        if (disposed) return;

        metadata.current = loaded;
        setMetadataError(null);

        // ⛔ Підсвічування перезадається лише ТЕПЕР, коли перелік функцій
        // відомий. Доти жодне ім'я не позначається невідомим: червоне через
        // ненадісланий запит виглядає як помилка в тексті, і користувач
        // починає правити те, що правильне.
        const monaco = api.current;
        if (monaco === null) return;

        monaco.applyGrammar(languageIdOf(dialect), dialect, () => metadata.current);
      } catch (error) {
        // ⛔ Без цього `catch` відмова була НЕОПРАЦЬОВАНИМ відхиленням
        // проміса: у браузері це `unhandledrejection` (а в тестах — «шум»,
        // який нічого не валить), на екрані — рівно ніщо.
        if (disposed) return;

        // ⚠ `prev ?? error`, а не просте присвоєння. `placement` прилітає
        // об'єктним літералом принаймні з одного виклику
        // (`features/templates/FormulaEditor.tsx`), тобто НОВИМ посиланням на
        // кожен перерендер, і цей ефект там перезапускається щоразу. Запис
        // нового об'єкта помилки в стан давав би новий рендер → новий запит →
        // нову помилку, тобто нескінченний цикл із запитом на кожному оберті.
        // Тотожне значення React відкидає без рендера, і цикл не починається;
        // зняти прапорець може лише «повторити» (`retryMetadata`).
        setMetadataError((prev: unknown) => prev ?? error);
      }
    })();

    return () => {
      disposed = true;
    };
  }, [ready, dialect, placement, metadataAttempt]);

  // ── Перевірка при введенні ────────────────────────────────────────────────
  useEffect(() => {
    if (!ready) return;

    const controller = new AbortController();

    // ⚠ Затримка, а не запит на кожен натиск: перевірка ходить на сервер, і
    // без неї кожна літера довгої формули коштувала б окремого звернення.
    const timer = globalThis.setTimeout(() => {
      void (async () => {
        try {
          const result = await validateExpression(value, dialect, placement ?? {}, controller.signal);

          // ⛔ Без цієї перевірки старіша відповідь (сервер повільніший саме
          // на ній) могла прийти ПІСЛЯ новішої і мовчки переписати
          // підкреслення та `onValidated` застарілим результатом — щойно
          // введений текст показував би висновок про текст, який користувач
          // уже змінив. `AbortController` тут — не оптимізація мережі, а
          // єдиний спосіб дізнатися, що саме ЦЕЙ запит більше нікому не
          // потрібен.
          if (controller.signal.aborted) return;

          const monaco = api.current;
          const model = editor.current?.getModel();

          if (monaco !== null && model !== null && model !== undefined) {
            monaco.showDiagnostics(model, result.diagnostics);
          }

          setValidateError(null);
          onValidated?.(result);
        } catch (error) {
          // ⚠ Скасований запит — не помилка перевірки, а очікуваний наслідок
          // того, що текст змінився знову: користувача нема чим повідомляти.
          // Це НОРМАЛЬНИЙ стан при швидкому введенні — банер на кожне
          // натискання клавіші був би другою неправдою замість першої.
          if (isAbortError(error)) return;

          // ⛔ Справжня відмова стає ВИДИМОЮ: причина з кодом поруч із
          // редактором. Тихе `throw` у цьому місці означало, що перевірка не
          // відбулася, а екран про це не сказав нічого.
          setValidateError((prev: unknown) => prev ?? error);
        }
      })();
    }, ValidateDelay);

    return () => {
      globalThis.clearTimeout(timer);
      controller.abort();
    };

    // ⚠ `onValidated` навмисно поза переліком: викликач найчастіше передає
    // стрілку, і залежність від неї перезапускала б перевірку на кожен
    // перерендер батька — тобто перетворила б затримку на ніщо.
  }, [ready, value, dialect, placement, validateAttempt]);

  if (failed) {
    return (
      <Alert color="statusError" title={t('expressions.editorFailed')}>
        {t('expressions.editorFailedHint')}
      </Alert>
    );
  }

  return (
    <Stack gap="xs">
      {/*
        ⛔ Банер стоїть ПОРУЧ із редактором, а не ЗАМІСТЬ нього. Писати вираз
        без підказок законно — склад мови їх лише пропонує, перевіряє все одно
        сервер (`validateExpression`). Сховати чи заблокувати поле означало б
        через недоступність довідника відібрати саму можливість роботи.

        ⚠ `AsyncBoundary` тут не взято навмисно: вона малює власний
        `<Title order={4}>`, що рве `heading-order` і валить гейти
        `a11y (dark)`/`a11y (light)`.
      */}
      {metadataError !== null && <ErrorAlert error={metadataError} onRetry={retryMetadata} />}

      {/*
        ⛔ Так само ПОРУЧ, а не ЗАМІСТЬ: писати вираз без перевірки законно —
        остаточне слово однаково за сервером у мить збереження, а редактор без
        підкреслень лишається придатним для введення. Сховати чи заблокувати
        поле через недоступну перевірку означало б відібрати роботу замість
        того, щоб назвати причину.
      */}
      {validateError !== null && <ErrorAlert error={validateError} onRetry={retryValidate} />}

      <div ref={host} style={{ height: height ?? '8rem', width: '100%' }} />
    </Stack>
  );
}
