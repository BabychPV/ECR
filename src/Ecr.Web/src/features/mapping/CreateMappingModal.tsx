import { useState, type JSX } from 'react';
import { Button, Modal, NumberInput, Select, Stack, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useMutation } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import type { AggregationKind, FieldTargetKind } from '@/api/types';
import { createEntityFieldMap } from './api';
import { t } from '@/shared/i18n';

/** Способи згортання точок періоду (`D-118`), у порядку показу форми. */
const AggregationOptions: readonly Exclude<AggregationKind, null>[] = [
  'Sum',
  'Avg',
  'Min',
  'Max',
  'Last',
  'First',
];

/**
 * Форма заведення мапінгу поля джерела (Прогалина 1 директиви паритету зі
 * старою системою).
 *
 * ⛔ До цієї форми `EntityFieldMap` заводився лише двома фабриками домену,
 * викликаними виключно з тестів — жодного шляху АПІ до створення не було, а
 * значить і жодного шляху з інтерфейсу. Форма навмисно проста: рядок-адресат
 * і спосіб згортання — необов'язкові (мапінг без них лишає точки сирими для
 * звірки, `D-118`), а одиниці межі інтеграції тут не заводяться — це
 * повноцінний окремий екран, якого директива паритету не просить.
 */
export function CreateMappingModal({
  sourceEntityId,
  initialSourceField,
  opened,
  onClose,
  onCreated,
}: {
  readonly sourceEntityId: number;
  readonly initialSourceField?: string;
  readonly opened: boolean;
  readonly onClose: () => void;
  readonly onCreated: () => void;
}): JSX.Element {
  const [sourceField, setSourceField] = useState(initialSourceField ?? '');
  const [targetKind, setTargetKind] = useState<FieldTargetKind>('Column');
  const [targetId, setTargetId] = useState<number | ''>('');
  const [targetRowKey, setTargetRowKey] = useState('');
  const [aggregation, setAggregation] = useState<AggregationKind | null>(null);

  const create = useMutation({
    mutationFn: () =>
      createEntityFieldMap({
        sourceEntityId,
        sourceField: sourceField.trim(),
        targetKind,
        targetColumnDefId: targetKind === 'Column' ? Number(targetId) : null,
        targetRegistryFieldDefId: targetKind === 'RegistryField' ? Number(targetId) : null,
        sourceUnitId: null,
        targetUnitId: null,
        targetRowKey: targetRowKey.trim().length === 0 ? null : targetRowKey.trim(),
        aggregation,
      }),
    onSuccess: () => {
      notifications.show({ message: t('mapping.created') });
      onCreated();
      onClose();
    },
    onError: (error) => {
      notifications.show({
        color: 'statusError',
        message: error instanceof EcrApiError ? error.message : String(error),
      });
    },
  });

  const canSubmit = sourceField.trim().length > 0 && targetId !== '';

  return (
    <Modal opened={opened} onClose={onClose} title={t('mapping.createTitle')}>
      <Stack gap="sm">
        <TextInput
          label={t('mapping.createField')}
          value={sourceField}
          onChange={(event) => setSourceField(event.currentTarget.value)}
        />

        <Select
          label={t('mapping.createKind')}
          value={targetKind}
          allowDeselect={false}
          onChange={(value) => setTargetKind((value as FieldTargetKind | null) ?? 'Column')}
          data={[
            { value: 'Column', label: t('mapping.createKindColumn') },
            { value: 'RegistryField', label: t('mapping.createKindRegistry') },
          ]}
        />

        <NumberInput
          label={t('mapping.createTargetId')}
          value={targetId}
          onChange={(value) => setTargetId(typeof value === 'number' ? value : '')}
        />

        <TextInput
          label={t('mapping.createRowKey')}
          description={t('mapping.createRowKeyHint')}
          value={targetRowKey}
          onChange={(event) => setTargetRowKey(event.currentTarget.value)}
        />

        <Select
          label={t('mapping.createAggregation')}
          value={aggregation}
          onChange={(value) => setAggregation((value as AggregationKind | null) ?? null)}
          clearable
          data={AggregationOptions.map((value) => ({ value, label: value }))}
        />

        <Button onClick={() => create.mutate()} loading={create.isPending} disabled={!canSubmit}>
          {t('mapping.createSubmit')}
        </Button>
      </Stack>
    </Modal>
  );
}
