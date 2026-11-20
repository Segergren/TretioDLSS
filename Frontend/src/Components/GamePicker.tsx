import { useEffect, useId, useRef, useState } from 'react';
import { Check, Search } from 'lucide-react';
import { onMessage, send } from '../lib/bridge';

interface Game {
  id: string;
  name: string;
  icon: string;
}

/** Typeable select over Discord's list of known games. */
export function GamePicker({ value, onChange, className = '' }: { value: string; onChange: (name: string) => void; className?: string }) {
  const [text, setText] = useState(value);
  const [open, setOpen] = useState(false);
  const [games, setGames] = useState<Game[]>([]);
  const [active, setActive] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);
  const lastQuery = useRef('');
  const id = useId();

  useEffect(() => setText(value), [value]);

  useEffect(
    () =>
      onMessage((method, content) => {
        if (method !== 'DiscordGames' || content?.query !== lastQuery.current) return;
        setGames(content.games ?? []);
        setActive(0);
      }),
    [],
  );

  useEffect(() => {
    if (!open) return;
    const q = text.trim();
    const t = setTimeout(() => {
      lastQuery.current = q;
      if (q.length === 0) setGames([]);
      else send('SearchDiscordGames', { query: q });
    }, 150);
    return () => clearTimeout(t);
  }, [text, open]);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) close();
    };
    window.addEventListener('mousedown', onDown);
    return () => window.removeEventListener('mousedown', onDown);
  });

  const close = () => {
    setOpen(false);
    setText(value);
  };

  const choose = (g: Game) => {
    onChange(g.name);
    setText(g.name);
    setOpen(false);
  };

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setOpen(true);
      setActive((i) => Math.min(games.length - 1, i + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActive((i) => Math.max(0, i - 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (open && games[active]) choose(games[active]);
    } else if (e.key === 'Escape' || e.key === 'Tab') {
      close();
    }
  };

  return (
    <div ref={rootRef} className={`relative min-w-0 ${className}`}>
      <div className="flex h-10 items-center gap-2 rounded-xl border border-edge-strong bg-night px-3 transition-colors duration-200 hover:border-dusk">
        <Search size={16} className="shrink-0 text-dusk" aria-hidden />
        <input
          role="combobox"
          aria-label="Discord game"
          aria-expanded={open}
          aria-controls={id}
          value={text}
          placeholder="Search games"
          onFocus={(e) => {
            e.target.select();
            setOpen(true);
          }}
          onChange={(e) => {
            setText(e.target.value);
            setOpen(true);
          }}
          onKeyDown={onKey}
          className="min-w-0 flex-1 bg-transparent text-[0.9375rem] text-chalk placeholder:text-dusk outline-none"
        />
      </div>
      {open && text.trim().length > 0 && (
        <ul id={id} role="listbox" className="menu-rise absolute top-full left-0 z-50 mt-1.5 max-h-[340px] w-full overflow-y-auto rounded-2xl border border-edge bg-boulevard p-1.5">
          {games.length === 0 ? (
            <li className="px-2.5 py-2 text-[0.8125rem] text-dusk">No matching game</li>
          ) : (
            games.map((g, i) => (
              <li
                key={g.id}
                role="option"
                aria-selected={g.name === value}
                onMouseEnter={() => setActive(i)}
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => choose(g)}
                className={`flex cursor-pointer items-center gap-2.5 rounded-xl px-2.5 py-1.5 ${i === active ? 'bg-marquee' : ''}`}
              >
                {g.icon ? (
                  <img src={g.icon} alt="" className="h-7 w-7 shrink-0 rounded-md" loading="lazy" />
                ) : (
                  <span className="h-7 w-7 shrink-0 rounded-md bg-night" aria-hidden />
                )}
                <span className={`min-w-0 flex-1 truncate text-[0.9375rem] ${g.name === value ? 'text-chalk' : 'text-haze'}`}>{g.name}</span>
                {g.name === value && <Check size={16} className="shrink-0 text-flamingo" aria-hidden />}
              </li>
            ))
          )}
        </ul>
      )}
    </div>
  );
}
