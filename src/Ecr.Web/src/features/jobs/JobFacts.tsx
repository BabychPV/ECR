import { lazy, Suspense, type JSX } from 'react';
import { Anchor, Button, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { useRestartJob } from './api';

/*
 * Поля фонової задачі, що прийшли з `BE-08`: спроба, причина провалу,
 * кореляція, документ, автор. Один модуль на картку задачі й перелік — дві
 * копії правила «коли показувати спробу» розійшлися б при першій правці.
 */

// ⚠ Лінивий імпорт — з тієї ж причини, що й в `AsyncBoundary`: `CopyButton`
// потрібен лише провалам, і статичний імпорт додав би його кожному рендеру.
const CorrelationCopy = lazy(() => import('@/shared/ui/CorrelationCopy'));

/**
 * Підпис спроби, або `null`, коли показувати нічого.
 *
 * ⛔ Перша спроба (і `null` — задача ще не стартувала) — це норма, не подія:
 * «спроба 1 з 4» на кожному рядку перетворила б сигнал про ретрай на шум.
 * `maxAttempts` = `null` (стан не з журналу, а в переліку поля немає зовсім) —
 * «спроба N» без «з M»: вигадана межа гірша за відсутню.
 */
export function attemptText(
  attempt: number | null | undefined,
  maxAttempts: number | null | undefined,
): string | null {
  if (attempt === null || attempt === undefined || attempt < 2) return null;

  return maxAttempts === null || maxAttempts === undefined
    ? t('jobs.attempt', { n: attempt })
    : t('jobs.attemptOf', { n: attempt, max: maxAttempts });
}

export function JobAttempt({
  attempt,
  maxAttempts,
}: {
  readonly attempt?: number | null | undefined;
  readonly maxAttempts?: number | null | undefined;
}): JSX.Element | null {
  const text = attemptText(attempt, maxAttempts);

  if (text === null) return null;

  return (
    <Text size="xs" c="dimmed" data-job-attempt="">
      {text}
    </Text>
  );
}

/** Ключ каталогу для коду помилки — той самий вигляд, що в `problemText`. */
function errorKey(code: string): string {
  return `err.${code}`;
}

/**
 * Причина провалу: код через каталог і кореляція з кнопкою копіювання.
 *
 * ⚠ `errorCode` = `null` у провалі — не «помилки немає», а «прибирання на
 * старті позначило задачу Failed, причину не записано». Порожнеча тут
 * читалася б як збій екрана.
 */
export function JobFailure({
  state,
  errorCode,
  correlationId,
}: {
  readonly state: string;
  readonly errorCode?: string | null | undefined;
  readonly correlationId?: string | null | undefined;
}): JSX.Element | null {
  if (state !== 'Failed') return null;

  const hasCode = errorCode !== null && errorCode !== undefined && errorCode !== '';
  const hasCorrelation =
    correlationId !== null && correlationId !== undefined && correlationId !== '';

  return (
    <Stack gap="xs" data-job-failure="">
      <Text size="sm" c="statusError">
        {hasCode ? t(errorKey(errorCode)) : t('jobs.failureUnrecorded')}
      </Text>

      {hasCorrelation && (
        <Group gap="xs" wrap="nowrap">
          <Text size="xs" c="dimmed" ff="monospace">
            {correlationId}
          </Text>
          <Suspense fallback={null}>
            <CorrelationCopy value={correlationId} label={t('jobs.copyCorrelation')} />
          </Suspense>
        </Group>
      )}
    </Stack>
  );
}

/**
 * Посилання на документ задачі.
 *
 * ⛔ Адресу будує викликач (шар `pages`): `features` не імпортують `app`, тож
 * реєстр маршрутів сюди не доходить — лише функція id → адреса.
 */
export function JobDocumentLink({
  documentId,
  documentHrefOf,
}: {
  readonly documentId?: number | null | undefined;
  readonly documentHrefOf: (id: number) => string;
}): JSX.Element | null {
  if (documentId === null || documentId === undefined) return null;

  return (
    <Anchor component={Link} to={documentHrefOf(documentId)} size="xs">
      {t('jobs.openDocument', { id: documentId })}
    </Anchor>
  );
}

/** Хто поставив задачу; `null` — системна (розклад, прибирання). */
export function jobAuthor(name: string | null | undefined): string {
  return name === null || name === undefined || name === '' ? t('jobs.system') : name;
}

/**
 * Чи показувати «Повторити» (UX-09, директива №11, T10 #40): лише `Failed`,
 * і лише власнику ВЛАСНОЇ задачі АБО праву `System.ViewHealth`.
 *
 * ⛔ Чиста функція, окремо від компонента, саме заради мутаційного доказу:
 * «чужа задача без ViewHealth» і «не-Failed стан» перевіряються без монтування
 * дерева й без заглушки мережі під `/restart`.
 */
export function canRestartJob(
  state: string,
  isOwnJob: boolean,
  hasViewHealth: boolean,
): boolean {
  return state === 'Failed' && (isOwnJob || hasViewHealth);
}

/**
 * Кнопка «Повторити» для проваленої задачі (UX-09, директива №11, T10 #40).
 *
 * ⛔ Показ не порівнює `createdByUserId` із сеансом — але вже НЕ тому, що
 * поля немає: комітом `a08ac58b` (22.09.2026) `createdByUserId` додано і до
 * `JobStatus`, і до `JobSummary` (сервер прямо в контракті: «видимість та
 * сама, що й `createdByDisplayName` — клієнт вирішує показ „Повторити“»).
 * Порівняння лишили ВИКЛИКАЧУ свідомо, а не через технічну неможливість:
 * обидва наявні місця виклику вже знають власність із власного контексту
 * без порівняння id — у шухляді «My tasks» перелік звужено сервером до
 * `mine=true` (`Q-156`, `MyTasksDrawer.tsx` передає `isOwnJob` буквально
 * `true`), а на `/admin/jobs` видимість і так гейтується правом
 * `System.ViewHealth` (`JobsPage.tsx` передає `isOwnJob` буквально `false`,
 * `hasViewHealth` — `true`). Автоматичне порівняння тут нічого не додало б
 * цим двом викликачам і ризикувало б непомітно змінити поведінку
 * `RecentJobs`, де показ «Повторити» НАВМИСНО не обмежений власністю —
 * дивись коментар біля `isOwnJob={false}` у `JobsPage.tsx`. Сервер
 * (`RestartJobHandler`) усе одно перевіряє власника заново під час запиту —
 * `403` (`err.ECR-AUTH-0403.jobNotYours`), якщо виклик (чи майбутній третій
 * викликач, який передасть прапорець неправильно) помилиться.
 *
 * ⚠ Підтвердження не питається: сервер сам відхилить непровалену задачу
 * `409`-ю (порядок 404 → 403 → 409), і друге запитання «справді повторити?»
 * після кнопки, яка вже каже «повторити», було б зайвим кроком.
 */
export function JobRetry({
  jobId,
  state,
  isOwnJob,
  hasViewHealth,
  onRestarted,
}: {
  readonly jobId: string;
  readonly state: string;
  readonly isOwnJob: boolean;
  readonly hasViewHealth: boolean;
  readonly onRestarted?: () => void;
}): JSX.Element | null {
  const restart = useRestartJob(onRestarted);

  if (!canRestartJob(state, isOwnJob, hasViewHealth)) return null;

  return (
    <Button
      size="xs"
      variant="default"
      loading={restart.isPending}
      onClick={() => restart.mutate(jobId)}
      data-job-retry=""
    >
      {restart.isPending ? t('jobs.restarting') : t('jobs.restart')}
    </Button>
  );
}

/**
 * Посилання на файл результату задачі (UX-09).
 *
 * ⛔ `resultUrl` — уже ГОТОВИЙ відносний шлях API
 * (`GET /api/v1/documents/{id}/export/{exportId}`), а НЕ значення, з якого
 * тут щось збирається з `message` чи інших полів: сервер заповнює його лише
 * для завершеного експорту документа читачеві з `Document.Export`
 * (`JobStatus.ResultUrl`/`JobSummary.ResultUrl`), інакше — `null`. Складати
 * адресу самостійно означало б повторити цю перевірку на клієнті й розійтися
 * з нею при першій же зміні формату відповіді.
 *
 * ⚠ Звичайний `<a href>` (через `Anchor`), а не `Link` react-router і не
 * `fetch`+`blob`: та сама причина, що в `ExportButton` — автентифікація на
 * cookie, і навігація тим самим походженням несе її сама.
 *
 * ✎ `V-10`: текст — `jobs.resultDownload` («Download the file»), а не
 * `document.exportReady` («Download the workbook»). Результатом експорту
 * буває й ZIP-архів CSV, і JSON, а формату тут не видно (є лише готова
 * адреса), тож підпис не має обіцяти книгу Excel.
 */
export function JobResultLink({
  resultUrl,
}: {
  readonly resultUrl?: string | null | undefined;
}): JSX.Element | null {
  if (resultUrl === null || resultUrl === undefined || resultUrl === '') return null;

  return (
    <Anchor href={resultUrl} size="xs" download data-job-result="">
      {t('jobs.resultDownload')}
    </Anchor>
  );
}
