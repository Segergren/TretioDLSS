import { Fragment, useEffect } from 'react';
import { Button } from './Button';
import { useStore } from '../lib/store';

/** Shown once after an update: what changed in each release since the last version that ran. */
export function ReleaseNotesDialog() {
  const { state, send } = useStore();
  const notes = state?.releaseNotes ?? [];
  const close = () => send('DismissReleaseNotes');

  useEffect(() => {
    if (notes.length === 0) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && close();
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [notes.length]); // eslint-disable-line react-hooks/exhaustive-deps

  if (notes.length === 0) return null;
  return (
    <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/60 p-6" onMouseDown={(e) => e.target === e.currentTarget && close()}>
      <div role="dialog" aria-modal="true" aria-labelledby="whatsnew" className="menu-rise flex max-h-full w-[min(560px,100%)] flex-col rounded-[24px] border border-edge bg-boulevard">
        <h2 id="whatsnew" className="px-7 pt-6 font-display text-[1.75rem] leading-9 font-bold text-chalk">
          What's new
        </h2>
        <div className="min-h-0 flex-1 overflow-y-auto px-7 pt-2 pb-2">
          {notes.map((n) => (
            <section key={n.version} className="mt-4 first:mt-2">
              <h3 className="eyebrow mb-2 leading-4">Version {n.version}</h3>
              <ul className="flex flex-col gap-2">
                {bullets(n.notes).map((b, i) => (
                  <li key={i} className="flex gap-2.5 text-[0.9375rem] leading-[22px] text-haze">
                    <span className="mt-[9px] h-1.5 w-1.5 shrink-0 rounded-full bg-flamingo" aria-hidden />
                    <span>{bold(b)}</span>
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
        <div className="flex justify-end px-7 pt-3 pb-6">
          <Button autoFocus onClick={close}>
            Close
          </Button>
        </div>
      </div>
    </div>
  );
}

/** The CI writes one Markdown bullet per change; anything else becomes a plain line. */
function bullets(md: string): string[] {
  return md
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0 && !l.startsWith('#'))
    .map((l) => l.replace(/^[*-]\s+/, ''));
}

function bold(text: string) {
  return text.split(/\*\*(.+?)\*\*/g).map((part, i) =>
    i % 2 === 1 ? (
      <strong key={i} className="font-semibold text-chalk">
        {part}
      </strong>
    ) : (
      <Fragment key={i}>{part}</Fragment>
    ),
  );
}
