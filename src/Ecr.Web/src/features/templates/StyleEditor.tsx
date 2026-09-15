import type { JSX } from 'react';
import { Alert, ColorInput, Fieldset, Group, NumberInput, Select, Switch, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import { type BorderWeight, type StyleDraft, whyCannotSaveStyle } from './style';

/**
 * Панель оформлення колонки (директива
 * `docs/build/directive-registry-lookup-and-cell-style.md`, Частина B, PR
 * B1) — Bold/Italic, розмір і назва шрифту, колір тексту/фону, товщина
 * рамки по чотирьох сторонах, вирівнювання, перенос тексту, формат числа.
 *
 * ⚠ Показ поза редагуванням цього самого стилю в живій сітці — окремий шар
 * (`DocumentGrid.tsx`, `cellProperties`, PR B2), не ця форма: тут лише
 * АВТОРСТВО стилю, шаблонного рівня, ОДИН РАЗ при конструюванні структури
 * (`B.0` директиви, "Рішення людини" — свідомо не Excel-подібне довільне
 * форматування довільного діапазону користувачем-заповнювачем).
 */

const BorderOptions: readonly { value: string; label: string }[] = [
  { value: '0', label: t('styles.borderNone') },
  { value: '1', label: t('styles.borderThin') },
  { value: '2', label: t('styles.borderMedium') },
  { value: '3', label: t('styles.borderThick') },
];

const HorizontalAlignOptions: readonly { value: string; label: string }[] = [
  { value: '0', label: t('styles.alignLeft') },
  { value: '1', label: t('styles.alignCenter') },
  { value: '2', label: t('styles.alignRight') },
  { value: '3', label: t('styles.alignJustify') },
];

const VerticalAlignOptions: readonly { value: string; label: string }[] = [
  { value: '0', label: t('styles.alignTop') },
  { value: '1', label: t('styles.alignMiddle') },
  { value: '2', label: t('styles.alignBottom') },
];

export function StyleEditor({
  draft,
  disabled,
  onChange,
}: {
  draft: StyleDraft;
  disabled: boolean;
  onChange: (next: StyleDraft) => void;
}): JSX.Element {
  const blocker = whyCannotSaveStyle(draft);

  return (
    <Fieldset legend={t('styles.legend')} disabled={disabled}>
      <TextInput
        label={t('styles.code')}
        description={t('styles.codeHint')}
        value={draft.code}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      {blocker !== null && (
        <Alert color="statusWarning" mt="xs">
          {blocker === 'CodeEmpty' ? t('styles.errCode') : t('styles.errCodeInvalid')}
        </Alert>
      )}

      <Group grow mt="sm">
        <Switch
          label={t('styles.bold')}
          checked={draft.isBold}
          onChange={(event) => onChange({ ...draft, isBold: event.currentTarget.checked })}
        />
        <Switch
          label={t('styles.italic')}
          checked={draft.isItalic}
          onChange={(event) => onChange({ ...draft, isItalic: event.currentTarget.checked })}
        />
        <Switch
          label={t('styles.wrapText')}
          checked={draft.wrapText}
          onChange={(event) => onChange({ ...draft, wrapText: event.currentTarget.checked })}
        />
      </Group>

      <Group grow mt="sm">
        <TextInput
          label={t('styles.fontName')}
          description={t('styles.fontNameHint')}
          value={draft.fontName}
          onChange={(event) => onChange({ ...draft, fontName: event.currentTarget.value })}
        />
        <NumberInput
          label={t('styles.fontSize')}
          min={1}
          value={draft.fontSize ?? ''}
          onChange={(value) =>
            onChange({ ...draft, fontSize: typeof value === 'number' ? value : null })
          }
        />
      </Group>

      <Group grow mt="sm">
        <ColorInput
          label={t('styles.foreground')}
          value={draft.foregroundHex}
          onChange={(value) => onChange({ ...draft, foregroundHex: value })}
        />
        <ColorInput
          label={t('styles.background')}
          value={draft.backgroundHex}
          onChange={(value) => onChange({ ...draft, backgroundHex: value })}
        />
      </Group>

      <Group grow mt="sm">
        <Select
          label={t('styles.horizontalAlign')}
          data={HorizontalAlignOptions}
          value={String(draft.horizontalAlign)}
          allowDeselect={false}
          onChange={(value) => {
            if (value !== null) onChange({ ...draft, horizontalAlign: Number(value) as StyleDraft['horizontalAlign'] });
          }}
        />
        <Select
          label={t('styles.verticalAlign')}
          data={VerticalAlignOptions}
          value={String(draft.verticalAlign)}
          allowDeselect={false}
          onChange={(value) => {
            if (value !== null) onChange({ ...draft, verticalAlign: Number(value) as StyleDraft['verticalAlign'] });
          }}
        />
      </Group>

      <Fieldset legend={t('styles.borderLegend')} mt="sm">
        <Group grow>
          <BorderSelect
            label={t('styles.borderTop')}
            value={draft.borderTop}
            onChange={(weight) => onChange({ ...draft, borderTop: weight })}
          />
          <BorderSelect
            label={t('styles.borderRight')}
            value={draft.borderRight}
            onChange={(weight) => onChange({ ...draft, borderRight: weight })}
          />
        </Group>
        <Group grow mt="xs">
          <BorderSelect
            label={t('styles.borderBottom')}
            value={draft.borderBottom}
            onChange={(weight) => onChange({ ...draft, borderBottom: weight })}
          />
          <BorderSelect
            label={t('styles.borderLeft')}
            value={draft.borderLeft}
            onChange={(weight) => onChange({ ...draft, borderLeft: weight })}
          />
        </Group>
      </Fieldset>

      <TextInput
        mt="sm"
        label={t('styles.numberFormat')}
        description={t('styles.numberFormatHint')}
        value={draft.numberFormat}
        onChange={(event) => onChange({ ...draft, numberFormat: event.currentTarget.value })}
      />
    </Fieldset>
  );
}

function BorderSelect({
  label,
  value,
  onChange,
}: {
  label: string;
  value: BorderWeight;
  onChange: (weight: BorderWeight) => void;
}): JSX.Element {
  return (
    <Select
      label={label}
      data={BorderOptions}
      value={String(value)}
      allowDeselect={false}
      onChange={(next) => {
        if (next !== null) onChange(Number(next) as BorderWeight);
      }}
    />
  );
}
