import { useState, type JSX } from 'react';
import { Button } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import type { DocumentSummary } from '@/api/types';
import { deleteDocument } from './api';
import { localized } from '@/shared/i18n/localized';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Право, під яким сервер приймає `DELETE /api/v1/documents/{id}`. */
export const DeleteDocumentPermission = 'Document.Delete';

/**
 * Чи документ, НАСКІЛЬКИ ЙОГО ЗНАЄ КЛІЄНТ, — чернетка.
 *
 * ⚠ «Наскільки знає» — не застереження заради застереження. Сервер вважає
 * чернеткою документ, у якого ВСІ аркуші в `Draft` **і** немає жодного сліду
 * погодження (`DraftDocumentDeletion.EnsureDraft`). Другої половини клієнт не
 * бачить: відкликаний аркуш знову `Draft`, а історія лишилася. Тому `true`
 * тут означає «кнопку показати», а не «видалення пройде» — остаточну відповідь
 * дає сервер, і його відмову показуємо з причиною.
 *
 * ⛔ Аркуш, якого немає в `sheetStates`, — `Draft`: так само його читає й
 * сторінка документа (`?? 'Draft'`). Інакше документ, для якого сервер ще не
 * завів рядок стану, втратив би кнопку без жодної причини.
 */
export function isKnownDraft(
  sheetStates: Readonly<Record<string, string>>,
  sheetCodes: readonly string[],
): boolean {
  return (
    sheetCodes.every((code) => (sheetStates[code] ?? 'Draft') === 'Draft') &&
    Object.values(sheetStates).every((state) => state === 'Draft')
  );
}

export interface DeleteDocumentActionArgs {
  readonly documentId: number;

  /** `undefined` — документ ще не приїхав: кнопки немає, бо назви немає. */
  readonly document: DocumentSummary | undefined;

  /** Коди аркушів документа за обраний період (з переліку таблиць). */
  readonly sheetCodes: readonly string[];

  /** Чи має сесія право `Document.Delete`. */
  readonly allowed: boolean;
}

export interface DeleteDocumentAction {
  /** Кнопка разом із підтвердженням — для шапки; `null`, якщо дії немає. */
  readonly trigger: JSX.Element | null;

  /** Банер відмови сервера — під шапкою; `null`, якщо відмови не було. */
  readonly refusal: JSX.Element | null;
}

/**
 * Дія «Видалити документ-чернетку» на сторінці документа.
 *
 * ⚠ Хук повертає ДВА вузли, а не один компонент, і це рішення про місце, не
 * про стиль. Кнопка живе в шапці (`PageHeader.actions` — ряд кнопок), а
 * відмова — банер `ErrorAlert` із причиною, кодом і кореляцією, якому в ряду
 * кнопок не місце. Покласти банер у `ConfirmModal` не можна: його `text`
 * загорнутий у `<Text>` (`<p>`), а `Alert` — це `<div>`, тобто невалідна
 * вкладеність. Діалог після відмови закривається, банер лишається на сторінці,
 * доки людина не відкриє діалог знову.
 *
 * ⛔ Відмова показується ПРИЧИНОЮ сервера, а не «не вдалося»: `409
 * ECR-DOC-0409` каже, ЧОМУ документ не чернетка (`deleteNotDraft` з аркушем і
 * станом або `deleteHasHistory`), `403` — що немає гранта на запис у проєкт.
 * Текст приходить уже локалізованим (`messageKey`), його розбирає
 * `ErrorAlert` → `problemText` — єдине місце на весь застосунок.
 */
export function useDeleteDocumentAction({
  documentId,
  document,
  sheetCodes,
  allowed,
}: DeleteDocumentActionArgs): DeleteDocumentAction {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [opened, setOpened] = useState(false);

  const name =
    document === undefined
      ? ''
      : localized(document.nameL10n).length > 0
        ? `${localized(document.nameL10n)} · ${document.businessKey}`
        : document.businessKey;

  const remove = useMutation({
    mutationFn: () => deleteDocument(documentId),
    onSuccess: async () => {
      setOpened(false);

      // ⚠ Спершу перелік, потім перехід: людина має прийти на перелік, де
      // видаленого документа вже немає, а не побачити його там ще раз.
      // Ключ `['documents']` — префікс і переліку, і зведення над ним.
      await queryClient.invalidateQueries({ queryKey: ['documents'] });

      showDone(t('documents.deleted', { name }));
      await navigate('/');

      // ⚠ Після переходу: доки сторінка змонтована, прибраний кеш вона
      // перезапитала б — і отримала б `404` на документ, якого вже немає.
      queryClient.removeQueries({ queryKey: ['document', documentId] });
      queryClient.removeQueries({ queryKey: ['document-tables', documentId] });
    },
    onError: async () => {
      setOpened(false);

      // ⚠ Відмова могла статися тому, що стан документа змінився за спиною
      // (аркуш подали з іншої вкладки). Перечитаний стан сховає кнопку, якщо
      // документ уже не чернетка, — банер із причиною лишається.
      await queryClient.invalidateQueries({ queryKey: ['document', documentId] });
    },
  });

  const shown =
    allowed && document !== undefined && isKnownDraft(document.sheetStates, sheetCodes);

  const trigger = shown ? (
    <>
      <Button
        size="xs"
        variant="outline"
        color="statusError"
        onClick={() => {
          remove.reset();
          setOpened(true);
        }}
      >
        {t('documents.delete')}
      </Button>

      <ConfirmModal
        opened={opened}
        title={t('documents.deleteTitle', { name })}
        text={t('documents.deleteText')}
        consequences={[{ text: t('documents.deleteNote'), note: true }]}
        verb={t('documents.delete')}
        danger
        isPending={remove.isPending}
        onConfirm={() => remove.mutate()}
        onClose={() => setOpened(false)}
      />
    </>
  ) : null;

  // ⚠ Без `onRetry`: «повторити» тут означало б повторне видалення БЕЗ
  // підтвердження. Банер знімає наступне відкриття діалогу (`remove.reset()`).
  const refusal = remove.error === null ? null : <ErrorAlert error={remove.error} />;

  return { trigger, refusal };
}
