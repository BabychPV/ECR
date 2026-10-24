import { Alert, List, Text } from '@mantine/core';
import type { DiagnosticInfo } from '@/api/types';
import { localizedMessage } from '@/features/expressions/markers';
import { t } from '@/shared/i18n';
import { errorCodeText } from '@/shared/ui/problemText';

// ⚠ Розбір відмови живе окремо (`publishProblemsOf.ts`, бюджет `D-132`): сторінка версії кличе його в
// `onError` публікації, а сам блок нижче вантажить лише тоді, коли є що показати. Реекспорт — щоб
// наявні імпорти не мінялися.
export { publishProblemsOf } from './publishProblemsOf';

/**
 * Текст однієї проблеми мовою інтерфейсу.
 *
 * ⚠ Із ключем — за ключем і підстановками (як в редакторі виразів). Без ключа
 * (типи, одиниці, цикл — `Q-303`) сирий `message` написаний розробником
 * українською, тому першим іде заголовок коду з каталогу, а сирий текст —
 * лише коли каталог цього коду не знає.
 */
export function publishProblemText(problem: DiagnosticInfo): string {
  if (problem.messageKey !== null && problem.messageKey !== undefined) {
    return localizedMessage(problem);
  }

  return errorCodeText(problem.code, problem.message);
}

interface PublishProblemsAlertProps {
  readonly problems: readonly DiagnosticInfo[];
  readonly onClose: () => void;
}

/** Блок «версію не опубліковано» з переліком проблем: код і причина в кожному рядку. */
export function PublishProblemsAlert({ problems, onClose }: PublishProblemsAlertProps) {
  return (
    <Alert
      color="statusError"
      title={t('version.publishProblems', { count: problems.length })}
      withCloseButton
      onClose={onClose}
      role="alert"
    >
      <List spacing="xs" size="sm">
        {problems.map((problem, index) => (
          <List.Item key={`${problem.code}-${String(index)}`}>
            <Text span fw={600}>
              {problem.code}
            </Text>{' '}
            {publishProblemText(problem)}
          </List.Item>
        ))}
      </List>
    </Alert>
  );
}
