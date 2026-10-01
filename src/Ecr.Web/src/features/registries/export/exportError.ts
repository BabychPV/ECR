import { EcrApiError } from '@/api/client';
import { t } from '@/shared/i18n';
import { problemText } from '@/shared/ui/problemText';

/** Відмова експорту словами: назва й підказка, що робити далі. */
export interface RegistryExportErrorText {
  readonly title: string;
  readonly detail: string | null;
  readonly hint: string | null;
}

/** Ключ каталогу відмови «записів більше за стелю» (`ExportRegistryHandler`). */
export const ExportTooLargeMessageKey = 'err.ECR-REQ-0422.registryExportTooLarge';

/**
 * Відмова експорту довідника (RT-16) — текстом для людини.
 *
 * ⛔ `403` — власним реченням, а не `err.http.forbidden`: загальне «доступ заборонено» не каже,
 * ЧОГО бракує. Тут бракує рівно одного — права читання довідника (`Registry.View` або грант
 * `Read`), і саме його треба просити.
 *
 * ⚠ `422` стелі (`Registries:ExportMaxRows`) — текстом сервера: він уже локалізований
 * (`messageKey`) і містить обидва числа. Підказка поруч — що з цим робити, бо обрізаного файлу
 * сервер свідомо не віддає.
 */
export function registryExportErrorText(error: unknown): RegistryExportErrorText {
  const shown = problemText(error);
  const status = error instanceof EcrApiError ? error.problem.status : null;

  if (status === 403) {
    return { title: t('registries.export.failed'), detail: t('registries.export.forbidden'), hint: null };
  }

  const messageKey = error instanceof EcrApiError ? error.problem.extensions2?.['messageKey'] : undefined;
  if (status === 422 && messageKey === ExportTooLargeMessageKey) {
    return {
      title: t('registries.export.failed'),
      detail: shown.detail ?? t('registries.export.tooLarge'),
      hint: t('registries.export.tooLargeHint'),
    };
  }

  return { title: t('registries.export.failed'), detail: shown.detail ?? shown.title, hint: null };
}
