import { useMutation, useQueryClient, type UseMutationResult } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import {
  deletePermitType,
  openPermitTypeVersion,
  publishPermitType,
} from '@/features/permits/api/typesApi';
import type { PermitType } from '@/features/permits/api/types';
import { t } from '@/shared/i18n';
import { showApiError, showDone } from '@/shared/ui/notify';
import { pickName } from './names';

/**
 * Дії життєвого циклу типу дозволу: публікація, нова версія, видалення (Permits, PM-08c).
 *
 * ⚠ Ключі мутацій — `['permits','types',code,…]`; після успіху скидається ВЕСЬ домен
 * `['permits','types']`: перелік типів і картка типу не мають лишитися зі старою версією.
 * ⛔ Відмови сервера показує `showApiError` — деталь береться з каталогу за `messageKey` мовою
 * користувача (`ECR-PRM-0422` з окремим текстом на кожен випадок, `4094` «опублікований/є дозволи»).
 * `ECR-PRM-4093` — токен застарів (хтось змінив чи опублікував тип): окрім тоста хост отримує
 * `onConflict`, щоб перечитати визначення; а не повторити з тим самим токеном.
 */
export interface TypeLifecycleHandlers {
  readonly onPublished: (next: PermitType) => void;
  readonly onVersionOpened: (next: PermitType) => void;
  readonly onDeleted: () => void;

  /** `409 ECR-PRM-4093`: визначення змінили — перечитати. */
  readonly onConflict: () => void;
}

/** Код відмови «токен чернетки чужий / немає чернетки / чернетка вже є». */
export const PermitTypeConflictCode = 'ECR-PRM-4093';

export interface PublishVariables {
  readonly reason: string;
  readonly rowVersion: string;
}

export interface TypeLifecycle {
  readonly publish: UseMutationResult<PermitType, Error, PublishVariables>;
  readonly openVersion: UseMutationResult<PermitType, Error, void>;
  readonly remove: UseMutationResult<void, Error, void>;
}

export function useTypeLifecycle(type: PermitType, handlers: TypeLifecycleHandlers): TypeLifecycle {
  const queryClient = useQueryClient();
  const code = type.code;
  const name = pickName(type.nameL10n);

  function invalidate(): void {
    void queryClient.invalidateQueries({ queryKey: ['permits', 'types'] });
  }

  function fail(error: unknown): void {
    showApiError(error);
    if (error instanceof EcrApiError && error.problem.errorCode === PermitTypeConflictCode) {
      handlers.onConflict();
    }
  }

  const publish = useMutation<PermitType, Error, PublishVariables>({
    mutationKey: ['permits', 'types', code, 'publish'],
    mutationFn: (variables) => publishPermitType(code, variables),
    onSuccess: (next) => {
      invalidate();
      showDone(t('permits.editor.dialogs.done.published', { name, version: next.version }));
      handlers.onPublished(next);
    },
    onError: fail,
  });

  const openVersion = useMutation<PermitType, Error, void>({
    mutationKey: ['permits', 'types', code, 'new-version'],
    mutationFn: () => openPermitTypeVersion(code),
    onSuccess: (next) => {
      invalidate();
      showDone(
        t('permits.editor.dialogs.done.newVersion', { name, version: next.draftVersion ?? next.version }),
      );
      handlers.onVersionOpened(next);
    },
    onError: fail,
  });

  const remove = useMutation<void, Error, void>({
    mutationKey: ['permits', 'types', code, 'delete'],
    mutationFn: () => deletePermitType(code),
    onSuccess: () => {
      invalidate();
      showDone(t('permits.editor.dialogs.done.deleted', { name }));
      handlers.onDeleted();
    },
    onError: fail,
  });

  return { publish, openVersion, remove };
}
