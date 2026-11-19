import { ReactNode, useEffect, useState } from 'react';

export function Toggle({
  checked,
  onChange,
  label,
  disabled,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
  disabled?: boolean;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-[background-color,filter] duration-200 enabled:hover:brightness-125 disabled:opacity-50 ${
        checked ? 'bg-flamingo' : 'bg-edge-strong'
      }`}
    >
      <span
        className={`inline-block h-[18px] w-[18px] rounded-full transition-transform duration-200 ${
          checked ? 'translate-x-[23px] bg-night' : 'translate-x-[3px] bg-dusk'
        }`}
      />
    </button>
  );
}

export function Slider({
  value,
  min,
  max,
  step,
  onChange,
  label,
  format,
  disabled,
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  onChange: (v: number) => void;
  label: string;
  format?: (v: number) => string;
  disabled?: boolean;
}) {
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);
  const fill = ((local - min) / (max - min)) * 100;
  return (
    <div className="flex w-full items-center gap-3">
      <input
        type="range"
        aria-label={label}
        className="tr-range h-1 min-w-0 flex-1"
        min={min}
        max={max}
        step={step}
        value={local}
        disabled={disabled}
        style={{ ['--fill' as any]: `${fill}%` }}
        onChange={(e) => {
          const v = Number(e.target.value);
          setLocal(v);
          onChange(v);
        }}
      />
      <span className="tabular w-14 shrink-0 text-right text-xs font-medium text-haze">
        {format ? format(local) : local}
      </span>
    </div>
  );
}

export function TextInput({
  value,
  onCommit,
  label,
  placeholder,
  className = '',
}: {
  value: string;
  onCommit: (v: string) => void;
  label: string;
  placeholder?: string;
  className?: string;
}) {
  const [local, setLocal] = useState(value);
  useEffect(() => setLocal(value), [value]);
  return (
    <input
      aria-label={label}
      value={local}
      placeholder={placeholder}
      onChange={(e) => setLocal(e.target.value)}
      onBlur={() => local !== value && onCommit(local)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
      }}
      className={`h-10 min-w-0 rounded-xl border border-edge-strong bg-night px-3 text-[0.9375rem] text-chalk placeholder:text-dusk transition-colors duration-200 hover:border-dusk ${className}`}
    />
  );
}

export function NumberInput({
  value,
  onCommit,
  label,
  min,
  max,
  suffix,
}: {
  value: number;
  onCommit: (v: number) => void;
  label: string;
  min: number;
  max: number;
  suffix?: string;
}) {
  const [local, setLocal] = useState(String(value));
  useEffect(() => setLocal(String(value)), [value]);
  const commit = () => {
    const n = Math.max(min, Math.min(max, Math.round(Number(local))));
    if (Number.isNaN(n)) {
      setLocal(String(value));
      return;
    }
    setLocal(String(n));
    if (n !== value) onCommit(n);
  };
  return (
    <div className="flex h-10 w-72 items-center rounded-xl border border-edge-strong bg-night pr-3 transition-colors duration-200 hover:border-dusk">
      <input
        aria-label={label}
        type="number"
        value={local}
        onChange={(e) => setLocal(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') (e.target as HTMLInputElement).blur();
        }}
        className="tabular h-full min-w-0 flex-1 bg-transparent pl-3 text-[0.9375rem] text-chalk outline-none focus-visible:outline-none"
      />
      {suffix && <span className="shrink-0 text-xs font-medium text-dusk">{suffix}</span>}
    </div>
  );
}

/** One labeled settings row: label and helper on the left, control on the right. */
export function OptionRow({
  label,
  helper,
  children,
  stacked,
}: {
  label: string;
  helper?: ReactNode;
  children: ReactNode;
  stacked?: boolean;
}) {
  return (
    <div
      className={`flex min-h-[4.125rem] gap-4 rounded-2xl border border-edge bg-night px-4 py-3 ${
        stacked ? 'flex-col' : 'items-center justify-between'
      }`}
    >
      <div className="min-w-0">
        <div className="text-[0.9375rem] leading-5 text-chalk">{label}</div>
        {helper && <div className="mt-0.5 text-[0.8125rem] leading-[18px] text-dusk">{helper}</div>}
      </div>
      <div className={stacked ? 'w-full' : 'flex shrink-0 items-center'}>{children}</div>
    </div>
  );
}

export function Panel({
  id,
  eyebrow,
  title,
  children,
  action,
}: {
  id?: string;
  eyebrow?: string;
  title: string;
  children: ReactNode;
  action?: ReactNode;
}) {
  return (
    <section id={id} className="scroll-mt-6 rounded-3xl border border-edge bg-boulevard p-5">
      <div className="mb-3 flex items-center justify-between gap-3">
        <div>
          {eyebrow && <div className="eyebrow mb-0.5">{eyebrow}</div>}
          <h2 className="text-[1.125rem] leading-6 font-semibold text-chalk">{title}</h2>
        </div>
        {action}
      </div>
      <div className="flex flex-col gap-2">{children}</div>
    </section>
  );
}
