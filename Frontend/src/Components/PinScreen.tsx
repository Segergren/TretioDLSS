import { useEffect, useRef, useState } from 'react';
import { Tv } from 'lucide-react';
import { Button } from './Button';
import { useStore } from '../lib/store';

/** A Moonlight client wants to pair: the whole window asks for the PIN shown on the TV. */
export function PinScreen() {
  const { state, send } = useStore();
  const device = state?.stream.waitingPin;
  const [pin, setPin] = useState('');
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    setPin('');
    if (device) setTimeout(() => input.current?.focus(), 50);
  }, [device]);

  if (!device) return null;

  const submit = (value: string) => {
    if (value.length < 4) return;
    send('SubmitPin', { pin: value });
  };

  return (
    <div className="intro-fade fixed inset-0 z-[110] flex flex-col items-center justify-center bg-night px-9 text-center" onClick={() => input.current?.focus()}>
      <span className="flex h-20 w-20 items-center justify-center rounded-[26px] bg-flamingo/15 text-flamingo">
        <Tv size={40} aria-hidden />
      </span>
      <h1 className="mt-7 font-display text-[2.5rem] leading-[46px] font-bold text-chalk">Pair with {device}</h1>
      <p className="mt-3 text-[1rem] leading-6 text-dusk">Type the PIN that Moonlight shows on the TV.</p>

      <div className="relative mt-9 flex gap-4">
        <input
          ref={input}
          inputMode="numeric"
          autoComplete="one-time-code"
          aria-label="PIN"
          value={pin}
          maxLength={4}
          onChange={(e) => {
            const v = e.target.value.replace(/\D/g, '').slice(0, 4);
            setPin(v);
            if (v.length === 4) submit(v);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') submit(pin);
            if (e.key === 'Escape') send('CancelPin');
          }}
          className="absolute inset-0 opacity-0"
        />
        {[0, 1, 2, 3].map((i) => (
          <span
            key={i}
            className={`relative flex h-24 w-20 items-center justify-center rounded-[22px] border-2 bg-boulevard font-display text-[3rem] leading-none font-bold text-chalk tabular-nums transition-colors duration-150 ${
              i === Math.min(pin.length, 3) ? 'border-flamingo' : 'border-edge-strong'
            }`}
          >
            {pin[i] ?? ''}
            {i === pin.length && <span className="caret-blink absolute bottom-5 h-[3px] w-8 rounded-full bg-flamingo" aria-hidden />}
          </span>
        ))}
      </div>

      <div className="mt-10 flex gap-3">
        <Button variant="ghost" onClick={() => send('CancelPin')}>
          Cancel
        </Button>
        <Button onClick={() => submit(pin)} disabled={pin.length < 4}>
          Pair
        </Button>
      </div>
    </div>
  );
}
