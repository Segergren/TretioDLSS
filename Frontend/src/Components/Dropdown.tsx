import { ReactNode, useEffect, useId, useLayoutEffect, useRef, useState } from 'react';
import { Check, ChevronDown, Download, type LucideIcon } from 'lucide-react';

export interface DropdownOption<T extends string> {
  value: T;
  label: string;
  detail?: string;
  /** Shown before the label, for menus that mix kinds of things (displays and TVs, cards and windows). */
  icon?: LucideIcon;
  disabled?: boolean;
  reason?: string;
  /** A button inside the row, for example a download that makes a disabled option usable. */
  action?: { label: string; run: () => void; busy?: boolean; progress?: number };
  /** A compact picker inside the row that picks a variant directly, for example the frame multiplier. */
  choices?: { label: string; selected: boolean; run: () => void }[];
  /** The row's variant is the one in use, so the picker is highlighted. */
  choicesActive?: boolean;
  /** What the picker picks, for screen readers; "multiplier" when not given. */
  choicesNoun?: string;
}

interface Props<T extends string> {
  value: T;
  options: DropdownOption<T>[];
  onChange: (value: T) => void;
  ariaLabel: string;
  size?: 'md' | 'sm';
  className?: string;
  placeholder?: string;
  surface?: 'night' | 'boulevard';
  disabled?: boolean;
  align?: 'left' | 'right';
  /** Fixed trigger text, for places where the selected value is already shown elsewhere. */
  triggerLabel?: string;
  /** Replaces the field look entirely, for example a frost tile; gets the open state. */
  renderTrigger?: (open: boolean) => ReactNode;
  triggerClassName?: string;
}

export function Dropdown<T extends string>({
  value,
  options,
  onChange,
  ariaLabel,
  size = 'md',
  className = '',
  placeholder = 'Select',
  surface = 'night',
  disabled,
  align = 'left',
  triggerLabel,
  renderTrigger,
  triggerClassName,
}: Props<T>) {
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const [maxHeight, setMaxHeight] = useState(340);
  const rootRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const id = useId();
  const selected = options.find((o) => o.value === value);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener('mousedown', onDown);
    return () => window.removeEventListener('mousedown', onDown);
  }, [open]);

  // Always open downward; the list gets the room left below the trigger and scrolls beyond that.
  useLayoutEffect(() => {
    if (!open || !rootRef.current) return;
    const rect = rootRef.current.getBoundingClientRect();
    setMaxHeight(Math.max(120, window.innerHeight - rect.bottom - 16));
  }, [open]);

  const choose = (opt: DropdownOption<T>) => {
    if (opt.disabled) return;
    onChange(opt.value);
    setOpen(false);
  };

  const onKey = (e: React.KeyboardEvent) => {
    if (disabled) return;
    if (!open && (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown')) {
      e.preventDefault();
      setOpen(true);
      setActive(Math.max(0, options.findIndex((o) => o.value === value)));
      return;
    }
    if (!open) return;
    if (e.key === 'Escape') {
      setOpen(false);
    } else if (e.key === 'ArrowDown') {
      e.preventDefault();
      setActive((i) => Math.min(options.length - 1, i + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActive((i) => Math.max(0, i - 1));
    } else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      const opt = options[active];
      if (opt?.disabled && opt.action && !opt.action.busy) opt.action.run();
      else if (opt) choose(opt);
    } else if (e.key === 'Tab') {
      setOpen(false);
    }
  };

  const h = size === 'md' ? 'h-10 text-[0.9375rem]' : 'h-8 text-[0.8125rem]';
  const fill = surface === 'night' ? 'bg-night' : 'bg-boulevard';

  return (
    <div ref={rootRef} className={`relative min-w-0 ${className}`}>
      <button
        type="button"
        role="combobox"
        aria-label={ariaLabel}
        aria-expanded={open}
        aria-controls={id}
        disabled={disabled}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={onKey}
        className={
          renderTrigger
            ? `w-full min-w-0 text-left ${triggerClassName ?? ''}`
            : `flex w-full min-w-0 items-center gap-2 rounded-xl border border-edge-strong px-3.5 leading-none text-chalk transition-colors duration-200 enabled:hover:border-dusk disabled:opacity-50 ${h} ${fill}`
        }
      >
        {renderTrigger ? (
          renderTrigger(open)
        ) : (
          <>
            <span className={`min-w-0 flex-1 truncate py-0.5 text-left leading-normal ${selected || triggerLabel ? '' : 'text-dusk'}`}>
              {triggerLabel ?? selected?.label ?? placeholder}
            </span>
            <ChevronDown
              size={16}
              className={`shrink-0 text-dusk transition-transform duration-200 ${open ? 'rotate-180' : ''}`}
              aria-hidden
            />
          </>
        )}
      </button>
      {open && (
        <ul
          ref={listRef}
          id={id}
          role="listbox"
          className={`menu-rise absolute top-full z-50 mt-1.5 min-w-full overflow-y-auto rounded-2xl border border-edge bg-boulevard p-1.5 ${
            align === 'right' ? 'right-0' : 'left-0'
          }`}
          style={{ width: 'max-content', maxWidth: 'min(400px, calc(100vw - 32px))', maxHeight }}
        >
          {options.map((opt, i) => {
            const isSel = opt.value === value;
            // A one-line row centers its label on the picker or button beside it; a second line keeps the label on top.
            const multiline = !!(opt.reason || opt.detail || opt.action?.busy);
            return (
              <li
                key={opt.value}
                role="option"
                aria-selected={isSel}
                aria-disabled={opt.disabled && !opt.action}
                onMouseEnter={() => setActive(i)}
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => choose(opt)}
                className={`flex ${multiline ? 'items-start' : 'items-center'} gap-2.5 rounded-xl px-2.5 py-2 ${
                  opt.disabled ? 'cursor-not-allowed' : 'cursor-pointer'
                } ${i === active && (!opt.disabled || opt.action) ? 'bg-marquee' : ''}`}
              >
                {opt.icon ? (
                  <span className="flex h-5 w-[18px] shrink-0 items-center justify-center">
                    {isSel ? (
                      <Check size={18} className="text-flamingo" aria-hidden />
                    ) : (
                      <opt.icon size={18} className={opt.disabled ? 'text-dusk/50' : 'text-dusk'} aria-hidden />
                    )}
                  </span>
                ) : (
                  <span className="flex h-5 w-4 shrink-0 items-center justify-center">
                    {isSel && <Check size={16} className="text-flamingo" aria-hidden />}
                  </span>
                )}
                <span className="min-w-0 flex-1">
                  <span
                    className={`block text-[0.9375rem] leading-5 ${opt.disabled ? 'text-haze/50' : isSel ? 'text-chalk' : 'text-haze'}`}
                  >
                    {opt.label}
                  </span>
                  {(opt.reason || opt.detail) && (
                    <span className="mt-0.5 block text-[0.8125rem] leading-[18px] text-dusk tabular-nums">
                      {opt.reason ?? opt.detail}
                    </span>
                  )}
                  {opt.action?.busy && (
                    <span className="mt-1.5 block h-1 overflow-hidden rounded-full bg-night" aria-hidden>
                      <span
                        className="block h-full rounded-full bg-flamingo transition-[width] duration-300"
                        style={{ width: `${Math.round((opt.action.progress ?? 0) * 100)}%` }}
                      />
                    </span>
                  )}
                </span>
                {opt.choices && !opt.disabled && (
                  <ChoicePicker
                    label={`${opt.label} ${opt.choicesNoun ?? 'multiplier'}`}
                    choices={opt.choices}
                    active={!!opt.choicesActive}
                    onPicked={() => setOpen(false)}
                  />
                )}
                {opt.action && !opt.action.busy && (
                  <button
                    type="button"
                    tabIndex={-1}
                    aria-label={`${opt.action.label} ${opt.label}`}
                    onClick={(e) => {
                      e.stopPropagation();
                      opt.action!.run();
                    }}
                    className="flex h-7 shrink-0 cursor-pointer items-center gap-1.5 self-center rounded-full border border-edge-strong px-3 text-[0.8125rem] font-medium text-chalk transition-colors duration-200 hover:border-dusk hover:bg-night"
                  >
                    <Download size={14} aria-hidden />
                    {opt.action.label}
                  </button>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

/** The small picker inside a row; its menu is fixed-positioned so the list's scroll area does not clip it. */
function ChoicePicker({
  label,
  choices,
  active,
  onPicked,
}: {
  label: string;
  choices: NonNullable<DropdownOption<string>['choices']>;
  active: boolean;
  onPicked: () => void;
}) {
  const [rect, setRect] = useState<DOMRect | null>(null);
  const rootRef = useRef<HTMLSpanElement>(null);
  const current = choices.find((c) => c.selected) ?? choices[0];

  useEffect(() => {
    if (!rect) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setRect(null);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        setRect(null);
      }
    };
    const onScroll = (e: Event) => {
      if (!rootRef.current?.contains(e.target as Node)) setRect(null);
    };
    window.addEventListener('mousedown', onDown, true);
    window.addEventListener('keydown', onKey, true);
    window.addEventListener('scroll', onScroll, true);
    return () => {
      window.removeEventListener('mousedown', onDown, true);
      window.removeEventListener('keydown', onKey, true);
      window.removeEventListener('scroll', onScroll, true);
    };
  }, [rect]);

  const menuHeight = choices.length * 32 + 10;
  const up = rect != null && window.innerHeight - rect.bottom < menuHeight + 8 && rect.top > menuHeight + 8;

  return (
    <span ref={rootRef} className="shrink-0 self-center" onMouseDown={(e) => e.stopPropagation()} onClick={(e) => e.stopPropagation()}>
      <button
        type="button"
        tabIndex={-1}
        aria-label={label}
        aria-haspopup="listbox"
        aria-expanded={rect != null}
        onClick={(e) => setRect(rect ? null : e.currentTarget.getBoundingClientRect())}
        className={`flex h-7 cursor-pointer items-center gap-1 rounded-full pl-3 pr-2 text-[0.8125rem] font-semibold tabular-nums transition-colors duration-200 ${
          active ? 'bg-chalk text-night' : 'border border-edge-strong text-haze hover:border-dusk hover:text-chalk'
        }`}
      >
        {current.label}
        <ChevronDown size={14} className={`transition-transform duration-200 ${rect ? 'rotate-180' : ''} ${active ? 'text-night' : 'text-dusk'}`} aria-hidden />
      </button>
      {rect && (
        <span
          role="listbox"
          aria-label={label}
          className={`fixed z-[60] flex flex-col rounded-xl border border-edge bg-boulevard p-1 ${up ? 'menu-drop' : 'menu-rise'}`}
          style={{
            right: window.innerWidth - rect.right,
            minWidth: rect.width,
            ...(up ? { bottom: window.innerHeight - rect.top + 4 } : { top: rect.bottom + 4 }),
          }}
        >
          {choices.map((c) => {
            const on = c.selected && active;
            return (
              <button
                key={c.label}
                type="button"
                role="option"
                aria-selected={on}
                tabIndex={-1}
                onClick={() => {
                  c.run();
                  setRect(null);
                  onPicked();
                }}
                className={`flex h-8 cursor-pointer items-center justify-center rounded-lg px-3 text-[0.8125rem] font-semibold tabular-nums transition-colors duration-150 ${
                  on ? 'bg-chalk text-night' : 'text-haze hover:bg-marquee hover:text-chalk'
                }`}
              >
                {c.label}
              </button>
            );
          })}
        </span>
      )}
    </span>
  );
}
