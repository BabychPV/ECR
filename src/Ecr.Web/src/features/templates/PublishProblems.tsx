import { Alert, List, Text } from '@mantine/core';
import { EcrApiError } from '@/api/client';
import type { DiagnosticInfo } from '@/api/types';
import { localizedMessage } from '@/features/expressions/markers';
import { t } from '@/shared/i18n';
import { errorCodeText } from '@/shared/ui/problemText';

/**
 * Перелік проблем, через які сервер відмовив у публікації версії шаблону.
 *
 * ⛔ A2-01: відмова публікації несе ПЕРЕЛІК у `details.diagnostics` (на проводі —
 * плоским полем `diagnostics`), але тост показував лише головне речення:
 * «… the first is ECR-TMPL-4224 at position 0». Автор шаблону не бачив ні
 * правил, ні таблиці, ні жодної з решти проблем — а публікація відмовляє з УСІМА
 * одразу, щоб не виправляти їх по одній.
 */
export function publishProblemsOf(error: unknown): DiagnosticInfo[] {
  if (!(error instanceof EcrApiError)) return [];

  const list = error.problem.extensions2?.['diagnostics'];

  if (!Array.isArray(list)) return [];

  return list.filter(
    (item): item is DiagnosticInfo =>
      typeof item === 'object' && item !== null && typeof (item as { code?: unknown }).code === 'string',
  );
}

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
