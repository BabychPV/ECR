import type { JSX } from 'react';
import { Badge, Button, Group, List, Stack, Text } from '@mantine/core';
import type { RegistryEntryDto } from '@/api/types';
import { entryReferenceKinds, entryReferences } from '@/features/registries/api';
import { t } from '@/shared/i18n';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { problemText } from '@/shared/ui/problemText';

/**
 * Підтвердження видалення запису довідника і — на відмову `ECR-REG-0409` — той самий діалог у
 * стані «заблоковано» (винесено з `RegistriesPage`, щоб іти лінивим чанком, D-132).
 *
 * ⛔ Двох діалогів тут немає навмисно: «підтвердьте» і «не можна, бо на запис посилаються N
 * комірок» — це два стани однієї розмови, і людина не має шукати причину в плашці, що з'їхала
 * кудись у куток.
 *
 * ⛔ X-23: тут стояв голий `<Modal>` із заголовком «Remove» без назви запису й фокусом на
 * хрестику. Тепер — `ConfirmModal`: назва запису в заголовку, фокус на «Cancel». Стан
 * «заблоковано» — той самий діалог із недоступним підтвердженням і дією, яка справді можлива.
 */
export function DeleteEntryDialog({
  entry,
  error,
  isPending,
  isTemporal,
  onConfirm,
  onClose,
  onValidity,
}: {
  /** Запис, видалення якого підтверджуємо (`ФВ-8.6`); `null` — діалог закритий. */
  entry: RegistryEntryDto | null;
  /** Помилка видалення (`useDeleteRegistryEntry().error`). */
  error: unknown;
  isPending: boolean;
  isTemporal: boolean;
  onConfirm: (entry: RegistryEntryDto) => void;
  onClose: () => void;
  /** «Вікно чинності» замість повтору — запис виводять з обігу датою (`ФВ-8.5`). */
  onValidity: (entry: RegistryEntryDto | null) => void;
}): JSX.Element {
  // ⛔ Не `null` означає «відмовлено, бо на запис посилаються N комірок». Саме
  // це число, а не текст відмови, вирішує, ЩО показати замість «повторити».
  const blocked = entryReferences(error);

  // ⛔ V-08: ХТО посилається — комірки, інші записи, константи методологій.
  const blockedBy = entryReferenceKinds(error);

  return (
    <ConfirmModal
      opened={entry !== null}
      title={t('registries.deleteEntryTitle', { code: entry?.code ?? '' })}
      text={blocked === null ? entry?.display : undefined}
      verb={t('common.delete')}
      confirmDisabled={blocked !== null}
      isPending={isPending}
      onConfirm={() => {
        if (entry !== null) onConfirm(entry);
      }}
      onClose={onClose}
    >
      {blocked !== null && (
        <Stack gap="sm">
          {/* ⚠ Текст відмови — СЕРВЕРНИЙ, якщо сервер його локалізував
              (`messageKey`), інакше назва з каталогу (`ФВ-14.9a`). Поруч — саме число посилань, бо
              воно і є мірою наслідку. */}
          <Group gap="xs">
            <Badge color="statusWarning">{blocked}</Badge>
            <Text size="sm">{problemText(error).detail ?? problemText(error).title}</Text>
          </Group>

          {/* ⛔ V-08: розклад посилань за видами — куди йти виправляти. */}
          {blockedBy.length > 0 && (
            <List size="sm" aria-label={t('registries.referencedBy')}>
              {blockedBy.map(({ kind, count }) => (
                <List.Item key={kind}>
                  {t(`registries.referenceKind.${kind}`)}: {count}
                </List.Item>
              ))}
            </List>
          )}

          {/* ⛔ Замість «повторити». Повтор дасть ту саму відмову: змінити
              треба не запит, а намір — запис виводять з обігу датою
              (`ФВ-8.5`), і тоді історичні документи лишаються читабельними. */}
          {isTemporal && (
            <Group justify="flex-start">
              <Button variant="light" onClick={() => onValidity(entry)}>
                {t('registries.validity')}
              </Button>
            </Group>
          )}
        </Stack>
      )}
    </ConfirmModal>
  );
}
