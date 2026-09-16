import { Alert, useComputedColorScheme } from '@mantine/core';
import { useEffect, useRef, useState, type JSX } from 'react';
import type { ExpressionDialect, ExpressionMetadataDto, ExpressionValidationDto } from '@/api/types';
import { expressionMetadata, validateExpression, type ExpressionPlacement } from './api';
import { languageIdOf } from './dialect';
import { t } from '@/shared/i18n';
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
      const loaded = await expressionMetadata(dialect, placement ?? {});
      if (disposed) return;

      metadata.current = loaded;

      // ⛔ Підсвічування перезадається лише ТЕПЕР, коли перелік функцій
      // відомий. Доти жодне ім'я не позначається невідомим: червоне через
      // ненадісланий запит виглядає як помилка в тексті, і користувач
      // починає правити те, що правильне.
      const monaco = api.current;
      if (monaco === null) return;

      monaco.applyGrammar(languageIdOf(dialect), dialect, () => metadata.current);
    })();

    return () => {
      disposed = true;
    };
  }, [ready, dialect, placement]);

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

          onValidated?.(result);
        } catch (error) {
          // ⚠ Скасований запит — не помилка перевірки, а очікуваний наслідок
          // того, що текст змінився знову: користувача нема чим повідомляти.
          if (isAbortError(error)) return;

          throw error;
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
  }, [ready, value, dialect, placement]);

  if (failed) {
    return (
      <Alert color="statusError" title={t('expressions.editorFailed')}>
        {t('expressions.editorFailedHint')}
      </Alert>
    );
  }

  return <div ref={host} style={{ height: height ?? '8rem', width: '100%' }} />;
}
