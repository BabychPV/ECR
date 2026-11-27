import { useEffect, useState, type JSX } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import type { DocumentSummary } from '@/api/types';
import { problemText } from '@/shared/ui/problemText';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { BusinessKeyMaxLength, changeDocumentBusinessKey } from './api';
import { BusinessKeyChangeDialog } from './BusinessKeyChangeDialog';

/*
 * ✎ b18 D-132: мутація, поля форми й діалог зміни ключа — окремий чанк (`BusinessKeyChangeAction` лишає в маршруті лише
 * пункт меню й прапорець «відкрито»). Вантажиться з першим відкриттям (на наведенні на пункт — заздалегідь).
 */

/** `422 rekeyKeyInvalid` — під полем ключа, а не банером: людина може одразу виправити. */
function isRekeyKeyInvalid(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-DOC-0422'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-DOC-0422.rekeyKeyInvalid'
  );
}

/** `422 rekeyReasonRequired` — під полем причини. */
function isRekeyReasonRequired(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-DOC-0422'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-DOC-0422.rekeyReasonRequired'
  );
}

/**
 * `409 rekeyStale`: чинний ключ на сервері вже не той, що бачила людина —
 * хтось змінив його першим.
 *
 * ⚠ Єдина відмова, після якої форму НЕ можна просто повторити: значення
 * `expectedBusinessKey`, з яким пішов запит, застаріле, а не невалідне.
 */
function isRekeyStale(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-DOC-0409'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-DOC-0409.rekeyStale'
  );
}

export interface BusinessKeyChangeHostProps {
  readonly documentId: number;
  readonly document: DocumentSummary;
  /** Період — частина ключа кешу документа (R-A6), потрібен для інвалідизації. */
  readonly periodKey: number;
  readonly opened: boolean;
  readonly onClose: () => void;
  /** Відмова `rekeyStale` (або `null`) — банер рисує власник, ПІСЛЯ закриття діалогу. */
  readonly onRefusal: (error: unknown) => void;
}

export default function BusinessKeyChangeHost({
  documentId,
  document,
  periodKey,
  opened,
  onClose,
  onRefusal,
}: BusinessKeyChangeHostProps): JSX.Element {
  const queryClient = useQueryClient();
  const [newKey, setNewKey] = useState('');
  const [reason, setReason] = useState('');

  const change = useMutation({
    mutationFn: () =>
      changeDocumentBusinessKey({
        documentId,
        businessKey: newKey.trim(),
        expectedBusinessKey: document.businessKey,
        reason: reason.trim(),
      }),
    onSuccess: async () => {
      onClose();

      // ⚠ Перелік документів теж показує бізнес-ключ (`DocumentsPage`) —
      // спільний префікс `['documents']` оновлює і його, і зведення над ним
      // (той самий прийом, що `useDocumentListSummary`).
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] }),
        queryClient.invalidateQueries({ queryKey: ['documents'] }),
      ]);

      showDone(t('documents.keyChanged'));
    },
    onError: async (error) => {
      // ⚠ ЛИШЕ `rekeyStale` закриває діалог і перечитує документ: чинний
      // ключ на сервері вже не той, з яким пішов запит, — надсилати ту саму
      // форму ще раз нема чим, доки людина не побачить новий ключ.
      if (isRekeyStale(error)) {
        onClose();
        await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
      }
    },
  });

  const { reset: resetChange } = change;

  // ⚠ Поля очищаються при КОЖНОМУ відкритті — той самий прийом, що
  // `ReasonModal`/`TestDataSourceModal`: причина чи ключ, що лишилися з
  // попередньої спроби, тут особливо небезпечні, бо йдуть в аудит зміни
  // бізнес-ключа.
  useEffect(() => {
    if (opened) {
      setNewKey('');
      setReason('');
      resetChange();
    }
  }, [opened, resetChange]);

  const stale = isRekeyStale(change.error) ? change.error : null;
  useEffect(() => {
    onRefusal(stale);
  }, [stale, onRefusal]);

  const currentKey = document.businessKey;
  const trimmedKey = newKey.trim();
  const trimmedReason = reason.trim();

  const keyValid =
    trimmedKey.length > 0 && trimmedKey.length <= BusinessKeyMaxLength && trimmedKey !== currentKey;
  const reasonValid = trimmedReason.length > 0;
  const canSubmit = keyValid && reasonValid && !change.isPending;

  const shownProblem = change.error !== null ? problemText(change.error) : null;

  const keyServerError = isRekeyKeyInvalid(change.error)
    ? (shownProblem?.detail ?? shownProblem?.title ?? null)
    : null;

  const reasonServerError = isRekeyReasonRequired(change.error)
    ? (shownProblem?.detail ?? shownProblem?.title ?? null)
    : null;

  // Решта відмов (duplicate/locked, або щось геть інше — 403/404/500):
  // банер під полями, форма лишається на екрані.
  const dialogBannerError =
    change.error !== null && !isRekeyStale(change.error) && keyServerError === null && reasonServerError === null
      ? change.error
      : null;

  return (
    <BusinessKeyChangeDialog
      opened={opened}
      onClose={onClose}
      newKey={newKey}
      onNewKeyChange={setNewKey}
      reason={reason}
      onReasonChange={setReason}
      keyError={keyServerError}
      reasonError={reasonServerError}
      bannerError={dialogBannerError}
      canSubmit={canSubmit}
      isPending={change.isPending}
      onConfirm={() => change.mutate()}
    />
  );
}
