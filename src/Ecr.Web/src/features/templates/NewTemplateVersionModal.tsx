import { useState, type JSX } from 'react';
import { Button, Group, Modal, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { CreateTemplateVersionRequest, VersionIdResponse } from '@/api/types';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Діалог «New version» для одного шаблону.
 *
 * ⛔ Винесений з `TemplatesPage` (`U-19`), бо тепер ту саму дію пропонує й
 * картка шаблону. Дві копії мутації розійшлися б першою ж правкою — а
 * розходження тут не косметичне: `cloneFrom` вирішує, з якої версії береться
 * структура, і копія з іншим правилом тихо клонувала б не те.
 *
 * ⚠ `templateId === null` — діалог закритий. Одне значення замість пари
 * `opened` + `templateId`: відкритий діалог без шаблону не має сенсу.
 *
 * ⚠ `cloneFrom` — остання версія шаблону або `null`, якщо версій ще немає;
 * рахує його той, хто відкриває діалог, бо саме він уже тримає перелік.
 */
export function NewTemplateVersionModal({
  templateId,
  cloneFrom,
  onClose,
}: {
  templateId: number | null;
  cloneFrom: number | null;
  onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [versionNumber, setVersionNumber] = useState('');

  const createVersion = useMutation({
    mutationFn: (target: { templateId: number; cloneFrom: number | null }) =>
      apiFetch<VersionIdResponse>(`/api/v1/templates/${target.templateId}/versions`, {
        method: 'POST',
        body: JSON.stringify({
          versionNumber: versionNumber.trim(),
          cloneFromVersionId: target.cloneFrom,
        } satisfies CreateTemplateVersionRequest),
      }),
    onSuccess: async (_result, target) => {
      await queryClient.invalidateQueries({
        queryKey: queryKeys.templates.versionsOf(target.templateId),
      });
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.list() });
      onClose();
      setVersionNumber('');
      showDone(t('templates.versionCreated'));
    },
    onError: showApiError,
  });

  return (
    <Modal opened={templateId !== null} onClose={onClose} title={t('templates.newVersion')}>
      <TextInput
        label={t('templates.versionNumber')}
        description={t('templates.versionNumberHint')}
        value={versionNumber}
        onChange={(event) => setVersionNumber(event.currentTarget.value)}
        data-autofocus
      />

      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose}>
          {t('common.cancel')}
        </Button>
        <Button
          disabled={versionNumber.trim().length === 0}
          loading={createVersion.isPending}
          onClick={() => {
            if (templateId !== null) {
              createVersion.mutate({ templateId, cloneFrom });
            }
          }}
        >
          {t('common.save')}
        </Button>
      </Group>
    </Modal>
  );
}
