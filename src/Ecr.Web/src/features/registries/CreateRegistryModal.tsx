import { useState, type JSX } from 'react';
import { Button, Checkbox, Group, Modal, Stack, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { StillNeeded } from '@/shared/ui/StillNeeded';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { createRegistry } from './api';

/** Поле форми заведення довідника, якого може бракувати для надсилання. */
type MissingRegistryField = 'code' | 'name';

/** Ключ напису для кожного бракуючого поля — той самий, що й у `label` полів нижче. */
export const RegistryFieldLabelKey: Record<MissingRegistryField, string> = {
  code: 'registries.code',
  name: 'registries.name',
};

/**
 * Чого формі заведення довідника бракує, щоб її можна було надіслати.
 *
 * ⛔ Чиста функція з тієї самої причини, що й `createProjectMissingFields`: з
 * однієї перевірки йдуть дві обіцянки — вимкнена кнопка і рядок
 * `StillNeeded`, — і вони не мають розійтися.
 */
export function createRegistryMissingFields(form: {
  code: string;
  name: string;
}): MissingRegistryField[] {
  const missing: MissingRegistryField[] = [];

  if (form.code.trim().length === 0) missing.push('code');
  if (form.name.trim().length === 0) missing.push('name');

  return missing;
}

/**
 * Заведення довідника з нуля (директива №11, T4).
 *
 * ⛔ U-18: до цього діалог жив усередині `RegistriesPage` і мав ІНШИЙ
 * контракт, ніж «New project»: кнопка підтвердження підписана «New registry»
 * (як і заголовок, і кнопка, що його відкрила — три однакові написи, жоден не
 * каже, що станеться), вимкнена без пояснення, поля не позначені, а «Cancel»
 * не було зовсім. Тепер — той самий контракт, що й у `CreateProjectModal`:
 * обов'язкові поля з `required`, рядок `StillNeeded`, «Cancel» і «Save».
 *
 * ⚠ «Save», а не «Create»: у продукті підтвердження форм підписане
 * `common.save` (23 використання, `common.create` не існує), і саме так уже
 * підписаний «New project».
 */
export function CreateRegistryModal({
  opened,
  onClose,
  onCreated,
}: {
  opened: boolean;
  onClose: () => void;
  onCreated: (code: string) => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [isTemporal, setIsTemporal] = useState(false);

  const create = useMutation({
    mutationFn: () =>
      createRegistry({
        code,
        nameL10n: { en: name },
        isTemporal,
      }),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.list() });
      setCode('');
      setName('');
      setIsTemporal(false);
      onClose();
      onCreated(created.code);
      showDone(t('registries.created'));
    },
    onError: showApiError,
  });

  const missingFields = createRegistryMissingFields({ code, name });

  return (
    <Modal opened={opened} onClose={onClose} title={t('registries.newRegistryTitle')}>
      <Stack gap="sm">
        <TextInput
          required
          label={t('registries.code')}
          description={t('registries.registryCodeHint')}
          value={code}
          onChange={(event) => setCode(event.currentTarget.value)}
          data-autofocus
        />

        <TextInput
          required
          label={t('registries.name')}
          value={name}
          onChange={(event) => setName(event.currentTarget.value)}
        />

        {/* ⛔ Рішення приймається ОДИН РАЗ при заведенні: змінити його для
            довідника з даними означало б перетлумачити вже введені записи. */}
        <Checkbox
          label={t('registries.temporalField')}
          description={t('registries.temporalFieldHint')}
          checked={isTemporal}
          onChange={(event) => setIsTemporal(event.currentTarget.checked)}
        />
      </Stack>

      <StillNeeded fields={missingFields.map((field) => t(RegistryFieldLabelKey[field]))} />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={missingFields.length > 0}
          loading={create.isPending}
          onClick={() => create.mutate()}
        >
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
