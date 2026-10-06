import { useState, type JSX } from 'react';
import { Button, Group, Modal, Select, Stack, Text, TextInput } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ConvertUnitRequest, ConvertUnitResponse, UnitRef } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatDecimal, normalizeDecimal } from '@/shared/format';
import { t } from '@/shared/i18n';
import { showApiError } from '@/shared/ui/notify';

/**
 * «Check a conversion» (`ФВ-16.2`, макет `screens-data.js` `/admin/units`,
 * діалог `convert`): друга дія екрана одиниць, а не ряд полів у шапці.
 *
 * ⚠ ЛІНИВИЙ чанк: діалог потрібен рідко, а шапка сторінки-переліку має одну
 * головну дію (`KIT.md` §1, п. 2) — до UI-21 тут стояли чотири поля й дві
 * кнопки в один ряд із заголовком.
 *
 * ⛔ Поле значення — `TextInput`, не `NumberInput`, і десяткове ніде не
 * проходить через `Number` (див. історію в `UnitsPage.tsx`, `e470777a`).
 *
 * ⛔ Цільова одиниця — лише тієї самої розмірності: сервер відхилить решту
 * `ECR-UOM-0422`, і пропонувати такий вибір означало б вести у відмову.
 */
export default function UnitConvertModal({
  units,
  initialFrom,
  onClose,
}: {
  readonly units: readonly UnitRef[];
  readonly initialFrom: string | null;
  readonly onClose: () => void;
}): JSX.Element {
  const [value, setValue] = useState('1');
  const [fromUnit, setFromUnit] = useState<string | null>(initialFrom);
  const [toUnit, setToUnit] = useState<string | null>(null);
  const [result, setResult] = useState<ConvertUnitResponse | null>(null);

  const source = units.find((unit) => unit.code === fromUnit);
  const targets = source === undefined ? [] : units.filter((u) => u.dimensionId === source.dimensionId);

  const convert = useMutation({
    mutationFn: () =>
      apiFetch<ConvertUnitResponse>('/api/v1/units/convert', {
        method: 'POST',
        body: JSON.stringify({
          value: value.trim(),
          fromUnit: fromUnit ?? '',
          toUnit: toUnit ?? '',
        } satisfies ConvertUnitRequest),
      }),
    onSuccess: setResult,
    onError: showApiError,
  });

  // ⚠ `ФВ-14.26`: спінер на кнопці — лише після 100 мс дії, не з першого кадру.
  const convertLoading = usePendingLoading(convert.isPending);

  const options = units.map((unit) => ({ value: unit.code, label: unit.code }));

  return (
    <Modal opened onClose={onClose} title={t('units.checkConversion')}>
      <Stack gap="sm">
        <TextInput
          inputMode="decimal"
          label={t('units.value')}
          value={value}
          onChange={(event) => {
            setValue(event.currentTarget.value);
            setResult(null);
          }}
          data-autofocus
        />

        <Select
          label={t('units.from')}
          data={options}
          value={fromUnit}
          onChange={(next) => {
            setFromUnit(next);

            // Цільова одиниця належала іншій розмірності — вибір більше не
            // має сенсу, і лишити його означало б надіслати відомо відхилений
            // запит.
            setToUnit(null);
            setResult(null);
          }}
          searchable
        />

        <Select
          label={t('units.to')}
          description={source === undefined ? t('units.pickFrom') : undefined}
          data={targets.map((unit) => ({ value: unit.code, label: unit.code }))}
          value={toUnit}
          onChange={(next) => {
            setToUnit(next);
            setResult(null);
          }}
          searchable
        />

        {result !== null && (
          // ⛔ `X-36`: результат — через канонічний `formatDecimal`, а не сирим
          // рядком `decimal(28,16)`. `?? result.value` — деградація в бік показу.
          <Text size="sm" fw={600} role="status" data-testid="unit-convert-result">
            {formatDecimal(result.value) ?? result.value} {result.unit}
          </Text>
        )}

        <Group justify="space-between" mt="sm">
          <Button
            variant="subtle"
            disabled={fromUnit === null || toUnit === null}
            onClick={() => {
              setFromUnit(toUnit);
              setToUnit(fromUnit);
              setResult(null);
            }}
          >
            {t('units.swap')}
          </Button>

          <Group gap="xs">
            <Button variant="default" onClick={onClose}>
              {t('common.close')}
            </Button>
            <Button
              // ⚠ Поле текстове, тож «не число» — можливий стан: кнопка, що
              // веде у відому відмову сервера, гірша за вимкнену.
              disabled={fromUnit === null || toUnit === null || normalizeDecimal(value) === null}
              loading={convertLoading}
              onClick={() => {
                if (convert.isPending) return;
                convert.mutate();
              }}
            >
              {t('units.convert')}
            </Button>
          </Group>
        </Group>
      </Stack>
    </Modal>
  );
}
