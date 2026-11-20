import { useEffect, useState } from 'react';
import { CircleHelp, Tv } from 'lucide-react';
import { useStore } from '../lib/store';
import { outputName } from '../lib/options';

/** TV output: pairing, waiting for Moonlight, and what is streaming now. */
export function TvPanel() {
  const { state } = useStore();
  const waiting = state?.pipeline.status === 'waiting' && !state?.stream.session;
  const [slow, setSlow] = useState(false);

  // The manual address is only for when Moonlight does not find the PC: after a while of waiting, before any pairing.
  useEffect(() => {
    setSlow(false);
    if (!waiting) return;
    const t = setTimeout(() => setSlow(true), 15000);
    return () => clearTimeout(t);
  }, [waiting]);

  if (!state) return null;
  const st = state.stream;
  const tv = outputName(state);
  const session = st.session;

  return (
    <>
      <section className="frost rounded-[22px] border border-edge px-5 py-3.5" aria-label="TV stream">
        <div className="flex items-center gap-3">
          <Tv size={20} className="shrink-0 text-dusk" aria-hidden />
          <h2 className="flex-1 text-[1rem] leading-6 font-semibold text-chalk">{tv}</h2>
          <button
            type="button"
            aria-label="How to start it on the TV"
            title="How to start it on the TV"
            onClick={() => window.dispatchEvent(new Event('tretio:tvhelp'))}
            className="flex h-8 w-8 items-center justify-center rounded-full text-dusk transition-colors duration-150 hover:bg-white/8 hover:text-chalk"
          >
            <CircleHelp size={18} aria-hidden />
          </button>
        </div>

        {st.error ? (
          <p className="mt-3 text-[0.9375rem] leading-5 text-error">The TV stream could not start: {st.error}</p>
        ) : session?.width ? (
          <p className="mt-3 text-[0.9375rem] leading-5 text-haze tabular-nums">
            Streaming to {session.client}: {session.width} x {session.height}, {session.fps} fps, {session.codec}
            {session.bitrateKbps ? `, ${Math.round(session.bitrateKbps / 1000)} Mbps` : ''}
          </p>
        ) : (
          <p className="mt-3 text-[0.9375rem] leading-5 text-dusk">
            Open Moonlight on the TV. In the menu on the left, pick the screen icon named TretioDLSS, then start the TretioDLSS app.
            {st.clients.length === 0 && ' The first time, Moonlight shows a PIN to type in here.'}
            {slow && st.clients.length === 0 && st.address && (
              <span className="mt-1.5 block text-[0.8125rem] leading-[18px] text-dusk">
                Not listed in Moonlight? Add the PC by hand there: <span className="font-medium text-chalk tabular-nums">{st.address}</span>
              </span>
            )}
          </p>
        )}
      </section>
    </>
  );
}
