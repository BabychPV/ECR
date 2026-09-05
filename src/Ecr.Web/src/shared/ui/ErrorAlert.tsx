import type { JSX } from 'react';
import { Alert, Code, Text } from '@mantine/core';
import { EcrApiError } from '@/api/client';

/**
 * Показ помилки API.
 *
 * ⛔ «Щось пішло не так» заборонено (`07-checkpoints`, Етап 6). Користувач
 * бачить **текст сервера**, стабільний код і ідентифікатор кореляції: перше
 * пояснює, що сталося, друге дає підтримці однозначну назву проблеми, третє —
 * єдиний спосіб знайти цей самий запит у серверному журналі.
 */
export function ErrorAlert({ error }: { error: unknown }): JSX.Element | null {
  if (error === null || error === undefined) return null;

  if (!(error instanceof EcrApiError)) {
    return (
      <Alert color="red" title="Помилка">
        <Text size="sm">{error instanceof Error ? error.message : String(error)}</Text>
      </Alert>
    );
  }

  return (
    <Alert color="red" title={error.problem.title}>
      <Text size="sm">{error.problem.detail ?? error.problem.title}</Text>
      <Text size="xs" mt="xs">
        <Code>{error.problem.errorCode}</Code> · <Code>{error.problem.correlationId}</Code>
      </Text>
    </Alert>
  );
}
