import { useState, type JSX } from 'react';
import { Button, Modal, NumberInput, Select, Stack, TextInput } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import type { AggregationKind, FieldTargetKind } from '@/api/types';
import { showApiError, showDone } from '@/shared/ui/notify';
import { createEntityFieldMap } from './api';
import { PiAfCatalogButton } from './PiAfCatalogPicker';
import { PiAfProbeAction } from './PiAfProbeAction';
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
  dataSourceId,
  initialSourceField,
  opened,
  onClose,
  onCreated,
}: {
  readonly sourceEntityId: number;

  /**
   * З'єднання, якому належить сутність, — щоб шлях можна було ВИБРАТИ з
   * каталогу джерела, а не набрати руками (`ФВ-13.13`).
   *
   * ⚠ Необов'язковий: форма живе й там, де з'єднання невідоме (перелік
   * сутностей ще не прийшов). Тоді кнопки каталогу просто немає, а поле
   * лишається текстовим — тобто нічого не ламається.
   */
  readonly dataSourceId?: number | undefined;

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
      showDone(t('mapping.created'));
      onCreated();
      onClose();
    },
    // ⛔ `X-08`: тут стояв `error.message` — сирий `detail` сервера
    // (українською без `messageKey`) або `TypeError: …` для мережі. Той самий
    // розбір, що й скрізь (`problemText`): подробиця — лише локалізована.
    onError: showApiError,
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

        {/* ⛔ Кнопка каталогу стоїть ПІД полем шляху і поруч із ним, а не в
            шапці вікна: вона заповнює саме це поле, і жодне інше. Права
            перевіряє вона сама — форма про них не знає. */}
        {dataSourceId !== undefined && (
          <PiAfCatalogButton dataSourceId={dataSourceId} onPick={setSourceField} />
        )}

        {/* ⛔ «Перевірити» (`ФВ-13.17`) — ДО першого збору: пробує РЕАЛЬНЕ
            джерело тим самим шляхом, що вписаний чи підставлений вище, і
            показує, чи він справді щось читає. Права перевіряє сама, як і
            кнопка каталогу. */}
        {dataSourceId !== undefined && (
          <PiAfProbeAction dataSourceId={dataSourceId} path={sourceField} onPickSuggestion={setSourceField} />
        )}

        <Select
          label={t('mapping.createKind')}
          value={targetKind}
          allowDeselect={false}
          onChange={(value) => {
            setTargetKind((value as FieldTargetKind | null) ?? 'Column');

            // ⛔ Аудит 2026-09-16 §10.4: ID належить ВИДУ цілі, а не формі.
            // Без цього скидання введений для `Column` ідентифікатор `42`
            // лишався в полі після перемикання на `RegistryField` — і
            // `canSubmit` (дивиться лише на `targetId !== ''`) дозволяв
            // надіслати його як `targetRegistryFieldDefId`. Число з чужого
            // простору імен проти реєстрових полів не перевіряється ніде, і
            // випадковий збіг з непов'язаною сутністю дав би мапінг, що тихо
            // вказує не туди. Той самий прийом, що `setSheets([])` на зміну
            // версії шаблону (`CreateDocumentModal.tsx`).
            setTargetId('');
          }}
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
