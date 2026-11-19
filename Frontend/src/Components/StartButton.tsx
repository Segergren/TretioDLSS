import { Play, Square } from 'lucide-react';
import { PipelineStatus } from '../Models/types';

export function StartButton({
  status,
  canStart,
  onStart,
  onStop,
}: {
  status: PipelineStatus;
  canStart: boolean;
  onStart: () => void;
  onStop: () => void;
}) {
  const running = status === 'running' || status === 'waiting';
  const busy = status === 'starting' || status === 'stopping';

  if (running || status === 'stopping') {
    return (
      <button
        type="button"
        onClick={onStop}
        disabled={status === 'stopping'}
        className="bg-accent-fill inline-flex h-13 min-w-[9.5rem] shrink-0 justify-center items-center gap-2.5 rounded-full px-8 text-[1rem] font-semibold leading-none text-night transition-[filter] duration-200 enabled:hover:brightness-106 disabled:opacity-70"
      >
        {status === 'stopping' ? <Spinner dark /> : <Square size={16} fill="currentColor" className="shrink-0" aria-hidden />}
        {status === 'stopping' ? 'Stopping' : 'Stop'}
      </button>
    );
  }

  if (!canStart) {
    return (
      <button
        type="button"
        disabled
        className="inline-flex h-13 min-w-[9.5rem] shrink-0 justify-center cursor-not-allowed items-center gap-2.5 rounded-full bg-white/8 px-8 text-[1rem] font-semibold leading-none text-dusk"
      >
        <Play size={18} fill="currentColor" className="shrink-0" aria-hidden />
        Start
      </button>
    );
  }

  return (
    <button
      type="button"
      onClick={onStart}
      disabled={busy}
      className="bg-accent-fill inline-flex h-13 min-w-[9.5rem] shrink-0 justify-center items-center gap-2.5 rounded-full px-8 text-[1rem] font-semibold leading-none text-night transition-[filter] duration-200 enabled:hover:brightness-106"
    >
      {busy ? <Spinner dark /> : <Play size={18} fill="currentColor" className="shrink-0" aria-hidden />}
      {busy ? 'Starting' : 'Start'}
    </button>
  );
}

export function Spinner({ dark, size = 16 }: { dark?: boolean; size?: number }) {
  return (
    <span
      className={`spin inline-block shrink-0 rounded-full border-2 ${dark ? 'border-night/30 border-t-night' : 'border-white/30 border-t-white'}`}
      style={{ width: size, height: size }}
      aria-hidden
    />
  );
}
