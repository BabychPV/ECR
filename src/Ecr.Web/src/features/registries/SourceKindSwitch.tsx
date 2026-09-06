import { useState, type JSX } from 'react';
import { Alert, Button, Checkbox, Group, Modal, Select, Stack, Textarea } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { AffectedRowsResponse, RegistryDefDto, RegistrySourceKind } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Перемикання master-джерела **набором** довідників (`ФВ-13.10`, `ФВ-11.4`).
 *
 * ⛔ Набір складає людина, а не система. Сутності «група довідників» немає
 * навмисно: група — це факт одного перемикання, а не властивість довідника.
 * Через рік після переходу поле «група» лишилося б у кожного нового
 * довідника, і ніхто б не знав, що туди писати.
 *
 * ⚠ Усе або нічого. Невідомий код відхиляє операцію цілком, а перевірка
 * «немає відкритого періоду» робиться один раз на весь набір: половина блоку
 * в одному режимі, половина в іншому — гірше, ніж відмова.
 */
export function SourceKindSwitch({ registries }: { registries: RegistryDefDto[] }): JSX.Element {
  const queryClient = useQueryClient();

  const [opened, setOpened] = useState(false);
  const [codes, setCodes] = useState<string[]>([]);
  const [kind, setKind] = useState<RegistrySourceKind>('Local');
  const [reason, setReason] = useState('');

  const send = useMutation({
    mutationFn: () =>
      apiFetch<AffectedRowsResponse>('/api/v1/registries/source-kind', {
        method: 'PUT',
        body: JSON.stringify({ registryCodes: codes, sourceKind: kind, reason: reason.trim() }),
      }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['registries'] });
      setOpened(false);
      setCodes([]);
      setReason('');
      showDone(t('registries.sourceSwitched', { count: result.affectedRows }));
    },

    // ⚠ Відкритий період приходить як `ECR-REG-0422` з поясненням, чому саме
    // зараз перемикати не можна. Показуємо його, а не «не вдалося».
    onError: showApiError,
  });

  return (
    <>
      <Button size="xs" variant="default" onClick={() => setOpened(true)}>
        {t('registries.sourceSwitch')}
      </Button>

      <Modal
        opened={opened}
        onClose={() => setOpened(false)}
        title={t('registries.sourceSwitch')}
        size="lg"
      >
        <Alert color="orange" title={t('registries.sourceSwitchHint')}>
          {t('registries.sourceSwitchWarning')}
        </Alert>

        <Checkbox.Group mt="md" value={codes} onChange={setCodes} label={t('registries.title')}>
          <Stack gap="xs" mt="xs">
            {registries.map((registry) => (
              <Checkbox
                key={registry.code}
                value={registry.code}
                label={`${localized(registry.nameL10n) || registry.code} (${registry.code}) · ${registry.sourceKind}`}
              />
            ))}
          </Stack>
        </Checkbox.Group>

        <Select
          mt="md"
          label={t('registries.sourceKind')}
          data={['External', 'Hybrid', 'Local']}
          value={kind}
          onChange={(value) => setKind((value ?? 'Local') as RegistrySourceKind)}
          allowDeselect={false}
        />

        {/* ⛔ Причина обов'язкова: через рік питання «навіщо перемикали цей
            набір разом» — єдине, на яке доведеться відповісти, і відповідь має
            бути в журналі, а не в чиїйсь пам'яті. */}
        <Textarea
          mt="md"
          label={t('workflow.reason')}
          description={t('registries.sourceReasonHint')}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          autosize
          minRows={2}
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setOpened(false)}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={codes.length === 0 || reason.trim().length === 0}
            loading={send.isPending}
            onClick={() => send.mutate()}
          >
            {t('registries.sourceSwitch')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
