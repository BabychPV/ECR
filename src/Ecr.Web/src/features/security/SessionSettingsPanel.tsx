import { useState, type JSX } from 'react';
import { Button, Group, NumberInput, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import type { SessionSettingsInput, SessionSettingsView } from '@/api/types';
import { MeQueryKey } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/** Ключ запиту налаштувань сеансу. */
export const SessionSettingsQueryKey = ['security', 'session-settings'] as const;

const SessionSettingsPath = '/api/v1/security/session-settings';

/**
 * Вкладка «Session» екрана безпеки (D-471): вихід після N хвилин без дій.
 *
 * ⚠ Межі приходять із сервера (`min`/`max`), а не вписані тут: стенд e2e знижує мінімум конфігурацією, і
 * поле, що відмовляє там, де сервер приймає, було б другою реалізацією правила.
 *
 * ⚠ Після збереження — перезапит `/me`: годинник ЦІЄЇ вкладки бере межу звідти й переходить на нову одразу.
 * Інші вкладки й інші люди отримують її з наступним `/me`, а сервер застосовує її до всіх сеансів сам.
 */
export function SessionSettingsPanel({ canManage }: { canManage: boolean }): JSX.Element {
  const queryClient = useQueryClient();
  const settings = useQuery({
    queryKey: SessionSettingsQueryKey,
    queryFn: () => apiFetch<SessionSettingsView>(SessionSettingsPath),
  });

  return (
    <AsyncBoundary<SessionSettingsView>
      isPending={settings.isPending}
      error={settings.error}
      data={settings.data}
      skeleton="table"
      onRetry={() => void settings.refetch()}
    >
      {(view) => (
        // ⚠ `key` — щоб збережене (чи чуже) значення з сервера скидало чернетку поля без ефекту.
        <SessionSettingsForm
          key={view.idleTimeoutMinutes}
          view={view}
          canManage={canManage}
          onSaved={async (saved) => {
            queryClient.setQueryData(SessionSettingsQueryKey, saved);
            await queryClient.invalidateQueries({ queryKey: MeQueryKey });
          }}
        />
      )}
    </AsyncBoundary>
  );
}

function SessionSettingsForm({
  view,
  canManage,
  onSaved,
}: {
  view: SessionSettingsView;
  canManage: boolean;
  onSaved: (saved: SessionSettingsView) => Promise<void>;
}): JSX.Element {
  const [value, setValue] = useState<number | string>(view.idleTimeoutMinutes);

  const minutes = typeof value === 'number' ? value : Number.NaN;
  const valid =
    Number.isInteger(minutes) && minutes >= view.minIdleTimeoutMinutes && minutes <= view.maxIdleTimeoutMinutes;

  const save = useMutation({
    mutationFn: (input: SessionSettingsInput) =>
      apiFetch<SessionSettingsView>(SessionSettingsPath, { method: 'PUT', body: JSON.stringify(input) }),
    onSuccess: async (saved) => {
      await onSaved(saved);
      showDone(t('security.session.saved', { minutes: saved.idleTimeoutMinutes }));
    },
    onError: showApiError,
  });
  // ФВ-14.26: спінер — лише коли збереження справді триває, не з першого кадру.
  const saving = usePendingLoading(save.isPending);

  const hint = t('security.session.idleHint', {
    min: view.minIdleTimeoutMinutes,
    max: view.maxIdleTimeoutMinutes,
    default: view.defaultIdleTimeoutMinutes,
  });

  return (
    <form
      data-testid="session-settings"
      onSubmit={(event) => {
        event.preventDefault();
        if (valid) save.mutate({ idleTimeoutMinutes: minutes });
      }}
    >
      <Stack gap="sm" maw={420}>
        <NumberInput
          label={t('security.session.idleLabel')}
          description={hint}
          value={value}
          onChange={setValue}
          min={view.minIdleTimeoutMinutes}
          max={view.maxIdleTimeoutMinutes}
          step={5}
          allowDecimal={false}
          allowNegative={false}
          clampBehavior="none"
          disabled={!canManage}
          error={value !== '' && !valid ? hint : undefined}
          suffix={` ${t('security.session.minutesSuffix')}`}
        />
        <Text size="sm" c="dimmed">
          {t('security.session.idleExplain')}
        </Text>
        {canManage && (
          <Group>
            <Button
              type="submit"
              loading={saving}
              disabled={!valid || minutes === view.idleTimeoutMinutes}
            >
              {t('security.session.save')}
            </Button>
          </Group>
        )}
      </Stack>
    </form>
  );
}
