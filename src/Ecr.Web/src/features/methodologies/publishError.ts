import { createElement } from 'react';
import { notifications } from '@mantine/notifications';
import { EcrApiError } from '@/api/client';
import { t } from '@/shared/i18n';
import { notificationCloseButtonProps, showApiError } from '@/shared/ui/notify';
import { logSuppressedDetail, problemText } from '@/shared/ui/problemText';

/**
 * Ключі пунктів переліку проблем публікації (`publish.problem.*`) — рівно ті,
 * що породжує сервер (`MethodologyPublishChecks`, `PublishMethodologyHandler`).
 *
 * ⚠ Перелік закритий навмисно: невідомий ключ (сервер новіший за клієнта) не
 * показується сирим ключем `⟦…⟧`, а просто випадає з переліку — назва відмови
 * й лічба («N problems») однаково лишаються в тості.
 */
export const PublishProblemKeys = [
  'publish.problem.constantNotNumber',
  'publish.problem.constantNoText',
  'publish.problem.categoryLabelInExpression',
  'publish.problem.textConstantInArithmetic',
  'publish.problem.numberReturnsText',
  'publish.problem.textReturnsNumber',
  'publish.problem.textOutput',
  'publish.problem.importNoVersion',
  'publish.problem.libraryHasRules',
  'publish.problem.ambiguousReference',
] as const;

type PublishProblemKey = (typeof PublishProblemKeys)[number];

/** Один пункт переліку так, як його віддає сервер у полі `problems`. */
interface PublishProblemItem {
  readonly messageKey: string;
  readonly args?: Record<string, string>;
}

function isKnownKey(key: string): key is PublishProblemKey {
  return (PublishProblemKeys as readonly string[]).includes(key);
}

function itemsOf(error: unknown): PublishProblemItem[] {
  if (!(error instanceof EcrApiError)) {
    return [];
  }

  // ⚠ Розширення приходять і в корені problem+json, і в `extensions2`
  // (`ExceptionHandlingMiddleware`): беремо те, що є.
  const raw =
    error.problem.extensions2?.['problems'] ?? (error.problem as unknown as Record<string, unknown>)['problems'];

  return Array.isArray(raw) ? (raw as PublishProblemItem[]) : [];
}

/**
 * Перекладені пункти переліку проблем публікації (F-15/B-12, четвертий раунд UX).
 *
 * ⛔ Доти перелік їхав одним українським реченням у `detail`, який клієнт
 * мовою інтерфейсу показувати не може, — і людина бачила лише «The version
 * failed pre-publication checks ({count} problems)» без жодної назви формули.
 */
export function publishProblemLines(error: unknown): string[] {
  const lines: string[] = [];

  for (const problem of itemsOf(error)) {
    const key = problem.messageKey;
    if (isKnownKey(key)) {
      lines.push(t(key, problem.args ?? {}));
    }
  }

  return lines;
}

/**
 * Показує відмову публікації: назву/подробицю — як `showApiError`, а під нею
 * перелік конкретних проблем, коли сервер його прислав.
 */
export function showPublishError(error: unknown): void {
  const lines = publishProblemLines(error);

  if (lines.length === 0) {
    showApiError(error);
    return;
  }

  const shown = problemText(error);
  logSuppressedDetail(shown);

  notifications.show({
    color: 'statusError',
    autoClose: false,
    title: shown.detail ?? shown.title,
    message: createElement(
      'div',
      null,
      createElement('div', null, t('publish.problemsTitle')),
      createElement(
        'ul',
        { style: { margin: 0, paddingInlineStart: '1.25rem' } },
        lines.map((line, index) => createElement('li', { key: index }, line)),
      ),
    ),
    closeButtonProps: notificationCloseButtonProps,
  });
}
