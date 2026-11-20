import { useEffect, type ReactNode } from 'react';
import { Monitor, MonitorUp, X } from 'lucide-react';
import moonlight from '../assets/moonlight.png';
import { Button } from './Button';

/** Full-window guide: how to start the picture from Moonlight on the TV. */
export function TvHelp({ address, onClose }: { address?: string | null; onClose: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className="intro-fade fixed inset-0 z-[105] flex flex-col bg-night px-9 pt-8 pb-7" role="dialog" aria-modal="true" aria-labelledby="tvhelp">
      <div className="flex items-start justify-between gap-6">
        <h1 id="tvhelp" className="font-display text-[2rem] leading-9 font-bold text-chalk">
          Start it from the TV
        </h1>
        <Button variant="ghost" icon={X} iconOnly aria-label="Close" onClick={onClose} />
      </div>

      <ol className="mt-8 grid grid-cols-2 content-start gap-4 min-[1000px]:grid-cols-4">
        <Step n={1} text="Open Moonlight on the TV.">
          <div className="flex flex-col items-center gap-2">
            <img src={moonlight} alt="" className="h-14 w-14" />
            <span className="text-[0.75rem] leading-4 text-haze">Moonlight</span>
          </div>
        </Step>
        <Step n={2} text="In the menu on the left, pick TretioDLSS.">
          <Launcher />
        </Step>
        <Step n={3} text="Type the PIN from the TV into TretioDLSS.">
          <div className="flex gap-1.5">
            {['4', '8', '2', '7'].map((d, i) => (
              <span key={i} className="flex h-11 w-9 items-center justify-center rounded-lg border border-edge-strong bg-boulevard font-display text-[1.25rem] font-bold text-chalk">
                {d}
              </span>
            ))}
          </div>
        </Step>
        <Step n={4} text="Pick the TretioDLSS cover. The picture starts.">
          <Launcher covers />
        </Step>
      </ol>

      <p className="mt-auto border-t border-edge pt-5 text-[0.875rem] leading-5 text-dusk">
        Not in Moonlight&apos;s menu? Add the PC there by hand
        {address ? (
          <>
            {': '}
            <span className="text-haze tabular-nums">{address}</span>
          </>
        ) : (
          '.'
        )}
      </p>
    </div>
  );
}

/** Zoomed parts of Moonlight's launcher on webOS: the side menu with the PCs, or the app covers. */
function Launcher({ covers }: { covers?: boolean }) {
  if (covers) {
    return (
      <div className="flex h-[112px] w-[210px] items-center gap-2.5 overflow-hidden rounded-lg bg-[#1f2329] px-3">
        <span className="h-[86px] w-[64px] shrink-0 rounded-[3px] bg-white/8" />
        <span className="flex h-[86px] w-[64px] shrink-0 flex-col items-center justify-center gap-1.5 rounded-[3px] bg-[#15191f] ring-[3px] ring-[#2196f3]">
          <svg viewBox="37 67 182 130" className="h-5 w-auto" aria-hidden>
            <rect x="44" y="74" width="120" height="86" rx="14" fill="none" stroke="#4d5565" strokeWidth="16" />
            <rect x="92" y="104" width="120" height="86" rx="14" fill="none" stroke="var(--color-flamingo)" strokeWidth="16" />
          </svg>
          <span className="text-[0.5625rem] leading-none font-bold text-white">TretioDLSS</span>
        </span>
        <span className="h-[86px] w-[64px] shrink-0 rounded-[3px] bg-white/8" />
      </div>
    );
  }
  return (
    <div className="flex h-[112px] w-[210px] flex-col overflow-hidden rounded-lg bg-[#272b31] text-left">
      <div className="flex items-center gap-2 px-3 pt-2 pb-1.5">
        <img src={moonlight} alt="" className="h-5 w-5" />
        <span className="text-[0.8125rem] leading-none text-white/90">Moonlight</span>
      </div>
      <Row icon={Monitor} label="DESKTOP-8UV7BF6" />
      <Row icon={Monitor} label="TretioDLSS" active />
      <Row icon={MonitorUp} label="Add computer" dim />
    </div>
  );
}

function Row({ icon: Icon, label, active, dim }: { icon: typeof Monitor; label: string; active?: boolean; dim?: boolean }) {
  return (
    <div className={`flex items-center gap-2 px-3 py-[5px] ${active ? 'bg-[#2196f3]' : ''}`}>
      <Icon size={13} className={`shrink-0 ${dim ? 'text-white/50' : 'text-white'}`} aria-hidden />
      <span className={`truncate text-[0.75rem] leading-4 ${dim ? 'text-white/50' : 'text-white'}`}>{label}</span>
    </div>
  );
}

function Step({ n, text, children }: { n: number; text: string; children: ReactNode }) {
  return (
    <li className="flex flex-col rounded-[22px] border border-edge bg-boulevard p-4">
      <div className="flex h-[128px] items-center justify-center rounded-2xl bg-night/60">{children}</div>
      <span className="mt-4 flex gap-2 px-1 text-[0.9375rem] leading-[22px] text-chalk">
        <span className="shrink-0 text-dusk tabular-nums">{n}.</span>
        <span>{text}</span>
      </span>
    </li>
  );
}
