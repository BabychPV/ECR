import type { JSX } from 'react';
import { Alert, Button, Code, Group, Stack, Text } from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { logSuppressedDetail, problemText } from './problemText';
import { t } from '@/shared/i18n';

/**
 * Технічна подробиця — згорнута, під розгортанням (`X-04`, `X-27`).
 *
 * ⛔ Не на екрані поруч із поясненням. Сирий текст (`ex.Message` задачі,
 * `TypeError: …` рендера) — не для людини: він чужою мовою або мовою СУБД.
 * Але й не зникає: адміністратор, який розбирає збій, розгортає його і
 * копіює в звернення.
 *
 * ⚠ Рідний `<details>`: розгортання працює з клавіатури й читалкою без
 * жодного стану React, і його зміст не заважає, доки згорнутий.
 */
export function TechnicalDetails({
  label,
  children,
}: {
  label: string;
  children: string;
}): JSX.Element {
  return (
    <details data-technical-details="">
      <Text component="summary" size="xs" c="dimmed" style={{ cursor: 'pointer' }}>
        {label}
      </Text>
      <Code block mt="xs" style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
        {children}
      </Code>
    </details>
  );
}

/**
 * Показ помилки — **єдине місце** на весь застосунок.
 *
 * ⛔ «Щось пішло не так» заборонено (`07-checkpoints`, Етап 6). Користувач
 * бачить **текст сервера**, стабільний код і ідентифікатор кореляції: перше
 * пояснює, що сталося, друге дає підтримці однозначну назву проблеми, третє —
 * єдиний спосіб знайти цей самий запит у серверному журналі.
 *
 * ⚠ Компонент один і той самий для форм і для `<AsyncBoundary>`. Дві різні
 * подачі помилки означали б, що на одному екрані код показується, а на іншому
 * ні — і користувач не знав би, чого чекати. Раніше так і було: обгортка мала
 * власний варіант, а форми — цей.
 *
 * ⚠ Заголовок береться з каталогу, а не з коду: тут стояв літерал «Помилка»
 * українською — мовою, якої в системі немає взагалі (`D-95`: en, ru, kz).
 */
export function ErrorAlert({
  error,
  onRetry,
}: {
  error: unknown;
  onRetry?: (() => void) | undefined;
}): JSX.Element | null {
  if (error === null || error === undefined) return null;

  const apiError = error instanceof EcrApiError ? error : null;
  const shown = problemText(error);

  logSuppressedDetail(shown);

  return (
    <Alert color="statusError" title={shown.title} role="alert">
      <Stack gap="xs">
        {/*
          ⚠ Текст СЕРВЕРА, а не власний узагальнений: «не вдалося завантажити»
          не каже нічого, а «період закрито» каже все.

          ✎ 2026-09-20, рішення людини: «українську прибрати — має бути
          залежно від обраної мови». Тут стояв `apiError?.message`, тобто
          `detail ?? title` БЕЗ розбору, якою мовою той `detail` написаний.

          ⛔ Тепер подробиця показується ЛИШЕ тоді, коли сервер позначив її
          `messageKey` — тобто зібрав із каталогу мовою користувача. Інакше
          рядка немає ЗОВСІМ (`D15-06`: елемент, для якого немає даних, не
          малюється). Заглушка («подробиць немає») зайняла б місце й нічого
          не сказала, а назва проблеми в заголовку вже є — і теж із каталогу.
        */}
        {shown.detail !== null && <Text size="sm">{shown.detail}</Text>}

        {apiError !== null && (
          // ⚠ Код і кореляція показуються ЗАВЖДИ: з ними звернення в підтримку
          // займає хвилину, без них — листування. Ідентифікатор генерує клієнт
          // і надсилає заголовком, і це єдине, що зшиває скаргу з логом.
          <Text size="xs">
            <Code>{apiError.problem.errorCode}</Code> <Code>{apiError.problem.correlationId}</Code>
          </Text>
        )}

        {/* ⛔ Тупикових екранів не буває (`ФВ-14.24`). Без дії «повторити»
            користувач має єдиний доступний хід — перезавантажити сторінку, і
            саме так він і зробить. */}
        {onRetry !== undefined && (
          <Group gap="xs">
            <Button size="xs" variant="default" onClick={onRetry}>
              {t('common.retry')}
            </Button>
          </Group>
        )}
      </Stack>
    </Alert>
  );
}
