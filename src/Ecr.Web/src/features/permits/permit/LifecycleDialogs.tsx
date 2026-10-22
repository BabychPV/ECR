import { useState, type JSX } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { formatDate } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { activatePermit, deletePermit, endPermit, renewPermit } from '../api/permitsApi';
import type { Permit } from '../api/types';
import { EndEarlyModal } from '../lib/EndEarlyModal';
import { endDateOutOfRangeText, sentenceDate } from '../lib/lifecycleText';
import { PermitsPaths } from '../shell/permitPaths';
import { type LifecycleDialogId } from './lifecycleDialogIds';
import { focusHeadingAfterNavigation, pageHeading } from './focusHeadingAfterNavigation';
import { permitKeys } from './usePermitData';

/** Мінімальна довжина причини дострокового завершення: дзеркало сервера (`EndPermitRequest.reason`). */
export const MinEndReasonLength = 10;

export interface LifecycleDialogsProps {
  readonly permit: Permit;
  readonly dialog: LifecycleDialogId;
  readonly onClose: () => void;
}

/**
 * Activate / End early / Renew / Delete draft (мокап `activate-permit`, `end-permit`, `renew-permit`).
 *
 * Це ВЛАСНІ діалоги екрана дозволу: список дозволів має свої (`features/permits/list/`). Відмови сервера
 * (`4092` перетин чинності, `4095` уже поновлено, `4096` не той стан) показує `showApiError` із каталогу; після
 * `4096` дозвіл перечитується, і кнопки відповідають справжньому стану.
 */
export function LifecycleDialogs({ permit, dialog, onClose }: LifecycleDialogsProps): JSX.Element {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [endError, setEndError] = useState<string | null>(null);

  function refresh(next?: Permit): void {
    if (next !== undefined) queryClient.setQueryData(permitKeys.item(permit.id), next);
    void queryClient.invalidateQueries({ queryKey: permitKeys.item(permit.id) });
    void queryClient.invalidateQueries({ queryKey: ['permits', 'list'] });
  }

  function fail(error: unknown): void {
    showApiError(error);
    refresh();
    onClose();
  }

  const activate = useMutation<Permit, Error, void>({
    mutationKey: ['permits', 'item', permit.id, 'activate'],
    mutationFn: () => activatePermit(permit.id),
    onSuccess: (next) => {
      refresh(next);
      showDone(t('permits.page.done.activated', { number: permit.number }));
      onClose();
    },
    onError: fail,
  });

  const end = useMutation<Permit, Error, string>({
    mutationKey: ['permits', 'item', permit.id, 'end'],
    mutationFn: (reason) => endPermit(permit.id, { endDate: null, reason }),
    onSuccess: (next) => {
      refresh(next);
      showDone(t('permits.page.done.ended', { number: permit.number }));
      onClose();
    },
    // PUI-5: дата завершення поза вікном (дозвіл почався сьогодні) - причина показується в діалозі, він лишається відкритим.
    onError: (error) => {
      const text = endDateOutOfRangeText(error);
      if (text === null) fail(error);
      else setEndError(text);
    },
  });

  const renew = useMutation<Permit, Error, void>({
    mutationKey: ['permits', 'item', permit.id, 'renew'],
    mutationFn: () => renewPermit(permit.id),
    onSuccess: (next) => {
      refresh();
      showDone(t('permits.page.done.renewed'));
      const previous = pageHeading();
      onClose();
      void navigate(`${PermitsPaths.permits}/${String(next.id)}`);
      focusHeadingAfterNavigation({ previous, expectText: next.number });
    },
    onError: fail,
  });

  const remove = useMutation<void, Error, void>({
    mutationKey: ['permits', 'item', permit.id, 'delete'],
    mutationFn: () => deletePermit(permit.id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['permits', 'list'] });
      showDone(t('permits.page.done.deleted', { number: permit.number }));
      const previous = pageHeading();
      onClose();
      void navigate(PermitsPaths.permits);
      focusHeadingAfterNavigation({ previous });
    },
    onError: fail,
  });

  const from = formatDate(permit.validFrom ?? '');
  const to = formatDate(permit.validTo ?? '');
  // PUI-10: `{to}` стоїть наприкінці речення «…по {to}.» - крапка скорочення «г.» не має подвоюватись.
  const toEnd = sentenceDate(to);

  // P3-F2: «End this permit early…» і «Delete draft…» відкриваються пунктами меню «More»; пункт зникає разом із випадним
  // списком, тож після Esc фокус повертається на кнопку «More» (її позначає `data-permit-more` на сторінці дозволу).
  const moreButton = (): HTMLElement | null => document.querySelector<HTMLElement>('[data-permit-more]');

  switch (dialog) {
    case 'activate':
      return (
        <ConfirmModal
          opened
          danger={false}
          title={t('permits.page.activate.title', { number: permit.number })}
          consequences={[
            t('permits.page.activate.reads', { type: permit.typeCode, from, to: toEnd }),
            { text: t('permits.page.activate.edits'), note: true },
            { text: t('permits.page.activate.overlap'), note: true },
          ]}
          verb={t('permits.page.activate.verb')}
          isPending={activate.isPending}
          onConfirm={() => activate.mutate()}
          returnFocusTo={moreButton}
          onClose={onClose}
        />
      );
    case 'end':
      return (
        <EndEarlyModal
          opened
          title={t('permits.page.end.title', { number: permit.number })}
          label={t('permits.page.end.reason')}
          description={t('permits.page.end.text')}
          confirmLabel={t('permits.page.end.verb')}
          minLength={MinEndReasonLength}
          isPending={end.isPending}
          error={endError}
          onEdit={() => setEndError(null)}
          onConfirm={(reason) => {
            // Спінер відкладено (usePendingLoading), кнопка ще клікабельна: другий клік не шле мутацію вдруге.
            if (end.isPending) return;
            setEndError(null);
            end.mutate(reason);
          }}
          returnFocusTo={moreButton}
          onClose={() => {
            setEndError(null);
            onClose();
          }}
        />
      );
    case 'renew':
      return (
        <ConfirmModal
          opened
          danger={false}
          title={t('permits.page.renew.title', { number: permit.number })}
          consequences={[
            t('permits.page.renew.creates', { date: to }),
            { text: t('permits.page.renew.kept'), note: true },
          ]}
          verb={t('permits.page.renew.verb')}
          isPending={renew.isPending}
          onConfirm={() => renew.mutate()}
          returnFocusTo={moreButton}
          onClose={onClose}
        />
      );
    case 'delete':
      return (
        <ConfirmModal
          opened
          title={t('permits.page.delete.title', { number: permit.number })}
          consequences={[t('permits.page.delete.rows'), { text: t('permits.page.delete.inUse'), note: true }]}
          verb={t('permits.page.delete.verb')}
          isPending={remove.isPending}
          onConfirm={() => remove.mutate()}
          returnFocusTo={moreButton}
          onClose={onClose}
        />
      );
  }
}
