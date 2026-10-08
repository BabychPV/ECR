import { lazy, Suspense, useRef, type JSX, type ReactNode } from 'react';
import { Button, Menu, Text } from '@mantine/core';
import { ExportButton, exportFormatOptions, type ExportFormat } from '@/features/export/ExportButton';
import { RecalculateKbd, useSheetActions } from '@/features/workflow/SheetActions';
import type { DocumentLock } from './documentLock';
import { DocumentSheetBanner } from './DocumentSheetBanner';
import { DocumentToolbar } from './DocumentToolbar';
import { StaleResultsBanner } from './StaleResultsBanner';
import { useStaleResultsReminder } from './useStaleResultsReminder';
import { t } from '@/shared/i18n';

// ⚠ `import()` — той самий чанк, що й раніше в `DocumentPage` (`D-132`): імпорт
// не потрібен у мить першого малюнка сторінки.
const ImportPanel = lazy(async () => ({ default: (await import('@/features/import/ImportPanel')).ImportPanel }));

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
  canExport,
  calculationsStale = false,
  validate,
  documentItems,
  status,
  resultsStale,
  resultsStaleSince,
}: DocumentActionBarProps): JSX.Element {
  const actions = useSheetActions({ documentId, sheetDefId, sheetName, periodKey, state, lock });
  useStaleResultsReminder(documentId, resultsStale === true, actions.recalculate !== null);
  const openImport = useRef<((returnTo?: HTMLElement | null) => void) | null>(null);
  const startExport = useRef<((format: ExportFormat) => void) | null>(null);

  const showImport = canImport && !readOnly;

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
            <Menu.Item key={`export-${option.value}`} onClick={() => startExport.current?.(option.value)}>
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
          {t('workflow.recalculateHint')}
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

  // ⛔ Одна головна дія (`L1`): подання або затвердження — ніколи обидва
  // (подати можна лише чернетку, затвердити — лише подане).
  const primary =
    actions.submit !== null ? (
      // ⚠ `variant="filled"` явно: за ним екранний тест рахує головні дії (`L1`).
      <Button variant="filled" loading={actions.submit.loading} onClick={actions.submit.run}>
        {t('document.submit')}
      </Button>
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
            {canExport && (
              <ExportButton
                documentId={documentId}
                periodKey={periodKey}
                language={language}
                withTrigger={false}
                startRef={startExport}
              />
            )}
          </>
        }
        more={items}
        primary={primary}
      >
        {validateVisible && validateButton}

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
        <StaleResultsBanner since={resultsStaleSince ?? null} recalculate={actions.recalculate} />
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
