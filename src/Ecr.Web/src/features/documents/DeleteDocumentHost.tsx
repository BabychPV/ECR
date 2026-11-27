import { useEffect, type JSX } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { deleteDocument } from './api';
import { LazyConfirmModal } from '@/features/workflow/lazyDialogs';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** ✎ b18 D-132: мутація видалення документа й діалог підтвердження (чанк за першим відкриттям). */
export interface DeleteDocumentHostProps {
  readonly documentId: number;
  readonly name: string;
  readonly opened: boolean;
  readonly onClose: () => void;
  /** Відмова сервера (або `null`) — банер рисує власник. */
  readonly onError: (error: unknown) => void;
}

export default function DeleteDocumentHost({
  documentId,
  name,
  opened,
  onClose,
  onError,
}: DeleteDocumentHostProps): JSX.Element {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  const remove = useMutation({
    mutationFn: () => deleteDocument(documentId),
    onSuccess: async () => {
      onClose();

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
      onClose();

      // ⚠ Відмова могла статися тому, що стан документа змінився за спиною
      // (аркуш подали з іншої вкладки). Перечитаний стан сховає кнопку, якщо
      // документ уже не чернетка, — банер із причиною лишається.
      await queryClient.invalidateQueries({ queryKey: ['document', documentId] });
    },
  });

  const { reset } = remove;
  useEffect(() => {
    if (opened) reset();
  }, [opened, reset]);

  useEffect(() => {
    onError(remove.error);
  }, [remove.error, onError]);

  return (
    <LazyConfirmModal
      opened={opened}
      title={t('documents.deleteTitle', { name })}
      text={t('documents.deleteText')}
      consequences={[
        { text: t('documents.deleteNote'), note: true },
        // ⛔ P1 «прихований аркуш»: клієнт бачить лише видимі аркуші, тож
        // «чернетка» тут — здогад, а не факт. Перевіряє сервер (усі аркуші,
        // зокрема приховані від цієї ролі); його відмову показує `refusal`.
        { text: t('documents.deleteServerChecks'), note: true },
      ]}
      verb={t('documents.delete')}
      danger
      isPending={remove.isPending}
      onConfirm={() => remove.mutate()}
      onClose={onClose}
    />
  );
}
