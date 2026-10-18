import { useState, type JSX } from 'react';
import { Badge, Button, Group, Menu } from '@mantine/core';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { badgeLook, statusKey, statusTone } from '@/shared/ui/StatusBadge';
import { PermitEditorHead } from '../shell/PermitEditorHead';
import { PermitsPaths } from '../shell/permitPaths';
import { validateDraft, validateForPublish, type DraftProblem } from './draftModel';
import { useEditorActions } from './editorActions';
import { useTypeEditorApi } from './editorContext';
import { secondaryTextOf, textOf } from './labels';
import { SaveStatus } from './SaveStatus';
import './typeEditor.css';

/** Бейдж версії: «Draft v2» для чернетки, «Published v1» для опублікованого. */
function VersionBadge({ draft, version }: { readonly draft: boolean; readonly version: number }): JSX.Element {
  const state = draft ? 'Draft' : 'Published';
  const look = badgeLook(statusTone('version', state));
  return (
    <Badge
      size="sm"
      variant="transparent"
      radius="xs"
      c={look.text}
      bg={look.bg}
      styles={{
        root: {
          height: 20,
          paddingInline: 'var(--mantine-spacing-xs)',
          border: `1px solid ${look.border}`,
          textTransform: 'none',
          letterSpacing: 'normal',
          fontWeight: 500,
          fontSize: 'var(--mantine-font-size-xs)',
        },
      }}
      data-version-state={state}
    >
      {`${t(statusKey('version', state))} v${version}`}
    </Badge>
  );
}

/**
 * Шапка конструктора типу (мокап: `.editor-head`): назад, назва + друга мова, бейдж версії, рядок
 * збереження, дії «Check» і «Publish…». Кнопки діалогів кличуть `EditorActions` (PM-08c).
 */
export function TypeEditorHeader(): JSX.Element {
  const api = useTypeEditorApi();
  const actions = useEditorActions();
  const [checked, setChecked] = useState<DraftProblem[] | null>(null);
  const { type, draft, editable, save, problems } = api;

  const title = textOf(draft.nameL10n) || type.code;
  const subtitle = secondaryTextOf(draft.nameL10n, title);
  const version = editable ? (type.draftVersion ?? type.version + 1) : type.version;
  // Видалити можна лише тип, що ніколи не публікувався (сервер: `ECR-PRM-4094` для опублікованого).
  const deletable = editable && type.version === 0;

  const check = (): void => setChecked([...validateDraft(draft, { typeCode: type.code }), ...validateForPublish(draft)]);

  return (
    <>
      <PermitEditorHead
        back={{ label: t('permits.back.types'), href: PermitsPaths.types }}
        title={title}
        subtitle={subtitle === '' ? undefined : subtitle}
        badge={<VersionBadge draft={editable} version={version} />}
        progress={
          <SaveStatus
            save={save}
            updatedAt={type.updatedAt}
            problemCount={problems.length}
            onRetry={api.retry}
            onReload={() => void api.reload()}
          />
        }
        actions={
          <Group gap="xs">
            {editable && (
              <Button variant="default" onClick={check}>
                {t('permits.editor.actions.check')}
              </Button>
            )}
            {editable ? (
              <Button onClick={actions.openPublish}>{t('permits.editor.actions.publish')}</Button>
            ) : (
              <Button onClick={actions.openNewVersion}>{t('permits.editor.actions.newVersion')}</Button>
            )}
            {deletable && (
              <Menu position="bottom-end" withinPortal>
                <Menu.Target>
                  <Button variant="default" aria-label={t('permits.editor.actions.more')}>
                    …
                  </Button>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item color="statusError" onClick={actions.openDeleteType}>
                    {t('permits.editor.actions.deleteType')}
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            )}
          </Group>
        }
      />
      {checked !== null && (
        <Banner
          tone={checked.length === 0 ? 'success' : 'warning'}
          title={checked.length === 0 ? t('permits.editor.check.ok') : t('permits.editor.problems.title', { count: checked.length })}
          text={
            checked.length === 0 ? undefined : (
              <ul className="pt-list">
                {checked.map((problem, index) => (
                  <li key={`${problem.messageKey}-${index}`}>{t(problem.messageKey, problem.params)}</li>
                ))}
              </ul>
            )
          }
          dismiss={{ label: t('permits.editor.check.dismiss'), onDismiss: () => setChecked(null) }}
        />
      )}
    </>
  );
}
