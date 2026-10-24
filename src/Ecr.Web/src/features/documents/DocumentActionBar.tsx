import { lazy, Suspense, useRef, useState, type JSX, type MouseEvent, type ReactNode, type RefObject } from 'react';
import { Button, Menu, Text } from '@mantine/core';
import { exportFormatOptions, type ExportFormat } from '@/features/export/exportFormat';
import { RecalculateKbd, submitLabel, useSheetActions, type SheetActionsModel } from '@/features/workflow/SheetActions';
import { Hint } from '@/shared/ui/Hint';
import type { DocumentLock } from './documentLock';
import { DocumentSheetBanner } from './DocumentSheetBanner';
import { DocumentToolbar } from './DocumentToolbar';
// ⛔ Статично, НЕ `lazy()`: ▾ і «Next to submit: …» розширюють групу дій, і з лінивим чанком вона
// переносилась на другий рядок через ~0,5 с ПІСЛЯ появи «Подати», зсуваючи «More» під курсором. Клік,
// що припадав на зсув (натиск на старому місці «More», відпуск — уже поза нею), губився: меню не
// відкривалось (e2e-stand 39994116932, 40023308269 — «Edit access» не з'являвся 45 с). Модуль — кілька
// сотень байтів із `Menu`/`Button`/`Text`, що вже в чанку маршруту (`D-132`).
import { SubmitPeriodPicker } from './SubmitPeriodPicker';
import { useStaleResultsReminder, type StaleReminderState } from './useStaleResultsReminder';
import { t } from '@/shared/i18n';

// ⚠ `import()` — той самий чанк, що й раніше в `DocumentPage` (`D-132`): імпорт
// не потрібен у мить першого малюнка сторінки.
const ImportPanel = lazy(async () => ({ default: (await import('@/features/import/ImportPanel')).ImportPanel }));
// Банер «результати застаріли» — лише коли сервер так каже (`resultsStale === true`), тож окремим чанком (`D-132`).
const StaleResultsBanner = lazy(async () => ({ default: (await import('./StaleResultsBanner')).StaleResultsBanner }));
// Експорт (задача, опитування, тост із посиланням) — окремим чанком, з першим запуском (`D-132`).
const loadExportButton = () => import('@/features/export/ExportButton');
const ExportButton = lazy(async () => ({ default: (await loadExportButton()).ExportButton }));

/** Що сторінка знає про документ і аркуш для рядка дій. */
interface DocumentActionBarProps {
  readonly documentId: number;
  readonly periodKey: number;
  readonly sheetDefId: number;
  /** Код активного аркуша — для банера «хто, коли» (UI-26). */
  readonly sheetCode: string;
  readonly sheetName: string;
  /** Стан активного аркуша за період. */
  readonly state: string;
  readonly lock: DocumentLock | null;
  /** Сітки не редагуються (стан аркуша чи `F-18`). */
  readonly readOnly: boolean;
  /** Мова книги експорту. */
  readonly language: string;
  readonly canImport: boolean;
  /**
   * ✎ D-445: аркуш поданий і правки в ньому вирішує сервер (погоджувач поточного кроку, без замка й вузького
   * екрана) — імпорт показується тому, хто може погоджувати, попри `readOnly`.
   */
  readonly approverImport?: boolean;
  readonly canExport: boolean;
  /** Числа методологій застаріли: пункт перерахунку в «More» називається «Recalculate calculations». */
  readonly calculationsStale?: boolean;
  /** Перевірка: лише читає збережене. */
  readonly validate: { readonly loading: boolean; readonly run: () => void };
  /** Рідкісні дії документа (зміна ключа, перенос версії, видалення) — кінцем меню. */
  readonly documentItems: readonly (JSX.Element | null)[];
  /** Ліва частина рядка: чип аркуша, прогрес, стан збереження. */
  readonly status?: ReactNode;
  /** Результати методологій застаріли (сервер виводить; `null`/відсутнє — банера немає). */
  readonly resultsStale?: boolean | null;
  /** Відколи застаріло (UTC ISO); `null`/відсутнє — без дати. */
  readonly resultsStaleSince?: string | null;
  /** Власник нагадування «вихід із застарілим» - сторінка (N3-08); без нього нагадує сам рядок. */
  readonly staleReminderHost?: RefObject<StaleReminderState>;
}

/**
 * Дії документа за макетом (UI-14; `docs/design/hybrid/screen-document.js`,
 * `renderActions`): Draft — «Validate · More · Submit»; Submitted для
 * погоджувача — «Reject · More · Approve»; затверджений — «Return for edits ·
 * More». Імпорт, експорт (формати), перерахунок, відкликання й рідкісні дії
 * документа — у «More».
 *
 * ⛔ ЯКІ дії дозволені — вирішує `useSheetActions` (стан, право, грант,
 * симуляція, `F-18`, A2-08 «автор не погоджує свого»), тут лише розкладка.
 * Імпорт і експорт лишаються змонтованими ПОЗА меню (поле файлу, діалог
 * перегляду, стеження за задачею експорту): меню розмонтовує вміст, щойно
 * закривається, а пункти лише запускають їх.
 */
export function DocumentActionBar({
  documentId,
  periodKey,
  sheetDefId,
  sheetCode,
  sheetName,
  state,
  lock,
  readOnly,
  language,
  canImport,
  approverImport = false,
  canExport,
  calculationsStale = false,
  validate,
  documentItems,
  status,
  resultsStale,
  resultsStaleSince,
  staleReminderHost,
}: DocumentActionBarProps): JSX.Element {
  const actions = useSheetActions({ documentId, sheetDefId, sheetName, periodKey, state, lock });
  useStaleResultsReminder(documentId, resultsStale === true, actions.recalculate !== null, staleReminderHost);
  // ✎ D-441 CLS: доки сервер не відповів, ЧИ і ЯКИЙ період подавати, остаточна форма рядка дій невідома
  // (кнопка «Подати: <період>», ▾, «Next to submit: …» або нічого) — скелет замість дій (`DocumentToolbar`,
  // `ActionsPlaceholder`). Рішення — у моделі (`useSheetActions().submitPending`):
  // - лише ПЕРША відповідь і лише перша спроба: збій із повторами не тримає дії схованими; `isLoading`, а не
  //   `isPending`: без мережі запит стоїть на паузі і скелет висів би вічно;
  // - ✎ зміна аркуша/періоду скелета НЕ дає: лишається відповідь попереднього ключа (`keepPreviousData`), рядок на
  //   місці, а «Подати» недоступна (`submit.checking`), доки не прийде ціль нового;
  // - ✎ людина без права подання скелета не бачить: «Подати» їй не буде за будь-якої відповіді.
  const actionsPending = actions.submitPending;
  const openImport = useRef<((returnTo?: HTMLElement | null) => void) | null>(null);
  const startExport = useExportStarter();

  // ✎ D-445: поданий аркуш імпортує погоджувач поточного кроку (як править у сітці, D-442). Крок маршруту
  // знає сервер — `approverImport` приходить із його відповіді (`GET …/approver-edit`, те саме правило, що й запис),
  // тож погоджувач ІНШОГО кроку пункту не бачить; остаточне «так/ні» однаково дає перегляд імпорту на сервері.
  const showImport = canImport && (!readOnly || (approverImport && actions.approve !== null));

  // ⚠ «Validate» ховається в меню лише там, де панель зайнята рішенням
  // погоджувача чи поверненням у роботу (макет: «Reject · More · Approve»,
  // «Return for edits · More»); на чернетці й там, де дій процесу немає
  // (перегляд, закритий період), перевірка лишається видимою.
  const validateVisible =
    actions.submit !== null || (actions.approve === null && actions.reject === null && actions.reopen === null);

  const validateButton = (
    <Button variant="default" loading={validate.loading} onClick={validate.run}>
      {t('document.validate')}
    </Button>
  );

  const items: (JSX.Element | null)[] = [
    validateVisible ? null : (
      <Menu.Item key="validate" disabled={validate.loading} onClick={validate.run}>
        {t('document.validate')}
      </Menu.Item>
    ),
    showImport ? (
      <Menu.Item
        key="import"
        // ⚠ Фокус після перегляду — на «More»: пункт меню на той час розмонтовано.
        onClick={() => openImport.current?.(document.querySelector<HTMLElement>('[data-testid="document-more"]'))}
      >
        {`${t('import.pick')}…`}
      </Menu.Item>
    ) : null,
    ...(canExport
      ? [
          <Menu.Label key="export-label">{t('document.export')}</Menu.Label>,
          ...exportFormatOptions().map((option) => (
            <Menu.Item
              key={`export-${option.value}`}
              onClick={() => startExport.start(option.value)}
              onPointerEnter={() => void loadExportButton()}
            >
              {option.label}
            </Menu.Item>
          )),
        ]
      : []),
    actions.recalculate !== null || actions.recall !== null ? <Menu.Divider key="workflow-divider" /> : null,
    actions.recalculate !== null ? (
      <Menu.Item
        key="recalculate"
        disabled={actions.recalculate.loading}
        // RC14-B: поданий сусідній аркуш - пункт лишається, `aria-disabled` + причина замість підказки.
        {...(actions.recalculate.blockedReason !== null ? { 'data-disabled': true, 'aria-disabled': true } : {})}
        onClick={actions.recalculate.run}
        // `UI-41` (пачка batch-3): F9 — та сама дія; клавішу видно й чути і в меню «More».
        aria-keyshortcuts="F9"
        rightSection={<RecalculateKbd />}
      >
        <Text size="sm">
          {actions.recalculate.running
            ? t('workflow.recalcRunning')
            : calculationsStale
              ? t('workflow.recalculateCalculations')
              : t('workflow.recalculate')}
        </Text>
        <Text size="xs" c="dimmed" maw={280}>
          {actions.recalculate.blockedReason ?? t('workflow.recalculateHint')}
        </Text>
      </Menu.Item>
    ) : null,
    actions.recall !== null ? (
      <Menu.Item key="recall" onClick={actions.recall.ask}>
        {t('workflow.recall')}
      </Menu.Item>
    ) : null,
  ];

  const rare = documentItems.filter((item) => item !== null);
  if (rare.length > 0) items.push(<Menu.Divider key="document-divider" />, ...rare);

  // ⛔ Одна головна дія (`L1`): подання або затвердження — ніколи обидві `filled`.
  // ✎ D-441: подання йде в НАЙРАНІШИЙ неподаний період, а не у відкритий, тож на поданому аркуші
  // погоджувач-автор може мати обидві дії: тоді головна — затвердження (воно про те, що на екрані),
  // а подання — другорядною кнопкою поруч.
  const submitPrimary = actions.submit !== null && actions.approve === null;
  const primary =
    actions.submit !== null && submitPrimary ? (
      // ⚠ `variant="filled"` явно: за ним екранний тест рахує головні дії (`L1`).
      <SubmitAction submit={actions.submit} variant="filled" />
    ) : actions.approve !== null ? (
      // ⛔ `X-05`: `statusSuccess` підібрано до AA 4.5:1 (`theme.ts`, `contrast.test.ts`).
      <Button
        variant="filled"
        color="statusSuccess"
        loading={actions.approve.loading}
        onClick={actions.approve.ask}
      >
        {t('workflow.approve')}
      </Button>
    ) : null;

  return (
    <>
      <DocumentToolbar
        status={
          <>
            {status}
            {/* Задача експорту триває — «Building…» тут, поруч зі станом. */}
            {canExport && startExport.mounted && (
              <Suspense fallback={null}>
                <ExportButton
                  documentId={documentId}
                  periodKey={periodKey}
                  language={language}
                  withTrigger={false}
                  startRef={startExport.ref}
                />
              </Suspense>
            )}
          </>
        }
        more={items}
        primary={primary}
        pending={actionsPending}
      >
        {validateVisible && validateButton}

        {actions.submit !== null && !submitPrimary && <SubmitAction submit={actions.submit} variant="default" />}

        {/* ⛔ Затвердження і відхилення — пара: «Reject» поруч з «Approve». */}
        {actions.reject !== null && (
          <Button color="statusError" variant="outline" onClick={actions.reject.ask}>
            {t('workflow.reject')}
          </Button>
        )}

        {/* ⚠ На затвердженому аркуші головної дії немає — повернення в роботу
            (окреме небезпечне право, причина обов'язкова) лишається видимим. */}
        {actions.reopen !== null && (
          <Button variant="default" onClick={actions.reopen.ask}>
            {t('workflow.reopen')}
          </Button>
        )}
      </DocumentToolbar>

      {/* ⛔ `F-18`: ЧОМУ тут нічого не змінити — одразу під рядком дій, до
          будь-якої сітки; ✎ UI-26: для поданого, затвердженого й
          відхиленого аркуша — хто, коли і що робити далі цій ролі. */}
      <DocumentSheetBanner
        documentId={documentId}
        periodKey={periodKey}
        sheetCode={sheetCode}
        state={state}
        lock={lock}
        canDecide={actions.approve !== null || actions.reject !== null}
      />

      {/* ✎ resultsStale: входи змінилися після прогону - банер і кнопка «Recalculate» (автоперерахунку немає). */}
      {resultsStale === true && (
        <Suspense fallback={null}>
          <StaleResultsBanner since={resultsStaleSince ?? null} recalculate={actions.recalculate} />
        </Suspense>
      )}

      {showImport && (
        <Suspense fallback={null}>
          <ImportPanel documentId={documentId} periodKey={periodKey} withTrigger={false} openRef={openImport} />
        </Suspense>
      )}

      {actions.dialogs}
    </>
  );
}

/**
 * «Почати експорт у формат» для пунктів «More», коли сама кнопка експорту — лінивий чанк (`D-132`).
 *
 * ⚠ До першого запуску `ExportButton` не змонтована (стежити ще нема за чим): пункт меню запам'ятовує
 * формат і монтує її, а щойно кнопка покладе свій запуск у `ref` (ефект `ExportButton`), запам'ятований
 * формат іде в нього — рівно один раз. Далі кнопка лишається змонтованою: задача, «Building…» і тост
 * живуть у ній, як і доти.
 */
function useExportStarter(): {
  readonly mounted: boolean;
  readonly ref: { current: ((format: ExportFormat) => void) | null };
  readonly start: (format: ExportFormat) => void;
} {
  const [mounted, setMounted] = useState(false);
  const [state] = useState(() => {
    let current: ((format: ExportFormat) => void) | null = null;
    let queued: ExportFormat | null = null;

    const ref = {
      get current(): ((format: ExportFormat) => void) | null {
        return current;
      },
      set current(next: ((format: ExportFormat) => void) | null) {
        current = next;
        if (next === null || queued === null) return;

        const format = queued;
        queued = null;
        next(format);
      },
    };

    const enqueue = (format: ExportFormat): boolean => {
      if (current !== null) {
        current(format);
        return true;
      }
      queued = format;
      return false;
    };

    return { ref, enqueue };
  });

  return {
    mounted,
    ref: state.ref,
    start: (format) => {
      if (!state.enqueue(format)) setMounted(true);
    },
  };
}

/**
 * «Подати: <період>» (D-441): кнопка називає період, який подасть, — найраніший неподаний Open/Grace;
 * ▾ поруч дає вибрати інший неподаний відкритий період, а «Наступний — …» попереджає, що за цим чекає ще один.
 *
 * ⚠ Вибір — `Menu`, а не `Select`: меню вже в чанку рядка дій (`D-132`), а комбобокс додав би свій код.
 */
function SubmitAction({
  submit,
  variant,
}: {
  readonly submit: NonNullable<SheetActionsModel['submit']>;
  readonly variant: 'filled' | 'default';
}): JSX.Element {
  // ✎ D-441 «миготіння скелета»: аркуш/період щойно змінився, ціль ще від попереднього (`submit.checking`) —
  // кнопка лишається ТІЄЮ САМОЮ (той самий текст і ширина: «More» не рухається), але недоступна: `aria-disabled`
  // + `data-disabled` (не `disabled`: фокус і наведення лишаються) і підказка «Loading…» в описі; клік гаситься,
  // а `run` моделі й сам нічого не шле. ⚠ `Hint` — завжди (лише `disabled`), а не умовна обгортка: та
  // перемонтовувала б кнопку при кожному перемиканні, і фокус клавіатури падав би на `body` (WCAG 2.4.3).
  // ⚠ `data-period-key` — лише підтверджена ціль: під час перевірки атрибута немає.
  const checking = submit.checking;
  return (
    <>
      <Hint label={t('common.loading')} disabled={!checking}>
        <Button
          variant={variant}
          loading={submit.loading}
          onClick={checking ? (event: MouseEvent<HTMLButtonElement>) => event.preventDefault() : submit.run}
          data-disabled={checking}
          aria-disabled={checking || undefined}
          aria-busy={checking || undefined}
          data-testid="document-submit"
          data-period-key={checking ? undefined : submit.periodKey}
          data-checking={checking || undefined}
        >
          {submitLabel(submit.periodKey)}
        </Button>
      </Hint>
      {/* ⚠ «Наступний — …» буває лише тоді, коли неподаних кілька, — тобто разом із вибором ▾. */}
      {/* ⛔ В одному коміті з кнопкою: рядок дій з'являється одразу в остаточній формі (див. імпорт). */}
      {submit.options.length > 1 && <SubmitPeriodPicker submit={submit} />}
    </>
  );
}
