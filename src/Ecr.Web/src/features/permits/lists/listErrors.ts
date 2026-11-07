import { EcrApiError } from '@/api/client';
import { t } from '@/shared/i18n';
import { showApiError, showErrorText } from '@/shared/ui/notify';

/** Відмова `PUT /permit-lists/{key}/items` «таке значення в списку вже є» (`ChangePermitListItemsHandler.RequireUnique*`). */
const DuplicateValueKey = 'err.ECR-PRM-0422.choicesInvalid';

/**
 * AU-13 N3: повтор значення списку — людський текст із назвою списку і повтореним значенням, а не
 * «The values of column "shared:LAND_REGION"…» (сервер у `columnCode` віддає внутрішній ключ списку).
 * Решта відмов — як завжди, `showApiError`. Тост один: сама зміна позначена `meta.handled` (`useChangeListItems`).
 */
export function showListChangeError(error: unknown, list: string, value: string): void {
  if (
    error instanceof EcrApiError &&
    error.problem.errorCode === 'ECR-PRM-0422' &&
    error.problem.extensions2?.['messageKey'] === DuplicateValueKey &&
    value.trim() !== ''
  ) {
    showErrorText(t('permits.lists.duplicateValue', { list, value: value.trim() }));

    return;
  }
  showApiError(error);
}
