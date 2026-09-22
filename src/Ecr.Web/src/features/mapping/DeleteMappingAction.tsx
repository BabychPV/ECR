import { useState, type JSX } from 'react';
import { Alert, Button, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { MappedFieldPreview } from '@/api/types';
import { collectedPointsBlockingDelete, deleteEntityFieldMap } from './api';
import { target } from './MappingGaps';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Видалення мапінгу з рядка перегляду (`BE-27`, макет `remove-mapping`).
 *
 * ⛔ Підтвердження ОБОВ'ЯЗКОВЕ, на відміну від сусідньої `PauseResumeAction`,
 * і різниця не косметична: пауза реверсивна тією самою кнопкою, а видалення —
 * ні. Форма взята з `ConfirmModal` (правило `L6`): назва об'єкта в заголовку,
 * дієслово на кнопці, фокус на «Скасувати», кнопка підтвердження — `danger`.
 *
 * ⚠ Мутація ВЛАСНА для кожного рядка — з того самого міркування, що в
 * `PauseResumeAction`: інакше «завантаження» й відмова одного рядка читалися б
 * для всього переліку одразу.
 *
 * ⚠ Наслідки названі обидва, і перший — з адресою: «мапінг видалено» нічого не
 * говорить людині, яка за хвилину побачить порожню комірку й не згадає, чому
 * вона порожня. Другий — приміткою: значення, яке вже лежить у комірці,
 * лишається, і це найчастіше питання після видалення.
 */
export function DeleteMappingAction({
  field,
}: {
  readonly field: MappedFieldPreview;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [opened, setOpened] = useState(false);

  const remove = useMutation({
    mutationFn: () => deleteEntityFieldMap(field.fieldMapId),
    onSuccess: async () => {
      setOpened(false);

      // ⚠ Префікс ключа, а не повний: сторінка запитує його як
      // `['mapping-preview', entityId, window.fromUtc]`, а `invalidateQueries`
      // зіставляє ЗА ПРЕФІКСОМ — тож застаріє будь-яка сутність і вікно.
      await queryClient.invalidateQueries({ queryKey: ['mapping-preview'] });
      showDone(t('mapping.deleteDone'));
    },
    onError: () => {
      // ⛔ Діалог закривається і на відмові: залишити його відкритим означає
      // запропонувати натиснути «Видалити» ще раз у відповідь на `409` — тобто
      // порадити повторити те, що сервер щойно відмовився робити. Пояснення
      // їде банером у рядку.
      setOpened(false);
    },
  });

  const collected = collectedPointsBlockingDelete(remove.error);

  return (
    <Stack gap="xs" miw="fit-content">
      <Button
        size="compact-xs"
        variant="subtle"
        color="statusError"
        onClick={() => {
          remove.reset();
          setOpened(true);
        }}
      >
        {t('mapping.delete')}
      </Button>

      <ConfirmModal
        opened={opened}
        title={t('mapping.deleteTitle', { field: field.sourceField })}
        text={t('mapping.deleteText')}
        consequences={[
          t('mapping.deleteConsequence', {
            target: target(field.targetRowKey, field.targetColumnCode),
          }),
          { text: t('mapping.deleteNote'), note: true },
        ]}
        verb={t('mapping.delete')}
        danger
        isPending={remove.isPending}
        onConfirm={() => remove.mutate()}
        onClose={() => setOpened(false)}
      />

      {/* ⛔ Відмова «за мапінгом уже зібрано дані» отримує ВЛАСНИЙ банер із
          числом точок: заголовок коду `ECR-INT-0409` нейтральний і покриває
          чотири різні стани, а людині тут потрібна рівно одна порада —
          призупинити замість видаляти, і підстава для неї (скільки саме даних
          залишиться без пояснення). Решта відмов іде штатним `ErrorAlert`.
          ⛔ Саме банер, а не `ErrorAlert` із власним `Error`: `problemText`
          для помилки не від нашого API свідомо ХОВАЄ текст і показує загальний
          заголовок, тож обгортка в `new Error(t(…))` мовчки викинула б і число,
          і пораду.
          ⚠ Без `onRetry`: «повторити» тут означало б видалення без
          підтвердження. */}
      {collected !== null ? (
        <Alert color="statusWarning" variant="light" data-testid="mapping-delete-blocked">
          <Text size="sm">{t('mapping.deleteBlocked', { points: collected })}</Text>
        </Alert>
      ) : (
        remove.error !== null && <ErrorAlert error={remove.error} />
      )}
    </Stack>
  );
}
