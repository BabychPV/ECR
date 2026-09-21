import { lazy, Suspense, type JSX } from 'react';
import { Anchor, Group, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';

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
