import { useEffect, useId, useRef, useState } from 'react';
import { Check, ChevronDown, Search, X } from 'lucide-react';
import { useStore } from '../lib/store';
import { useDiscordGames, type Game } from './GamePicker';

/** Top-bar game picker, next to the profile. Sets the Discord game. */
export function GameMenu() {
  const { state, update } = useStore();
  const [open, setOpen] = useState(false);
  const [text, setText] = useState('');
  const [active, setActive] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);
  const id = useId();
  const games = useDiscordGames(text, open, () => setActive(0));
  const game = state?.settings.discord.game.trim() ?? '';

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!rootRef.current?.contains(e.target as Node)) setOpen(false);
    };
    window.addEventListener('mousedown', onDown);
    return () => window.removeEventListener('mousedown', onDown);
  }, [open]);

  if (!state) return null;

  const toggle = () => {
    if (open) return setOpen(false);
    setText('');
    setActive(0);
    setOpen(true);
  };

  const choose = (name: string) => {
    update({ discord: { game: name } });
    setOpen(false);
  };

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setActive((i) => Math.min(games.length - 1, i + 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActive((i) => Math.max(0, i - 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const g: Game | undefined = games[active];
      if (g) choose(g.name);
    } else if (e.key === 'Escape') {
      setOpen(false);
    }
  };

  return (
    <div ref={rootRef} className="relative">
      <button
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-label={`Game: ${game || 'none'}`}
        onClick={toggle}
        className="flex h-11 max-w-[260px] items-center gap-2 rounded-full border border-edge bg-white/6 pr-3 pl-4 text-[0.9375rem] leading-none transition-colors duration-200 hover:border-edge-strong"
      >
        <span className="py-0.5 leading-normal text-dusk">Game</span>
        <span className={`min-w-0 truncate py-0.5 leading-normal font-medium ${game ? 'text-chalk' : 'text-dusk'}`}>{game || 'None'}</span>
        <ChevronDown size={16} className={`shrink-0 text-dusk transition-transform duration-200 ${open ? 'rotate-180' : ''}`} aria-hidden />
      </button>

      {open && (
        <div className="menu-rise absolute top-full right-0 z-50 mt-1.5 w-[320px] rounded-2xl border border-edge bg-boulevard p-1.5">
          <div className="flex h-10 items-center gap-2 rounded-xl border border-edge-strong bg-night px-3 focus-within:border-dusk">
            <Search size={16} className="shrink-0 text-dusk" aria-hidden />
            <input
              autoFocus
              role="combobox"
              aria-label="Search games"
              aria-expanded={games.length > 0}
              aria-controls={id}
              value={text}
              placeholder="Search games"
              onChange={(e) => setText(e.target.value)}
              onKeyDown={onKey}
              className="min-w-0 flex-1 bg-transparent text-[0.9375rem] text-chalk placeholder:text-dusk outline-none!"
            />
          </div>
          <ul id={id} role="listbox" className="mt-1 max-h-[340px] overflow-y-auto">
            {text.trim().length > 0 && games.length === 0 && <li className="px-2.5 py-2 text-[0.8125rem] text-dusk">No matching game</li>}
            {games.map((g, i) => (
              <li
                key={g.id}
                role="option"
                aria-selected={g.name === game}
                onMouseEnter={() => setActive(i)}
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => choose(g.name)}
                className={`flex cursor-pointer items-center gap-2.5 rounded-xl px-2.5 py-1.5 ${i === active ? 'bg-marquee' : ''}`}
              >
                {g.icon ? (
                  <img src={g.icon} alt="" className="h-7 w-7 shrink-0 rounded-md" loading="lazy" />
                ) : (
                  <span className="h-7 w-7 shrink-0 rounded-md bg-night" aria-hidden />
                )}
                <span className={`min-w-0 flex-1 truncate text-[0.9375rem] ${g.name === game ? 'text-chalk' : 'text-haze'}`}>{g.name}</span>
                {g.name === game && <Check size={16} className="shrink-0 text-flamingo" aria-hidden />}
              </li>
            ))}
          </ul>
          {game && (
            <div className="mt-1 border-t border-edge pt-1">
              <button
                type="button"
                onClick={() => choose('')}
                className="flex w-full items-center gap-2.5 rounded-xl px-2.5 py-2 text-left text-[0.9375rem] leading-5 text-haze hover:bg-marquee hover:text-chalk"
              >
                <X size={16} aria-hidden />
                No game
              </button>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
