import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, ChevronDown } from 'lucide-react';

/** Top-right of the picture card when the card delivers only black; a click shows what to check. */
export function NoPictureBadge({ className = '' }: { className?: string }) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => !root.current?.contains(e.target as Node) && setOpen(false);
    window.addEventListener('mousedown', close);
    return () => window.removeEventListener('mousedown', close);
  }, [open]);

  const pill = 'flex h-9 items-center gap-2 rounded-full border border-warning/30 bg-warning/12 px-3.5 text-[0.875rem] leading-none font-medium text-warning';

  return (
    <div ref={root} className={`z-30 flex flex-col items-end ${className}`}>
      <button type="button" aria-expanded={open} onClick={() => setOpen((o) => !o)} className={`${pill} transition-colors hover:bg-warning/18`}>
        <AlertTriangle size={16} className="shrink-0" aria-hidden />
        No picture
        <ChevronDown size={15} className={`shrink-0 transition-transform duration-200 ${open ? 'rotate-180' : ''}`} aria-hidden />
      </button>
      {open && (
        <div className="menu-rise mt-2 w-[300px] rounded-2xl border border-edge bg-boulevard p-4 text-left">
          <p className="text-[0.875rem] leading-5 font-semibold text-chalk">The capture card sends only black</p>
          <ul className="mt-2 flex flex-col gap-1.5 text-[0.8125rem] leading-[18px] text-haze">
            <li>Check the HDMI cable from the console to the card.</li>
            <li>On a PS5, turn off HDCP under Settings, System, HDMI.</li>
            <li>Make sure the console is on and not in rest mode.</li>
          </ul>
        </div>
      )}
    </div>
  );
}
