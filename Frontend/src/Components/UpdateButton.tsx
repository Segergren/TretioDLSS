import { useState } from 'react';
import { Download } from 'lucide-react';
import { useStore } from '../lib/store';

/** Top bar: a downloaded update, installed and restarted with one click. */
export function UpdateButton() {
  const { state, send } = useStore();
  const [installing, setInstalling] = useState(false);
  const ready = state?.update?.ready;
  if (!ready) return null;
  return (
    <button
      type="button"
      title={installing ? 'Installing' : `Install version ${ready} and restart`}
      aria-label={`Install update ${ready}`}
      disabled={installing}
      onClick={() => {
        setInstalling(true);
        send('InstallUpdate');
      }}
      className="flex h-11 w-11 items-center justify-center rounded-full border border-success/40 bg-success/12 text-success transition-colors duration-200 enabled:hover:bg-success/20 disabled:opacity-60"
    >
      <Download size={18} className={installing ? 'animate-pulse' : ''} aria-hidden />
    </button>
  );
}
