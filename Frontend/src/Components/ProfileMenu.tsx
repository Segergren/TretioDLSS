import { useEffect, useRef, useState } from 'react';
import { Check, ChevronDown, Pencil, Plus, Trash2 } from 'lucide-react';
import { useStore } from '../lib/store';

/** Top-right profile picker. Changes save into the active profile on their own. */
export function ProfileMenu() {
  const { state, send } = useStore();
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const rootRef = useRef<HTMLDivElement>(null);
  const active = state?.settings.activeProfile ?? '';
  const profiles = state?.profiles ?? [];

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) close();
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && !editing) close();
    };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open, editing]);

  if (!state) return null;

  function close() {
    setOpen(false);
    setEditing(null);
  }

  function startRename(name: string) {
    setEditing(name);
    setDraft(name);
  }

  function commitRename() {
    const to = draft.trim();
    if (editing && to && to !== editing) send('RenameProfile', { name: editing, to });
    setEditing(null);
  }

  return (
    <div ref={rootRef} className="relative">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Profile: ${active}`}
        onClick={() => (open ? close() : setOpen(true))}
        className="flex h-11 max-w-[260px] items-center gap-2 rounded-full border border-edge bg-white/6 pr-3 pl-4 text-[0.9375rem] leading-none transition-colors duration-200 hover:border-edge-strong"
      >
        <span className="py-0.5 leading-normal text-dusk">Profile</span>
        <span className="min-w-0 truncate py-0.5 leading-normal font-medium text-chalk">{active}</span>
        <ChevronDown size={16} className={`shrink-0 text-dusk transition-transform duration-200 ${open ? 'rotate-180' : ''}`} aria-hidden />
      </button>

      {open && (
        <div role="menu" className="menu-rise absolute top-full right-0 z-50 mt-1.5 w-[320px] rounded-2xl border border-edge bg-boulevard p-1.5">
          <ul className="max-h-[340px] overflow-y-auto">
            {profiles.map((p) => {
              const isActive = p.name === active;
              const isEditing = editing === p.name;
              return (
                <li
                  key={p.name}
                  role="menuitemradio"
                  aria-checked={isActive}
                  onClick={() => {
                    if (isEditing) return;
                    if (!isActive) send('SelectProfile', { name: p.name });
                    close();
                  }}
                  className="group flex cursor-pointer items-start gap-2.5 rounded-xl px-2.5 py-2 hover:bg-marquee"
                >
                  <span className="flex h-5 w-4 shrink-0 items-center justify-center">
                    {isActive && <Check size={16} className="text-flamingo" aria-hidden />}
                  </span>
                  <span className="min-w-0 flex-1">
                    {isEditing ? (
                      <input
                        autoFocus
                        value={draft}
                        maxLength={40}
                        aria-label="Profile name"
                        onFocus={(e) => e.currentTarget.select()}
                        onChange={(e) => setDraft(e.target.value)}
                        onBlur={commitRename}
                        onClick={(e) => e.stopPropagation()}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter') commitRename();
                          if (e.key === 'Escape') setEditing(null);
                        }}
                        className="h-6 w-full rounded-md border border-edge-strong bg-night px-1.5 text-[0.9375rem] leading-5 text-chalk outline-none focus:border-dusk"
                      />
                    ) : (
                      <span className={`block truncate text-[0.9375rem] leading-5 ${isActive ? 'text-chalk' : 'text-haze'}`}>{p.name}</span>
                    )}
                    <span className="mt-0.5 block truncate text-[0.8125rem] leading-[18px] text-dusk tabular-nums">{p.summary}</span>
                  </span>
                  {!isEditing && (
                    <span className="flex shrink-0 items-center gap-0.5 self-center opacity-0 transition-opacity duration-150 group-hover:opacity-100 focus-within:opacity-100">
                      <IconButton label={`Rename ${p.name}`} onClick={() => startRename(p.name)}>
                        <Pencil size={14} aria-hidden />
                      </IconButton>
                      {profiles.length > 1 && (
                        <IconButton label={`Delete ${p.name}`} onClick={() => send('DeleteProfile', { name: p.name })}>
                          <Trash2 size={14} aria-hidden />
                        </IconButton>
                      )}
                    </span>
                  )}
                </li>
              );
            })}
          </ul>
          <div className="mt-1 border-t border-edge pt-1">
            <button
              type="button"
              role="menuitem"
              onClick={() => send('NewProfile')}
              className="flex w-full items-center gap-2.5 rounded-xl px-2.5 py-2 text-left text-[0.9375rem] leading-5 text-haze hover:bg-marquee hover:text-chalk"
            >
              <span className="flex h-5 w-4 shrink-0 items-center justify-center">
                <Plus size={16} aria-hidden />
              </span>
              New profile
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function IconButton({ label, onClick, children }: { label: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={(e) => {
        e.stopPropagation();
        onClick();
      }}
      className="flex h-7 w-7 items-center justify-center rounded-full text-dusk transition-colors duration-150 hover:bg-night hover:text-chalk"
    >
      {children}
    </button>
  );
}
