import { useEffect, useState, type ReactNode } from 'react';
import { Play, Square } from 'lucide-react';
import { TextMorph } from 'torph/react';
import { PipelineStatus } from '../Models/types';

/**
 * One button for the whole run: Start, Starting, Waiting, Stop. It stays mounted and the label
 * morphs letter by letter (Torph), so the width follows the text instead of one button replacing another.
 */
export function StartButton({
  status,
  canStart,
  onStart,
  onStop,
  waitingLabel,
}: {
  status: PipelineStatus;
  canStart: boolean;
  onStart: () => void;
  onStop: () => void;
  /** Shown instead of Stop while waiting for something outside the app, such as the TV; hover reveals Cancel. */
  waitingLabel?: string;
}) {
  const [hover, setHover] = useState(false);
  // The cursor that just pressed Start is still on the button: Cancel waits until it has left once.
  const [armed, setArmed] = useState(false);
  const running = status === 'running' || status === 'waiting';
  const waitingForTv = status === 'waiting' && !!waitingLabel;
  useEffect(() => {
    if (waitingForTv) setArmed(!hover);
  }, [waitingForTv]); // eslint-disable-line react-hooks/exhaustive-deps
  const showCancel = hover && armed;

  let key: string;
  let icon: ReactNode;
  let label: string;
  let tone: 'accent' | 'soft' | 'off';
  let onClick: (() => void) | undefined;
  let disabled = false;

  if (waitingForTv) {
    key = showCancel ? 'cancel' : 'waiting';
    icon = showCancel ? <Square size={15} fill="currentColor" className="shrink-0" aria-hidden /> : <Spinner />;
    label = showCancel ? 'Cancel' : waitingLabel!;
    tone = 'soft';
    onClick = onStop;
  } else if (running || status === 'stopping') {
    key = status === 'stopping' ? 'stopping' : 'stop';
    icon = status === 'stopping' ? <Spinner dark /> : <Square size={16} fill="currentColor" className="shrink-0" aria-hidden />;
    label = status === 'stopping' ? 'Stopping' : 'Stop';
    tone = 'accent';
    onClick = onStop;
    disabled = status === 'stopping';
  } else if (!canStart) {
    key = 'off';
    icon = <Play size={18} fill="currentColor" className="shrink-0" aria-hidden />;
    label = 'Start';
    tone = 'off';
    disabled = true;
  } else {
    const busy = status === 'starting';
    key = busy ? 'starting' : 'start';
    icon = busy ? <Spinner dark /> : <Play size={18} fill="currentColor" className="shrink-0" aria-hidden />;
    label = busy ? 'Starting' : 'Start';
    tone = 'accent';
    onClick = onStart;
    disabled = busy;
  }

  const tones = {
    accent: 'bg-accent-fill text-night enabled:hover:brightness-106',
    soft: 'frost-light text-chalk hover:bg-white/16',
    off: 'cursor-not-allowed bg-white/8 text-dusk',
  };

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={waitingForTv ? `${waitingLabel}, click to cancel` : undefined}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => {
        setHover(false);
        setArmed(true);
      }}
      // While waiting the button keeps the width of the waiting label, so hovering to Cancel only morphs the text.
      className={`relative inline-flex h-13 shrink-0 items-center justify-center gap-2.5 overflow-hidden rounded-full px-8 text-[1rem] leading-none font-semibold whitespace-nowrap transition-[min-width,background-color,color,filter] duration-300 ${waitingForTv ? 'min-w-[12.25rem]' : 'min-w-[9.5rem]'} ${tones[tone]}`}
    >
      <span key={key} className="label-morph inline-flex shrink-0">
        {icon}
      </span>
      <TextMorph duration={320}>{label}</TextMorph>
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
