import { useEffect, useState, type JSX } from 'react';
import { Alert, Badge, Button, Checkbox, Group, List, Modal, Stack, Text, Textarea } from '@mantine/core';
import { t } from '@/shared/i18n';
import { formatPeriodKey } from '@/shared/format';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';
import type { ConfirmedImpact, SourceImpact, SourceImpactAction } from './sourceImpact';

/**
 * B80 N5 (D-554): діалог впливу затвердження / повернення аркуша-джерела на залежні документи.
 *
 * Показує, які залежні документи й періоди зачепить дія і які з них уже затверджено (затверджений період не
 * перераховується — він застаріє), а далі за політикою зв'язки: `Warn` — підтвердження; `ConfirmWithReason` —
 * підтвердження з причиною (іде в журнал безпеки); `Block` — пояснення відмови без кнопки підтвердження.
 *
 * ✎ Рішення людини 19.11: діалог показується ЗАВЖДИ, коли є залежні (`required = false` — лише відомості: хто буде
 * перерахований, а хто ні); адміністратору з правом `Calculation.RecalculateApprovedDependents` — галочка «перерахувати також
 * затверджені» з обов'язковою причиною. `action = 'edit'` — перша правка аркуша-джерела за сесію: «Continue editing» і
 * «не питати до кінця сесії для цього аркуша»; для правки галочка «й затверджені» виконується ПІСЛЯ збереження правок.
 *
 * ⛔ Фокус на «Cancel» (правило `L6`): `Enter` не виконує дію, поки людина не прочитала вплив. Підтвердження несе
 * відбиток впливу, який бачила людина, — сервер відмовить, якщо вплив змінився.
 */
export function SourceImpactModal({
  opened,
  action,
  impact,
  isPending,
  onConfirm,
  onClose,
}: {
  readonly opened: boolean;
  readonly action: SourceImpactAction;
  readonly impact: SourceImpact | null;
  readonly isPending?: boolean | undefined;
  readonly onConfirm: (confirmed: ConfirmedImpact, extras: { readonly suppress: boolean }) => void;
  readonly onClose: () => void;
}): JSX.Element | null {
  const [reason, setReason] = useState('');
  const [recalculateApproved, setRecalculateApproved] = useState(false);
  const [suppress, setSuppress] = useState(false);
  useRestoreFocus(opened);
  const pendingLoading = usePendingLoading(isPending === true);

  // ⚠ Причина й галочки очищаються при КОЖНОМУ відкритті: причина попередньої дії не підписує наступну.
  useEffect(() => {
    if (opened) {
      setReason('');
      setRecalculateApproved(false);
      setSuppress(false);
    }
  }, [opened]);

  if (impact === null) return null;

  const blocked = impact.blocked;
  // Галочку «й затверджені» бачить лише той, хто має право, і лише коли є що перераховувати (затверджені залежні).
  const mayRecalculateApproved = !blocked && impact.canRecalculateApproved && impact.approvedCount > 0;
  const approvedChecked = mayRecalculateApproved && recalculateApproved;
  const needsReason = (impact.reasonRequired && !blocked) || approvedChecked;
  const trimmed = reason.trim();
  const confirmDisabled = needsReason && trimmed.length === 0;
  // ✎ Рішення людини 19.11: діалог показується ЗАВЖДИ, коли є залежні; `required = false` — лише відомості.
  const required = impact.required;
  const policyKey = blocked ? 'block' : needsReason ? 'confirmWithReason' : required ? 'warn' : 'info';

  return (
    <Modal opened={opened} onClose={onClose} title={t(`sourceImpact.title.${action}`)} returnFocus={false} size="lg">
      <Stack gap="sm">
        <Text size="sm">
          {required
            ? t(`sourceImpact.intro.${action}`, { approved: impact.approvedCount, total: impact.dependentCount })
            : t(`sourceImpact.intro.info.${action}`, { total: impact.dependentCount })}
        </Text>

        {impact.consumers.length > 0 ? (
          <Stack gap="xs" component="section" aria-label={t('sourceImpact.list')}>
            <Text size="sm" fw={600}>
              {t('sourceImpact.list')}
            </Text>
            <List size="sm" spacing={4} data-testid="source-impact-consumers">
              {impact.consumers.map((c) => (
                <List.Item key={`${String(c.documentId)}-${String(c.periodKey)}-${c.bindingCode}`}>
                  <Group gap="xs" wrap="wrap">
                    <Text size="sm" span>
                      {c.projectCode} · {c.documentKey} · {formatPeriodKey(c.periodKey)}
                    </Text>
                    <Badge size="sm" variant="light" color={c.willRecalculate ? 'statusInfo' : 'statusWarning'}>
                      {t(c.willRecalculate ? 'sourceImpact.status.pending' : 'sourceImpact.status.approved')}
                    </Badge>
                  </Group>
                  {c.sheets.length > 1 ? (
                    <Group gap={4} wrap="wrap" data-testid="source-impact-sheets">
                      {c.sheets.map((sh) => (
                        <Badge key={sh.sheetDefId} size="xs" variant="outline" color={sh.willRecalculate ? 'statusInfo' : 'statusWarning'}>
                          {sh.sheetCode} · {t(sh.willRecalculate ? 'sourceImpact.status.pending' : 'sourceImpact.status.approved')}
                        </Badge>
                      ))}
                    </Group>
                  ) : null}
                </List.Item>
              ))}
            </List>
          </Stack>
        ) : null}

        {blocked ? (
          <Alert color="statusError" data-testid="source-impact-blocked">
            {t('sourceImpact.policy.block')}
          </Alert>
        ) : (
          <Text size="sm" data-testid={`source-impact-policy-${policyKey}`}>
            {t(`sourceImpact.policy.${policyKey}`)}
          </Text>
        )}

        {mayRecalculateApproved ? (
          <Checkbox
            checked={recalculateApproved}
            onChange={(event) => setRecalculateApproved(event.currentTarget.checked)}
            label={t('sourceImpact.approved.label')}
            description={t('sourceImpact.approved.hint')}
            data-testid="source-impact-approved"
          />
        ) : null}

        {needsReason && !blocked ? (
          <Textarea
            label={t('sourceImpact.reason.label')}
            value={reason}
            onChange={(event) => setReason(event.currentTarget.value)}
            minRows={3}
            autosize
            required
            data-testid="source-impact-reason"
          />
        ) : null}

        {action === 'edit' ? (
          <Checkbox
            checked={suppress}
            onChange={(event) => setSuppress(event.currentTarget.checked)}
            label={t('sourceImpact.suppress.label')}
            data-testid="source-impact-suppress"
          />
        ) : null}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" data-autofocus onClick={onClose} data-testid="source-impact-cancel">
            {t('common.cancel')}
          </Button>
          {blocked ? null : (
            <Button
              {...(required ? { color: 'statusWarning' } : {})}
              loading={pendingLoading}
              disabled={confirmDisabled}
              onClick={() => {
                // ⛔ B145: закритий діалог не підтверджує (анімація закриття ще ~200 мс тримає кнопку в DOM).
                if (opened && isPending !== true) {
                  onConfirm(
                    {
                      hash: impact.impactHash,
                      reason: needsReason ? trimmed : null,
                      ...(approvedChecked ? { recalculateApproved: true } : {}),
                    },
                    { suppress },
                  );
                }
              }}
              data-testid="source-impact-confirm"
            >
              {action === 'edit'
                ? t('sourceImpact.confirm.edit')
                : required
                  ? t(`sourceImpact.confirm.${action}`)
                  : t(action === 'approve' ? 'workflow.approve' : 'workflow.reopen')}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
