import { useState, type JSX } from 'react';
import { Button, Group, Modal, Stack, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type {
  AffectedRowsResponse,
  RegistryDefDto,
  RegistryEntryDto,
  RegistryEntryIdResponse,
  RegistryEntryUpsertDto,
  SetValidityRequest,
} from '@/api/types';
import { LocalizedInput, hasAnyText, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Заведення і правка запису довідника (`ФВ-8.12`).
 *
 * ⛔ Дії не було в інтерфейсі: сторож вважав `POST /registries/{code}/entries`
 * досяжним, бо клієнт ЧИТАЄ ту саму адресу (`A7-42`). Довідники — це те, на
 * що посилаються колонки типу `Lookup`; без жодного запису такі колонки не
 * пропонують нічого, і документ заповнити неможливо.
 *
 * ⛔ Запис **не видаляється** ніколи: у комірках зберігається його `Id`, і
 * видалення зробило б історичні документи нечитабельними. Замість видалення —
 * вікно чинності (`ФВ-8.5`), і воно редагується окремою дією.
 */
export function RegistryEntryEditor({
  registry,
  entry,
  opened,
  onClose,
}: {
  registry: RegistryDefDto;
  entry: RegistryEntryDto | null;
  opened: boolean;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  const [code, setCode] = useState('');
  const [display, setDisplay] = useState<LocalizedValue>({});
  const [values, setValues] = useState<Record<string, string>>({});
  const [loadedFor, setLoadedFor] = useState<number | null | undefined>(undefined);

  // ⚠ Стан наповнюється при зміні запису, а не в ефекті: ефект дав би зайвий
  // рендер із порожніми полями, і діалог блимав би порожнім щоразу.
  const key = entry?.id ?? null;
  if (opened && loadedFor !== key) {
    setLoadedFor(key);
    setCode(entry?.code ?? '');

    // ⚠ Перелік віддає `display` вже вибраною мовою — об'єкта з усіма мовами
    // в ньому немає. Тому при правці показуємо те, що є, під мовою за
    // замовчуванням: підставити порожнечу означало б мовчки стерти назву.
    setDisplay(entry === null ? {} : { en: entry.display });
    setValues({});
  }

  const upsert = useMutation({
    mutationFn: () =>
      apiFetch<RegistryEntryIdResponse>(
        `/api/v1/registries/${encodeURIComponent(registry.code)}/entries`,
        {
          method: 'POST',
          body: JSON.stringify({
            // ⛔ `id: null` означає СТВОРЕННЯ. Той самий ендпоінт і на
            // створення, і на правку: розділяти їх означало б два шляхи до
            // одного інваріанта унікальності коду.
            id: entry?.id ?? null,
            registryDefId: registry.id,
            code: code.trim(),
            display: { values: display },
            parentEntryId: entry?.parentEntryId ?? null,
            values,
          } satisfies RegistryEntryUpsertDto),
        },
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.entries(registry.code) });
      onClose();
      showDone(entry === null ? t('registries.entryCreated') : t('registries.entrySaved'));
    },
    onError: showApiError,
  });

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={entry === null ? t('registries.newEntry') : t('registries.editEntry')}
    >
      <TextInput
        label={t('registries.code')}
        description={t('registries.entryCodeHint')}
        value={code}
        onChange={(event) => setCode(event.currentTarget.value)}
        data-autofocus
      />

      <LocalizedInput label={t('registries.name')} value={display} onChange={setDisplay} />

      {/* ⚠ Поля довідника описані в його схемі й у кожного свій тип. Поки
          редактор приймає їх текстом: сервер знає типи і відхилить невідповідне
          значення з поясненням. Типізоване поле на кожен тип — окрема робота,
          і робити її наосліп, без жодного заведеного довідника, немає сенсу. */}
      {registry.fields.length > 0 && (
        <Stack gap="xs" mt="sm">
          {registry.fields.map((field) => (
            <TextInput
              key={field.id}
              label={`${field.code}${field.isRequired ? ' *' : ''}`}
              description={field.dataType}
              value={values[field.code] ?? ''}
              onChange={(event) =>
                setValues((current) => ({ ...current, [field.code]: event.currentTarget.value }))
              }
            />
          ))}
        </Stack>
      )}

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={code.trim().length === 0 || !hasAnyText(display)}
          loading={upsert.isPending}
          onClick={() => upsert.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}

/**
 * Вікно чинності запису (`ФВ-8.5`).
 *
 * ⛔ Це заміна видалення, а не додаткова властивість. Запис, який більше не
 * використовують, закривають датою: у комірках лежить його `Id`, і видалення
 * зробило б історичні документи нечитабельними.
 *
 * ⚠ Відповідь несе кількість зачеплених рядків документів — саме вони стають
 * **осиротілими** (`ФВ-8.13`) і блокують подання. Показати це число до того,
 * як людина дізнається про наслідок із відмови подання, — і є сенс дії.
 */
export function ValidityEditor({
  registryCode,
  entry,
  onClose,
}: {
  registryCode: string;
  entry: RegistryEntryDto | null;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [loadedFor, setLoadedFor] = useState<number | null>(null);

  if (entry !== null && loadedFor !== entry.id) {
    setLoadedFor(entry.id);
    setFrom(entry.validFrom ?? '');
    setTo(entry.validTo ?? '');
  }

  const save = useMutation({
    mutationFn: () =>
      apiFetch<AffectedRowsResponse>(
        `/api/v1/registries/${encodeURIComponent(registryCode)}/entries/${entry?.id ?? 0}/validity`,
        {
          method: 'POST',
          body: JSON.stringify({
            // ⚠ Порожнє поле — це `null`, тобто «без межі», а не порожній
            // рядок: `DateOnly?` сервера розрізняє їх, і `''` дав би 400.
            from: from.length === 0 ? null : from,
            to: to.length === 0 ? null : to,
          } satisfies SetValidityRequest),
        },
      ),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.entries(registryCode) });
      onClose();
      showDone(t('registries.validitySaved', { count: result.affectedRows }));
    },
    onError: showApiError,
  });

  return (
    <Modal opened={entry !== null} onClose={onClose} title={t('registries.validity')}>
      <TextInput
        type="date"
        label={t('registries.validFrom')}
        description={t('registries.validityHint')}
        value={from}
        onChange={(event) => setFrom(event.currentTarget.value)}
        data-autofocus
      />

      <TextInput
        mt="sm"
        type="date"
        label={t('registries.validTo')}
        value={to}
        onChange={(event) => setTo(event.currentTarget.value)}
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button loading={save.isPending} onClick={() => save.mutate()}>
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
