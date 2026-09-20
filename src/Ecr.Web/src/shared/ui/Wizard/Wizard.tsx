import { useCallback, useEffect, useId, useMemo, useRef, useState, type JSX, type ReactNode } from 'react';
import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';

/**
 * Багатокроковий майстер (`KIT.md` §6 `Wizard`, директива №15 §2, шар 3).
 *
 * `KIT.md:393` називає, коли він узагалі береться: «≥ 2 кроків або є що
 * перевірити перед застосуванням (створити документ, опублікувати версію,
 * імпорт)». Тобто це не «діалог із вкладками» — це дія, ціна якої така, що
 * перед застосуванням треба показати ПІДСУМОК.
 *
 * ⛔ Крок `Review` додається САМ і в `steps` не описується. Інакше його
 * забули б: підсумок перед застосуванням — єдине, заради чого майстер
 * відрізняється від форми, і він же — перше, що випадає, коли кроки пише
 * кожен екран самостійно. `KIT.md:375` формулює це дослівно: «крок Review
 * додається сам».
 *
 * ⛔ Стан кроків — ЛОКАЛЬНИЙ, не в адресі (директива №15 §2, рядок `Wizard`:
 * «стан кроків — локальний, НЕ в URL (крім `?dialog=`)»). Це свідомий виняток
 * із наскрізного правила `ФВ-14.29` («вибір користувача живе в адресі»), і
 * причина в тому, що адресу надсилають колезі. Посилання на «крок 3 з
 * половиною введених даних» відкриється в колеги порожнім кроком 3 — дані ж
 * лишилися в тій вкладці, — і це гірше, ніж відсутнє посилання: воно
 * виглядає робочим. Адресований лише сам ДІАЛОГ (`?dialog=`), і цим
 * опікується сторінка, а не майстер. Тому цей файл не імпортує ні
 * `useUrlState`, ні будь-що з `react-router`.
 *
 * ⚠ Смуга кроків НЕ на Mantine `Stepper`, хоч директива й називає `Stepper`
 * серед «Mantine напряму» (§2, шар 2). Причина технічна й перевірена в
 * джерелі: `StepperStep` завжди рендерить `UnstyledButton`, тобто `<button>`,
 * навіть коли `onStepClick` не заданий. У майстрі переходи по кроках мають
 * іти лише через `validate` (інакше `Next` перевіряє, а клац по смузі —
 * ні), тож ці кнопки нікуди не ведуть — і при цьому забирають на себе `Tab`
 * перед полями кроку. Тут смуга — це підпис, а не орган керування; поточний
 * крок позначений `aria-current="step"`.
 */

/** Помилка перевірки кроку: текст плюс те, куди ставити фокус (`L9`). */
export interface WizardStepError {
  readonly message: string;

  /**
   * CSS-селектор ПЕРШОГО хибного поля — у межах тіла цього кроку.
   *
   * ⚠ Селектор, а не `ref`: крок малює довільний вузол, і вимагати від нього
   * тримати посилання на кожне поле означало б, що `L9` («фокус на перше
   * хибне поле») виконується лише там, де про це не забули. Пошук іде саме
   * ВСЕРЕДИНІ тіла кроку, а не по всьому документу: `#name` на сторінці під
   * діалогом — не те поле, і фокус, що поїхав туди, гірший за відсутній.
   */
  readonly focus?: string | undefined;
}

/**
 * Що повертає `validate`.
 *
 * ⚠ Рядок — скорочення для `{ message }` без фокуса: у кроці з одним полем
 * називати селектор нема сенсу, а змушувати писати об'єкт означало б, що
 * перевірку не додадуть зовсім.
 */
export type WizardStepValidation = string | WizardStepError | null | undefined;

export interface WizardStepRenderArgs<TData> {
  readonly data: TData;

  /** Часткове оновлення даних майстра; позначає їх як незбережені. */
  readonly update: (patch: Partial<TData>) => void;
}

export interface WizardStep<TData> {
  readonly id: string;
  readonly label: string;
  readonly hint?: string | undefined;
  readonly render: (args: WizardStepRenderArgs<TData>) => ReactNode;

  /**
   * Перевірка кроку при спробі піти ДАЛІ (`L9`: «помилки форми — при спробі
   * зберегти»).
   *
   * ⛔ Текст звідси показується банером У ЦЬОМУ Ж КРОЦІ, а не тостом. Тост
   * живе поза діалогом, зникає за кілька секунд і не має зв'язку з полем,
   * яке треба виправити; до того ж читалка оголошує його в момент, коли
   * фокус уже поїхав на поле. Банер лишається на екрані рівно там, де
   * помилка, — доки крок не пройде перевірку.
   */
  readonly validate?: ((data: TData) => WizardStepValidation) | undefined;

  /**
   * Чи дозволено переходити далі (кнопка `Next` вимкнена, доки ні).
   *
   * ⚠ Не заміна `validate`, а доповнення: вимкнена кнопка не пояснює ПРИЧИНИ,
   * тож нею позначається лише очевидно незавершене (нічого не обрано), а все,
   * що потребує пояснення, лишається за `validate`.
   */
  readonly canNext?: ((data: TData) => boolean) | undefined;
}

/** Те, що майстер дає обробнику застосування (`KIT.md:376`). */
export interface WizardApi {
  /** Закрити майстер (застосування вдалося). */
  readonly close: () => void;

  /** Показати відмову банером на кроці `Review`, не закриваючи майстра. */
  readonly fail: (message: string) => void;
}

/** Стан майстра в момент спроби вийти з незбереженим. */
export interface WizardExit<TData> {
  readonly stepId: string;
  readonly stepIndex: number;
  readonly data: TData;
}

/**
 * Підписи органів керування самого майстра.
 *
 * ⛔ ПРОПАМИ, а не ключами каталогу. У каталозі (`09-seed.sql`) із потрібного
 * є лише `common.cancel`; `common.back`/`common.next`/`common.review` там
 * немає, а завести їх звідси не можна — сід спільний і в цій підзадачі
 * недоступний (CLAUDE.md §2: спільні ресурси в паралельній фазі read-only).
 * Вигадати ключ і викликати `t('common.next')` означало б показати
 * `⟦common.next⟧` на кожній кнопці кожного майстра — тобто зламати рівно те,
 * що ключ мав зробити правильним.
 *
 * ⚠ Коли ключі з'являться, зміна буде в одному місці: дефолти тут, а не
 * тридцять викликів `t()` по екранах.
 */
export interface WizardLabels {
  readonly back: string;
  readonly next: string;

  /** Підпис кроку `Review` у смузі кроків і в заголовку кроку. */
  readonly review: string;

  /** ⚠ За замовчуванням — чинний ключ каталогу `common.cancel`. */
  readonly cancel?: string | undefined;
}

export interface WizardProps<TData> {
  readonly opened: boolean;

  /** ⚠ Із назвою об'єкта дії: «Publish version 3», не «Wizard». */
  readonly title: string;

  readonly initialData: TData;
  readonly steps: readonly WizardStep<TData>[];

  /**
   * Підсумок для кроку `Review` — те, що людина щойно ввела, зібране докупи.
   *
   * ⚠ `D15-06`: порожній підсумок НЕ малює порожньої рамки. Крок `Review`
   * лишається (його сенс — пауза перед незворотним), але місце під підсумок
   * не резервується.
   */
  readonly summary: (data: TData) => ReactNode;

  /** Речення над підсумком («Перевірте, перш ніж публікувати»). */
  readonly reviewText?: string | undefined;

  /** ⚠ ДІЄСЛОВО на кнопці застосування: «Publish version», не «OK». */
  readonly applyLabel: string;

  readonly danger?: boolean | undefined;
  readonly labels: WizardLabels;

  /** Ширший діалог для кроків із таблицею чи редактором. */
  readonly wide?: boolean | undefined;

  readonly isApplying?: boolean | undefined;
  readonly onApply: (data: TData, api: WizardApi) => void;
  readonly onClose: () => void;

  /**
   * Вихід із НЕЗБЕРЕЖЕНИМ (`KIT.md:33`: «вихід із незбереженим —
   * `UnsavedGuard`»).
   *
   * ⛔ Проп ОБОВ'ЯЗКОВИЙ, і це головне рішення цього інтерфейсу. Майстер
   * існує для дій, які варто зібрати за кілька кроків; закрити його `Esc`
   * після трьох заповнених кроків і мовчки втратити введене — рівно та
   * поведінка, проти якої написаний `D14-12`. Зробити проп необов'язковим
   * означало б, що більшість викликів його не передасть (нема чого
   * заповнювати — нема про що думати), і мовчазна втрата лишилася б
   * ТИПОВОЮ поведінкою.
   *
   * ⚠ Викликається ЛИШЕ коли є що втрачати: доки жодного `update` не було,
   * `Esc` закриває майстра одразу. Питання на порожньому майстрі — це та сама
   * звичка відповідати «так» не читаючи, яку описує `UnsavedGuard.tsx`.
   *
   * Повертає `true` — закриваємо зараз; `false` — майстер лишається
   * відкритим, а викликач показує власне підтвердження (і закриє майстра
   * сам, знявши `opened`).
   */
  readonly onExitUnsaved: (exit: WizardExit<TData>) => boolean;
}

/**
 * Ідентифікатор кроку підсумку.
 *
 * ⚠ Зарезервований: крок із таким `id` у `steps` — помилка розробника, і в
 * режимі розробки майстер на ній падає (нижче). Мовчазне перейменування дало
 * б два кроки з однаковим `id` у смузі й непередбачувані переходи.
 */
export const WizardReviewStepId = 'review';

/** Запит на постановку фокуса; новий об'єкт = новий запит (`L9`). */
interface FocusRequest {
  readonly selector: string;
}

export function Wizard<TData>({
  opened,
  title,
  initialData,
  steps,
  summary,
  reviewText,
  applyLabel,
  danger,
  labels,
  wide,
  isApplying,
  onApply,
  onClose,
  onExitUnsaved,
}: WizardProps<TData>): JSX.Element {
  /*
   * ⛔ Те саме, що в `DataTable` на восьмій колонці (`L5`): у режимі розробки
   * ВИНЯТОК, а не `console.error`. `src/test/setup.ts` консоль не стереже,
   * тож попередження не побачив би ніхто — ні в тесті, ні в браузері, де воно
   * загубилося б серед іншого.
   */
  if (import.meta.env.DEV) {
    if (steps.length === 0) {
      throw new Error(
        'Wizard без кроків — це ConfirmDialog: крок Review сам собою не є майстром (KIT.md:393).',
      );
    }

    if (steps.some((entry) => entry.id === WizardReviewStepId)) {
      throw new Error(
        `Ідентифікатор "${WizardReviewStepId}" зарезервовано за кроком підсумку, який Wizard додає сам (KIT.md:375).`,
      );
    }
  }

  const [index, setIndex] = useState(0);
  const [data, setData] = useState<TData>(initialData);
  const [dirty, setDirty] = useState(false);
  const [failure, setFailure] = useState<WizardStepError | null>(null);
  const [focusRequest, setFocusRequest] = useState<FocusRequest | null>(null);

  const bodyRef = useRef<HTMLDivElement>(null);
  const labelId = useId();

  /*
   * ⚠ `initialData` живе у `ref`, а не в залежностях ефекту нижче. Викликачі
   * передають літерал об'єкта, який перестворюється на КОЖНОМУ рендері
   * сторінки, тож залежність скидала б уже введене — і саме на тих екранах,
   * де сторінка перемальовується частіше (запит у польоті, тост, тікаючий
   * таймер).
   */
  const initialRef = useRef(initialData);
  initialRef.current = initialData;

  /*
   * ⚠ Скидання при КОЖНОМУ відкритті, а не при закритті — дослівно той самий
   * аргумент, що в `ReasonModal`/`ConfirmModal`: дані попереднього майстра,
   * що лишилися в полях, — найтихіший спосіб опублікувати версію з
   * параметрами зовсім іншої.
   */
  useEffect(() => {
    if (!opened) return;

    setIndex(0);
    setData(initialRef.current);
    setDirty(false);
    setFailure(null);
    setFocusRequest(null);
  }, [opened]);

  /*
   * ⛔ Фокус ставиться в ЕФЕКТІ, а не в обробнику натискання. На кроці, куди
   * майстер щойно перейшов (перевірка при `Apply` знайшла хибний крок
   * позаду), потрібного поля в DOM ще немає — `querySelector` у тому ж такті
   * не знайшов би нічого, і `L9` виконувалося б лише для поточного кроку.
   * Ефект виконується після коміту, тобто коли поля вже намальовані.
   */
  useEffect(() => {
    if (focusRequest === null) return;

    bodyRef.current?.querySelector<HTMLElement>(focusRequest.selector)?.focus();
  }, [focusRequest]);

  const update = useCallback((patch: Partial<TData>) => {
    setData((current) => ({ ...current, ...patch }));
    setDirty(true);
  }, []);

  const reviewIndex = steps.length;
  const onReview = index >= reviewIndex;
  const step = steps[Math.min(index, Math.max(reviewIndex - 1, 0))];

  /** Смуга кроків: описані кроки плюс доданий `Review`. */
  const stepLabels = useMemo(
    () => [...steps.map((entry) => entry.label), labels.review],
    [steps, labels.review],
  );

  /**
   * Перевіряє крок; `null` — можна далі.
   *
   * ⚠ Нормалізує обидві форми відповіді (`'текст'` і `{ message, focus }`) в
   * одному місці — інакше кожен виклик розбирав би тип сам, і рядкова форма
   * рано чи пізно десь загубилася б.
   */
  const checkStep = useCallback(
    (entry: WizardStep<TData>, value: TData): WizardStepError | null => {
      const result = entry.validate?.(value);

      if (result === null || result === undefined) return null;
      if (typeof result === 'string') return result.length === 0 ? null : { message: result };

      return result.message.length === 0 ? null : result;
    },
    [],
  );

  /** Показує відмову кроку й замовляє фокус на першому хибному полі. */
  const reject = useCallback((problem: WizardStepError): void => {
    setFailure(problem);

    // ⚠ Новий ОБ'ЄКТ щоразу: та сама помилка на тому самому полі двічі
    // поспіль мусить повернути туди фокус вдруге, а не мовчки нічого не
    // зробити через рівність значень.
    setFocusRequest(
      problem.focus === undefined || problem.focus.length === 0
        ? null
        : { selector: problem.focus },
    );
  }, []);

  const goNext = useCallback((): void => {
    if (step === undefined) return;

    const problem = checkStep(step, data);

    if (problem !== null) {
      reject(problem);

      return;
    }

    setFailure(null);
    setFocusRequest(null);
    setIndex((current) => Math.min(current + 1, reviewIndex));
  }, [checkStep, data, reject, reviewIndex, step]);

  const goBack = useCallback((): void => {
    /*
     * ⛔ Дані НЕ скидаються: `data` живе в майстрі, а не в кроці, тож
     * повернення показує рівно те, що було введено. Знімається лише банер —
     * він стосувався кроку, з якого ми пішли.
     */
    setFailure(null);
    setFocusRequest(null);
    setIndex((current) => Math.max(current - 1, 0));
  }, []);

  const api = useMemo<WizardApi>(
    () => ({
      close: onClose,
      fail: (message: string) => {
        setFailure({ message });
      },
    }),
    [onClose],
  );

  const apply = useCallback((): void => {
    /*
     * ⛔ Перед застосуванням перевіряються ВСІ кроки, а не лише останній
     * пройдений. Дані можна змінити й після того, як крок було пройдено
     * (повернувся назад, стер поле, пішов уперед по вже перевірених кроках),
     * і майстер, що покладається на «колись проходило», застосував би саме
     * те, від чого захищає `validate`. Перший, що не проходить, і стає
     * поточним — разом із банером і фокусом (`L9`).
     */
    for (let position = 0; position < steps.length; position += 1) {
      const entry = steps[position];
      if (entry === undefined) continue;

      const problem = checkStep(entry, data);
      if (problem === null) continue;

      setIndex(position);
      reject(problem);

      return;
    }

    setFailure(null);
    setFocusRequest(null);
    onApply(data, api);
  }, [api, checkStep, data, onApply, reject, steps]);

  const requestClose = useCallback((): void => {
    if (!dirty) {
      onClose();

      return;
    }

    const exit: WizardExit<TData> = {
      stepId: onReview ? WizardReviewStepId : (step?.id ?? WizardReviewStepId),
      stepIndex: index,
      data,
    };

    if (onExitUnsaved(exit)) onClose();
  }, [data, dirty, index, onClose, onExitUnsaved, onReview, step]);

  const summaryNode = onReview ? summary(data) : null;
  const hasSummary =
    summaryNode !== null && summaryNode !== undefined && summaryNode !== false && summaryNode !== '';

  const hasReviewText = reviewText !== undefined && reviewText.length > 0;
  const hint = onReview ? undefined : step?.hint;
  const hasHint = hint !== undefined && hint.length > 0;

  const nextBlocked = !onReview && step?.canNext !== undefined && !step.canNext(data);

  return (
    <Modal opened={opened} onClose={requestClose} title={title} size={wide === true ? 'xl' : 'lg'}>
      <Stack gap="md">
        {/*
         * Смуга кроків. Не орган керування (див. шапку файлу) — підпис.
         * `aria-current="step"` — єдине, що тут потрібне читалці.
         */}
        <Group gap="xs" data-testid="wizard-steps">
          {stepLabels.map((entry, position) => (
            <Text
              key={`${String(position)}:${entry}`}
              size="sm"
              data-step-label={entry}
              {...(position === index
                ? { fw: 600, 'aria-current': 'step' as const }
                : { c: 'dimmed' })}
            >
              {entry}
            </Text>
          ))}
        </Group>

        {/*
         * ⚠ `data-autofocus` на ТІЛІ кроку, а не на кнопці. Пастка фокуса
         * Mantine без цього атрибута бере перший фокусований вузол діалогу —
         * хрестик у шапці, — і людина, яка відкрила майстер, щоб щось
         * заповнити, починає з кнопки «закрити». Фокус на конкретному полі
         * поставити звідси неможливо: поля малює крок. Тому фокус іде на
         * НАЗВАНУ область кроку: читалка оголошує підпис кроку, а `Tab` веде
         * у перше поле — рівно те, чого людина й чекає.
         */}
        <div
          ref={bodyRef}
          data-autofocus
          tabIndex={-1}
          role="group"
          aria-labelledby={labelId}
          data-testid="wizard-step-body"
          data-step-id={onReview ? WizardReviewStepId : step?.id}
        >
          <Stack gap="xs">
            <Text id={labelId} fw={600}>
              {onReview ? labels.review : (step?.label ?? '')}
            </Text>

            {/* ⛔ `D15-06`: підказки немає — рядка немає. */}
            {hasHint ? (
              <Text size="sm" c="dimmed">
                {hint}
              </Text>
            ) : null}

            {/*
             * ⛔ Банер, а не тост, і саме ТУТ — усередині кроку, над його
             * полями. `Banner` із тоном `danger` несе `role="alert"`, тобто
             * читалка оголошує текст негайно, а фокус уже стоїть на хибному
             * полі.
             */}
            {failure !== null ? (
              <Banner tone="danger" text={failure.message} testId="wizard-step-error" />
            ) : null}

            {onReview ? (
              <>
                {hasReviewText ? <Text size="sm">{reviewText}</Text> : null}

                {/* ⛔ `D15-06`: порожній підсумок не малює порожньої рамки. */}
                {hasSummary ? <div data-testid="wizard-summary">{summaryNode}</div> : null}
              </>
            ) : (
              step?.render({ data, update })
            )}
          </Stack>
        </div>

        <Group justify="flex-end" gap="xs">
          {/*
           * ⛔ `D15-06`: на першому кроці кнопки «Назад» НЕМАЄ зовсім, а не
           * вимкнена. Вимкнена кнопка займає місце й читається як «тут щось
           * зламалося», хоча назад просто нікуди.
           */}
          {index > 0 ? (
            <Button variant="default" onClick={goBack} data-testid="wizard-back">
              {labels.back}
            </Button>
          ) : null}

          <Button variant="default" onClick={requestClose} data-testid="wizard-cancel">
            {labels.cancel ?? t('common.cancel')}
          </Button>

          {/*
           * ⛔ `L1`: у діалозі рівно ОДНА `primary`-кнопка. `Next` і `Apply`
           * ніколи не існують одночасно — це один орган керування, що міняє
           * підпис на останньому кроці. `variant` заданий ЯВНО: без пропа
           * Mantine не друкує `data-variant`, і правило стало б
           * неперевірюваним (див. `PageHeaderActions.tsx`).
           */}
          {onReview ? (
            <Button
              variant="filled"
              {...(danger === true ? { color: 'statusError' } : {})}
              loading={isApplying === true}
              onClick={apply}
              data-testid="wizard-apply"
            >
              {applyLabel}
            </Button>
          ) : (
            <Button
              variant="filled"
              disabled={nextBlocked}
              onClick={goNext}
              data-testid="wizard-next"
            >
              {labels.next}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
