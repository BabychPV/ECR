import { useEffect, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, Modal, Select, Stack, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { AffectedStepsResponse, ApprovalRouteDto, RoleView } from '@/api/types';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Маршрут погодження проєкту (`ФВ-5.17`).
 *
 * ⛔ Сутність `ApprovalRoute` і таблиця `wf.ApprovalStep` існували від Етапу 3,
 * були покриті конфігурацією EF — і не мали ЖОДНОГО способу наповнення:
 * список кроків приватний, ендпоінта немає, seed нічого не створює. Дві
 * таблиці, якими ніхто не користується, — той самий клас, що дав `A7-25`.
 *
 * ⚠ Порожній маршрут — законний і найчастіший стан: затвердження одноетапне,
 * як було. Прибрати маршрут можна тією самою кнопкою, якою його заведено:
 * інакше помилково створений лишався б назавжди.
 */
export function ApprovalRouteEditor({ projectId }: { projectId: number }): JSX.Element {
  const queryClient = useQueryClient();
  const [opened, setOpened] = useState(false);
  const [steps, setSteps] = useState<number[]>([]);

  const route = useQuery({
    queryKey: ['approval-route', projectId],
    queryFn: () => apiFetch<ApprovalRouteDto>(`/api/v1/projects/${projectId}/approval-route`),
    enabled: opened,
  });

  const roles = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleView[]>('/api/v1/roles'),
    enabled: opened,
  });

  // ⚠ Стан форми наповнюється тим, що ВЖЕ налаштовано. Порожній список на
  // відкритті виглядав би як «маршруту немає», і збереження мовчки стерло б
  // наявний маршрут.
  useEffect(() => {
    if (route.data !== undefined) {
      setSteps(route.data.steps.map((step) => step.roleId));
    }
  }, [route.data]);

  const save = useMutation({
    mutationFn: () =>
      apiFetch<AffectedStepsResponse>(`/api/v1/projects/${projectId}/approval-route`, {
        method: 'PUT',
        body: JSON.stringify({ roleIds: steps }),
      }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['approval-route', projectId] });
      setOpened(false);
      showDone(
        result.steps === 0 ? t('workflow.routeCleared') : t('workflow.routeSaved', { count: result.steps }),
      );
    },

    // ⚠ Дві однакові ролі поспіль і неіснуюча роль приходять поясненням, а не
    // «не вдалося зберегти»: обидві помилки виправні прямо тут.
    onError: showApiError,
  });

  const roleName = (roleId: number): string =>
    (roles.data ?? []).find((r) => r.id === roleId)?.code ?? String(roleId);

  const options = (roles.data ?? []).map((r) => ({ value: String(r.id), label: r.code }));

  return (
    <>
      <Button size="xs" variant="default" onClick={() => setOpened(true)}>
        {t('workflow.route')}
      </Button>

      <Modal opened={opened} onClose={() => setOpened(false)} title={t('workflow.route')} size="lg">
        <Alert color="gray" title={t('workflow.routeHint')}>
          {t('workflow.routeEmptyHint')}
        </Alert>

        <Stack gap="xs" mt="md">
          {steps.map((roleId, index) => (
            // Порядок кроків і є маршрутом, тож ключ — позиція: та сама роль
            // може законно стояти на двох різних кроках.
            <Group key={`${index}:${roleId}`} gap="xs">
              <Badge variant="light">{index + 1}</Badge>
              <Text style={{ flex: 1 }}>{roleName(roleId)}</Text>
              <Button
                size="compact-xs"
                variant="subtle"
                color="red"
                onClick={() => setSteps(steps.filter((_, i) => i !== index))}
              >
                {t('common.delete')}
              </Button>
            </Group>
          ))}

          {steps.length === 0 && (
            <Text size="sm" c="dimmed">
              {t('workflow.routeNone')}
            </Text>
          )}
        </Stack>

        <Select
          mt="md"
          label={t('workflow.routeAddStep')}
          description={t('workflow.routeAddStepHint')}
          data={options}
          value={null}
          onChange={(value) => {
            if (value !== null) {
              setSteps([...steps, Number(value)]);
            }
          }}
          searchable
        />

        <Group justify="flex-end" mt="md">
          <Button variant="default" onClick={() => setOpened(false)}>
            {t('common.cancel')}
          </Button>
          <Button loading={save.isPending} onClick={() => save.mutate()}>
            {t('common.save')}
          </Button>
        </Group>
      </Modal>
    </>
  );
}
