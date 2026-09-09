import type { JSX } from 'react';
import { Alert, Button, Code, Group, Stack, Text } from '@mantine/core';
import { EcrApiError } from '@/api/client';
import { t } from '@/shared/i18n';

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

  return (
    <Alert color="statusError" title={apiError?.problem.title ?? t('state.errorTitle')} role="alert">
      <Stack gap="xs">
        {/* ⚠ Текст СЕРВЕРА, а не власний узагальнений: «не вдалося
            завантажити» не каже нічого, а «період закрито» каже все. */}
        <Text size="sm">{apiError?.message ?? t('state.errorUnknown')}</Text>

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
