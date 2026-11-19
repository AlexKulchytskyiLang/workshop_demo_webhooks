import { useState, type ReactNode } from 'react';
import { ENVIRONMENT_LABEL, HEALTH_COLOUR, HEALTH_LABEL } from '../lib/format';
import { Icon } from './Icon';
import { useToast } from './Feedback';

/** Status is always colour AND words (never colour alone), as the accessibility rules require. */
export function StatusPill({ health, detail }: { health: string; detail?: string }) {
  return (
    <>
      <span className={`pill ${HEALTH_COLOUR[health] ?? 'gray'}`}>{HEALTH_LABEL[health] ?? health}</span>
      {detail && <div className="muted sm" style={{ marginTop: 3 }}>{detail}</div>}
    </>
  );
}

export function EnvTag({ environment }: { environment: string }) {
  return <span className={`tag ${environment === 'production' ? 'prod' : 'stg'}`}>{ENVIRONMENT_LABEL[environment] ?? environment}</span>;
}

/** 24 hours of health in 12 steps. Gaps (no traffic) are drawn as breaks, and the numbers are always in text next to it. */
export function Sparkline({ series, tone }: { series: (number | null)[]; tone: 'green' | 'amber' | 'red' | 'muted' }) {
  const width = 96;
  const height = 26;
  const points = series.map((v, i) => (v === null ? null : { x: (i / Math.max(1, series.length - 1)) * width, y: height - 3 - (v / 100) * (height - 6) }));
  const runs: { x: number; y: number }[][] = [[]];
  for (const p of points) {
    if (p === null) {
      if (runs[runs.length - 1]!.length > 0) runs.push([]); // a gap in the traffic breaks the line
    } else {
      runs[runs.length - 1]!.push(p);
    }
  }
  return (
    <svg className="sparkline" viewBox={`0 0 ${width} ${height}`} aria-hidden="true" focusable="false">
      {runs.filter((r) => r.length > 0).map((run, i) => (
        run.length === 1
          ? <circle key={i} cx={run[0]!.x} cy={run[0]!.y} r="1.8" fill={`var(--${tone})`} />
          : <polyline key={i} points={run.map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ')} fill="none" stroke={`var(--${tone})`} strokeWidth="1.6" strokeLinejoin="round" strokeLinecap="round" />
      ))}
    </svg>
  );
}

export function Kpi({ label, value, detail, tone }: { label: string; value: string; detail?: ReactNode; tone?: 'up' | 'down' | 'muted' }) {
  return (
    <div className="card kpi">
      <div className="l">{label}</div>
      <div className={`v${tone === 'down' ? ' down' : ''}`}>{value}</div>
      {detail && <div className={`d ${tone ?? 'muted'}`}>{detail}</div>}
    </div>
  );
}

/** Copy to the clipboard with a spoken confirmation; falls back to selecting the text when the clipboard is not available. */
export function CopyButton({ text, label = 'Copy', what }: { text: string; label?: string; what?: string }) {
  const toast = useToast();
  return (
    <button
      type="button"
      className="btn sm"
      onClick={() => {
        void navigator.clipboard?.writeText(text).then(
          () => toast.show(`${what ?? 'Text'} copied`),
          () => toast.show('Copy is blocked in this browser. Select the text and copy it by hand.'),
        );
      }}
    >
      <Icon name="copy" />{label}
    </button>
  );
}

/** A block of code or HTTP that can be read with the keyboard (it is focusable so it can be scrolled) and copied. */
export function CodeBlock({ code, label, copy = true }: { code: string; label: string; copy?: boolean }) {
  return (
    <div className="codeblock">
      {copy && <div className="cb-tools"><CopyButton text={code} what={label} /></div>}
      <pre className="code" tabIndex={0} aria-label={label}>{code}</pre>
    </div>
  );
}

/** Radio cards (a group of mutually exclusive options with an explanation each) on real radio inputs. */
export function RadioCards<T extends string>({
  legend, name, value, options, onChange, columns,
}: {
  legend: string;
  name: string;
  value: T;
  options: { value: T; title: ReactNode; description?: ReactNode; disabled?: boolean; disabledReason?: string }[];
  onChange: (value: T) => void;
  columns?: 2 | 3 | 4;
}) {
  return (
    <fieldset className="rcgroup">
      <legend className="sr-only">{legend}</legend>
      <div className={`rcs${columns ? ` c${columns}` : ''}`}>
        {options.map((o) => (
          <label key={o.value} className={`rc${value === o.value ? ' on' : ''}${o.disabled ? ' disabled' : ''}`} title={o.disabled ? o.disabledReason : undefined}>
            <input type="radio" name={name} value={o.value} checked={value === o.value} disabled={o.disabled} onChange={() => onChange(o.value)} />
            <span>
              <span className="t">{o.title}</span>
              {o.description && <span className="d block">{o.description}</span>}
              {o.disabled && o.disabledReason && <span className="d block">{o.disabledReason}</span>}
            </span>
          </label>
        ))}
      </div>
    </fieldset>
  );
}

/** A checkbox-style switch with a visible label, for enabled/disabled style settings. */
export function Switch({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  const [id] = useState(() => `sw-${Math.random().toString(36).slice(2, 8)}`);
  return (
    <label htmlFor={id} className="row" style={{ cursor: 'pointer' }}>
      <input id={id} type="checkbox" role="switch" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      <span>{label}</span>
    </label>
  );
}

/** A real tab list (role=tablist) for switching panels on the same screen. The caller renders the panel with role=tabpanel and aria-labelledby={`${idPrefix}-${value}`}. */
export function TabList<T extends string>({
  label, idPrefix, tabs, value, onChange,
}: {
  label: string;
  idPrefix: string;
  tabs: { id: T; label: string; count?: number }[];
  value: T;
  onChange: (id: T) => void;
}) {
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {tabs.map((t) => (
        <button key={t.id} type="button" role="tab" id={`${idPrefix}-${t.id}`} aria-selected={value === t.id} aria-controls={`${idPrefix}-panel`} className="tabbtn" onClick={() => onChange(t.id)}>
          {t.label}{t.count !== undefined && <span className="muted"> ({t.count.toLocaleString('en-US')})</span>}
        </button>
      ))}
    </div>
  );
}

/** Shown instead of a screen the person's role cannot use, with the way to get access. */
export function NoAccess({ what }: { what: string }) {
  return (
    <div className="banner amber" role="note">
      <Icon name="lock" />
      <div className="grow"><b>Your role cannot {what}.</b> Ask an owner or an admin of your organisation if you need it.</div>
    </div>
  );
}
