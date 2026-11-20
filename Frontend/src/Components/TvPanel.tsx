import { useState } from 'react';
import { Tv } from 'lucide-react';
import { Button } from './Button';
import { useStore } from '../lib/store';
import { outputName } from '../lib/options';

/** TV output: pairing, waiting for Moonlight, and what is streaming now. */
export function TvPanel() {
  const { state, send } = useStore();
  const [pin, setPin] = useState('');
  if (!state) return null;
  const st = state.stream;
  const tv = outputName(state);
  const session = st.session;

  const submit = () => {
    if (pin.trim().length < 4) return;
    send('SubmitPin', { pin: pin.trim() });
    setPin('');
  };

  return (
    <section className="frost rounded-[22px] border border-edge px-5 py-3.5" aria-label="TV stream">
      <div className="flex items-center gap-3">
        <Tv size={20} className="shrink-0 text-dusk" aria-hidden />
        <h2 className="flex-1 text-[1rem] leading-6 font-semibold text-chalk">{tv}</h2>
      </div>

      {st.error ? (
        <p className="mt-3 text-[0.9375rem] leading-5 text-error">The TV stream could not start: {st.error}</p>
      ) : st.waitingPin ? (
        <div className="mt-3 flex flex-wrap items-center gap-3">
          <p className="min-w-0 flex-1 text-[0.9375rem] leading-5 text-haze">
            {st.waitingPin} wants to pair. Enter the PIN that Moonlight shows on the TV.
          </p>
          <input
            autoFocus
            inputMode="numeric"
            value={pin}
            maxLength={8}
            aria-label="PIN"
            placeholder="PIN"
            onChange={(e) => setPin(e.target.value.replace(/\D/g, ''))}
            onKeyDown={(e) => e.key === 'Enter' && submit()}
            className="h-10 w-28 rounded-xl border border-edge-strong bg-night px-3 text-center text-[1.125rem] tracking-[0.2em] text-chalk tabular-nums outline-none focus:border-dusk"
          />
          <Button onClick={submit} disabled={pin.trim().length < 4}>
            Pair
          </Button>
          <Button variant="ghost" onClick={() => send('CancelPin')}>
            Cancel
          </Button>
        </div>
      ) : session?.width ? (
        <p className="mt-3 text-[0.9375rem] leading-5 text-haze tabular-nums">
          Streaming to {session.client}: {session.width} x {session.height}, {session.fps} fps, {session.codec}
          {session.bitrateKbps ? `, ${Math.round(session.bitrateKbps / 1000)} Mbps` : ''}
        </p>
      ) : (
        <p className="mt-3 text-[0.9375rem] leading-5 text-haze">
          Open Moonlight on the TV, pick TretioDLSS, then the TretioDLSS app. The picture starts when the TV connects.
          {st.clients.length === 0 && ' The first time, Moonlight shows a PIN to type in here.'}
          {st.address && (
            <span className="mt-1.5 block text-[0.8125rem] leading-[18px] text-dusk">
              Not listed in Moonlight? Add the PC by hand there: <span className="font-medium text-chalk tabular-nums">{st.address}</span>
            </span>
          )}
        </p>
      )}

    </section>
  );
}
