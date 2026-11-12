import { useRef, useState, type JSX } from 'react';
import { Button, Group, Modal, Stack } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { LazyConfirmModal } from '@/features/workflow/lazyDialogs';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { hasAnyText, LocalizedInput, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { createSheetCopy, deleteSheetCopy, renameSheetGroup } from './sheetCopyApi';
import { sheetTabKey } from './sheetCopyTypes';

/** Аркуш (вкладка), для якого показуються дії копій. */
export interface SheetCopyTarget {
  readonly sheetDefId: number;
  readonly sheetCode: string;
  /** Підпис вкладки («Назва 2»). */
  readonly label: string;
  /** Назва групи без номера, мовами каталогу — початкове значення діалогу перейменування. */
  readonly groupNameValues: LocalizedValue;
  /** `null` — оригінал: видалити його не можна. */
  readonly sheetCopyId: number | null;
  readonly isCopyable: boolean;
  /** TC02: живих повідомлень чату копії — видаляються разом із нею; `null`/0 — нема чого попереджати. */
  readonly chatMessageCount?: number | null;
}

export interface SheetCopyBarProps {
  readonly documentId: number;
  readonly target: SheetCopyTarget;
  /** Дозволено додавати копію (аркуш не поданий, період відкритий). */
  readonly canAdd: boolean;
  /** Перейти на вкладку з ключем (`sheetTabKey`). */
  readonly onSelect: (tabKey: string) => void;
}

/**
 * Дії копій аркуша над смугою вкладок: «Add copy», «Rename», «Delete copy» (лише для копії).
 *
 * ⚠ Окремий лінивий чанк (`D-132`): `DocumentPage` стоїть упритул до бюджету, а аркуша, що копіюється,
 * у більшості документів немає — компонент не малює нічого й сторінка його не вантажить.
 *
 * ⛔ Відмову сервера (409 `ECR-DOC-0409`: `sheetCopyIsOriginal`, `sheetCopyHasWorkflow`) показує `ErrorAlert`
 * текстом каталогу за `messageKey`; власних кодів помилок тут немає.
 */
export function SheetCopyBar({ documentId, target, canAdd, onSelect }: SheetCopyBarProps): JSX.Element | null {
  const queryClient = useQueryClient();
  const [renameOpen, setRenameOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [nameDraft, setNameDraft] = useState<LocalizedValue>({});
  // ⛔ W-4 (аудит 2026-11): «Add copy» — неідемпотентний POST (копія в КОЖНОМУ періоді). `loading` блокує кнопку лише
  // після 100 мс (`usePendingLoading`), а `isPending` оновлюється після перерендеру: подвійний клік давав дві копії.
  // Ref — синхронно.
  const adding = useRef(false);

  const refresh = async (): Promise<void> => {
    // ⚠ Префікси ключів: `['document-tables', id, period]` залежить від періоду, який тут не потрібен.
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['document-tables', documentId] }),
      queryClient.invalidateQueries({ queryKey: ['document-tables-status', documentId] }),
      queryClient.invalidateQueries({ queryKey: ['document', documentId] }),
    ]);
  };

  const add = useMutation({
    mutationFn: () => createSheetCopy(documentId, target.sheetDefId),
    // Відмову показує власний ErrorAlert панелі — без другого глобального сповіщення.
    meta: { handled: true },
    onSuccess: async (created) => {
      await refresh();
      onSelect(sheetTabKey(target.sheetCode, created.sheetCopyId));
    },
  });

  const rename = useMutation({
    mutationFn: () => renameSheetGroup(documentId, target.sheetDefId, { nameL10n: nameDraft }),
    meta: { handled: true },
    onSuccess: async () => {
      setRenameOpen(false);
      await refresh();
    },
  });

  const remove = useMutation({
    mutationFn: (sheetCopyId: number) => deleteSheetCopy(documentId, sheetCopyId),
    onSuccess: async () => {
      setDeleteOpen(false);
      // Видалена вкладка активна — повертаємось на оригінал групи.
      onSelect(sheetTabKey(target.sheetCode, null));
      await refresh();
    },
    onError: () => {
      setDeleteOpen(false);
    },
  });
  const addLoading = usePendingLoading(add.isPending);
  const renameLoading = usePendingLoading(rename.isPending);

  const isCopy = target.sheetCopyId !== null;
  if (!target.isCopyable && !isCopy) return null;

  // TC02: чат копії видаляється разом із нею — число видно до підтвердження (0/невідомо — без зайвого рядка).
  const chatCount = target.chatMessageCount ?? 0;
  const deleteText =
    chatCount > 0
      ? `${t('documents.sheetCopies.deleteText')} ${t('documents.sheetCopies.deleteChat', { n: chatCount })}`
      : t('documents.sheetCopies.deleteText');

  // Відмова додавання/видалення — банером; «повторити» не пропонуємо (повторне видалення без підтвердження).
  const refusal = add.error ?? remove.error;

  return (
    <Stack gap="xs" data-testid="sheet-copy-bar">
      <Group gap="xs" role="group" aria-label={t('documents.sheetCopies.group')}>
        <Button
          size="xs"
          variant="default"
          disabled={!canAdd}
          loading={addLoading}
          data-testid="sheet-copy-add"
          onClick={() => {
            if (adding.current) return;
            adding.current = true;
            remove.reset();
            add.mutate(undefined, {
              onSettled: () => {
                adding.current = false;
              },
            });
          }}
        >
          {t('documents.sheetCopies.add')}
        </Button>
        <Button
          size="xs"
          variant="default"
          data-testid="sheet-copy-rename"
          onClick={() => {
            rename.reset();
            setNameDraft({ ...target.groupNameValues });
            setRenameOpen(true);
          }}
        >
          {t('documents.sheetCopies.rename')}
        </Button>
        {isCopy && (
          <Button
            size="xs"
            variant="default"
            color="statusError"
            data-testid="sheet-copy-delete"
            onClick={() => {
              add.reset();
              remove.reset();
              setDeleteOpen(true);
            }}
          >
            {t('documents.sheetCopies.delete')}
          </Button>
        )}
      </Group>

      {refusal !== null && <ErrorAlert error={refusal} />}

      <Modal
        opened={renameOpen}
        onClose={() => {
          setRenameOpen(false);
        }}
        title={t('documents.sheetCopies.renameTitle')}
      >
        <Stack>
          <LocalizedInput label={t('documents.sheetCopies.nameLabel')} value={nameDraft} onChange={setNameDraft} required />
          {rename.error !== null && <ErrorAlert error={rename.error} />}
          <Group justify="flex-end">
            <Button
              variant="default"
              onClick={() => {
                setRenameOpen(false);
              }}
            >
              {t('common.cancel')}
            </Button>
            <Button
              disabled={!hasAnyText(nameDraft)}
              loading={renameLoading}
              data-testid="sheet-copy-rename-save"
              onClick={() => {
                rename.mutate();
              }}
            >
              {t('common.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      {isCopy && (
        <LazyConfirmModal
          opened={deleteOpen}
          title={t('documents.sheetCopies.deleteTitle', { name: target.label })}
          text={deleteText}
          verb={t('documents.sheetCopies.delete')}
          danger
          isPending={remove.isPending}
          onConfirm={() => {
            if (target.sheetCopyId !== null) remove.mutate(target.sheetCopyId);
          }}
          onClose={() => {
            setDeleteOpen(false);
          }}
        />
      )}
    </Stack>
  );
}
