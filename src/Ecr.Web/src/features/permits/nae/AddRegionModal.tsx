import { useState, type JSX } from 'react';
import { Button, Group, Modal, NativeSelect, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { lookupLabel, useLookupOptions } from '@/features/registries/rc812/data';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import { AreaSelect } from './AreaSelect';
import { createNaeRoot, type CreateNaeRootRequest, type NaeRootResult } from './naeManualApi';

/** Довідник регіонів, з якого вибирається корінь (його ж читає `seed-roots`). */
export const RegionRegistryCode = 'LAND_REGION';

export interface AddRegionModalProps {
  readonly opened: boolean;
  readonly onClose: () => void;
  /** Вузол, який варто вибрати в дереві: найглибший із створених (район, якщо його просили). */
  readonly onAdded: (nodeId: number, result: NaeRootResult) => void;
}

/**
 * ⚠ Сервер приймає в regionCode АНГЛІЙСЬКУ НАЗВУ регіону (`Atyrau`; верхній регістр і код вузла `R_ATYRAU` він
 * будує сам), а не код запису довідника: назву читаємо з картки запису, а не з `display` (це мова користувача).
 */
async function englishName(rowId: number, fallback: string): Promise<string> {
  if (rowId === 0) return fallback;
  const entry = await apiFetch<components['schemas']['RegistryEntryDetailDto']>(`/api/v1/registries/${RegionRegistryCode}/entries/${String(rowId)}`);

  return entry.displayL10n?.values?.['en'] ?? fallback;
}

const orNull = (value: string): string | null => (value.trim() === '' ? null : value.trim());

/**
 * «Додати регіон» (структура НВ без AF): регіон із довідника `LAND_REGION`, за бажанням район і назви найглибшого вузла.
 * ⚠ Сторінка тягне діалог через `lazy()` (бюджет чанка маршруту, `D-132`).
 */
export function AddRegionModal({ opened, onClose, onAdded }: AddRegionModalProps): JSX.Element {
  const queryClient = useQueryClient();
  const regions = useLookupOptions(RegionRegistryCode, '', null);
  const [regionCode, setRegionCode] = useState('');
  const [areaCode, setAreaCode] = useState('');
  const [nameEn, setNameEn] = useState('');
  const [nameRu, setNameRu] = useState('');
  const [nameKz, setNameKz] = useState('');
  const [error, setError] = useState<string | null>(null);

  const options = (regions.data?.items ?? []).map((row) => ({ value: row.code, label: lookupLabel(row) }));
  const chosen = regionCode === '' ? (options[0]?.value ?? '') : regionCode;

  const create = useMutation({
    mutationFn: async (args: { readonly rowId: number; readonly fallback: string; readonly body: Omit<CreateNaeRootRequest, 'regionCode'> }) =>
      createNaeRoot({ ...args.body, regionCode: await englishName(args.rowId, args.fallback) }),
    onSuccess: async (result) => {
      showDone(result.created ? t('permits.nae.manual.added') : t('permits.nae.manual.existed'));
      // Довідник міг з'явитися: визначення, рівні й дерево читаються заново.
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.all() });
      onAdded(result.areaEntryId ?? result.regionEntryId, result);
    },
    onError: (failure: unknown) => showApiError(failure),
  });
  const busy = usePendingLoading(create.isPending);

  const submit = (): void => {
    if (chosen === '') {
      setError(t('permits.nae.manual.regionRequired'));

      return;
    }
    setError(null);
    const row = (regions.data?.items ?? []).find((item) => item.code === chosen);
    create.mutate({
      rowId: row?.id ?? 0,
      fallback: row?.display ?? chosen,
      body: {
      areaCode: orNull(areaCode),
      nameEn: orNull(nameEn),
      nameRu: orNull(nameRu),
      nameKz: orNull(nameKz),
      },
    });
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('permits.nae.manual.addRegion')} size="md">
      <Stack gap="sm" data-nae-add-region="">
        {regions.error !== null && regions.error !== undefined ? (
          <ErrorAlert error={regions.error} onRetry={() => void regions.refetch()} />
        ) : (
          <NativeSelect
            label={t('permits.nae.manual.region')}
            value={chosen}
            data={options}
            onChange={(event) => setRegionCode(event.currentTarget.value)}
            disabled={regions.isPending}
            required
            data-nae-region=""
          />
        )}
        <AreaSelect
          label={t('permits.nae.manual.area')}
          description={t('permits.nae.manual.areaHint')}
          value={areaCode}
          onChange={setAreaCode}
          data-nae-area=""
        />
        <Text size="sm" c="dimmed">
          {t('permits.nae.manual.namesHint')}
        </Text>
        <TextInput label={t('permits.nae.manual.nameEn')} value={nameEn} onChange={(event) => setNameEn(event.currentTarget.value)} />
        <TextInput label={t('permits.nae.manual.nameRu')} value={nameRu} onChange={(event) => setNameRu(event.currentTarget.value)} />
        <TextInput label={t('permits.nae.manual.nameKz')} value={nameKz} onChange={(event) => setNameKz(event.currentTarget.value)} />
        {error !== null && (
          <Text size="sm" c="statusError" role="alert">
            {error}
          </Text>
        )}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button onClick={submit} loading={busy} disabled={create.isPending} data-nae-add-region-submit="">
            {t('permits.nae.manual.addRegionSubmit')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
