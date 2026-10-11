import { useCallback, useRef, useState, type JSX } from 'react';
import { LazyConfirmModal, LazyReasonModal } from '@/features/workflow/lazyDialogs';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { useReturnAssignment, useValidateAssignment } from './api';
import { returnableRecipients } from './ownerActions';
import { ReturnReasonMaxLength, type OwnerAssignmentTarget } from './types';

type Asking = { readonly kind: 'validate' | 'return'; readonly target: OwnerAssignmentTarget };

/**
 * Дії власника над передачею: «Валідувати» (підтвердження) і «Повернути з
 * коментарем» (обов'язковий коментар 1-500 символів).
 *
 * Хук, а не готова кнопка, бо дії викликають із різних місць (шторка, «Мої
 * задачі», матриця): кожне місце дає свій відкривач і малює `dialogs` один раз.
 *
 * ⛔ Фокус після закриття повертається на ВІДКРИВАЧ - елемент, що мав фокус у
 * мить виклику. `returnFocus` Mantine тут не рятує: відкривач часто пункт меню
 * «⋯», який зникає, щойно меню закрилось, і фокус падає на `<body>`. Тому
 * відкривач запам'ятовується явно, а якщо його вже нема в DOM - фокус лишається
 * там, куди його поставив браузер, і НЕ краде його в іншого елемента.
 *
 * ⚠ Діалоги ліниві (`lazyDialogs`): код діалогу не входить у чанк сторінки.
 */
export function useOwnerAssignmentActions(documentId: number): {
  readonly validate: (target: OwnerAssignmentTarget, opener?: HTMLElement | null) => void;
  readonly returnBack: (target: OwnerAssignmentTarget, opener?: HTMLElement | null) => void;
  readonly dialogs: JSX.Element;
} {
  // ⚠ `asking` лишається після закриття (лише `opened` гаситься): анімація
  // закриття мусить домалювати діалог з тим самим заголовком, а не порожнім.
  const [asking, setAsking] = useState<Asking | null>(null);
  const [opened, setOpened] = useState(false);
  const openerRef = useRef<HTMLElement | null>(null);
  const validateMutation = useValidateAssignment(documentId);
  const returnMutation = useReturnAssignment(documentId);

  const open = useCallback((kind: 'validate' | 'return', target: OwnerAssignmentTarget, opener?: HTMLElement | null) => {
    const active = document.activeElement;
    openerRef.current = opener ?? (active instanceof HTMLElement && active !== document.body ? active : null);
    setAsking({ kind, target });
    setOpened(true);
  }, []);

  const close = useCallback(() => {
    const opener = openerRef.current;
    openerRef.current = null;
    setOpened(false);
    // Фокус - у наступному кадрі: Mantine знімає пастку фокуса саме тоді.
    if (opener !== null) setTimeout(() => opener.isConnected && opener.focus(), 0);
  }, []);

  const target = asking?.target;
  const label = target?.sliceLabel ?? '';
  const names = target === undefined ? '' : returnableRecipients(target).map((r) => r.displayName).join(', ');

  const dialogs = (
    <>
      <LazyConfirmModal
        opened={opened && asking?.kind === 'validate'}
        title={t('editAssign.validateTitle', { slice: label })}
        text={t('editAssign.validateText')}
        consequences={[t('editAssign.validateConsequence')]}
        verb={t('editAssign.validate')}
        danger={false}
        isPending={validateMutation.isPending}
        onConfirm={() => {
          if (target === undefined) return;
          validateMutation.mutate(
            { id: target.id, baseVersion: target.version },
            {
              onSuccess: () => {
                close();
                showDone(t('editAssign.validated', { slice: label }));
              },
              onError: showApiError,
            },
          );
        }}
        onClose={close}
      />

      <LazyReasonModal
        opened={opened && asking?.kind === 'return'}
        title={t('editAssign.returnTitle', { slice: label })}
        label={t('editAssign.returnReason')}
        description={t('editAssign.returnHint', { names, max: ReturnReasonMaxLength })}
        confirmLabel={t('editAssign.returnConfirm')}
        isPending={returnMutation.isPending}
        minLength={1}
        maxLength={ReturnReasonMaxLength}
        onConfirm={(reason) => {
          if (target === undefined) return;
          returnMutation.mutate(
            { id: target.id, reason, baseVersion: target.version },
            {
              onSuccess: () => {
                close();
                showDone(t('editAssign.returned', { slice: label }));
              },
              onError: showApiError,
            },
          );
        }}
        onClose={close}
      />
    </>
  );

  return {
    validate: (row, opener) => open('validate', row, opener),
    returnBack: (row, opener) => open('return', row, opener),
    dialogs,
  };
}
