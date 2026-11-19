import { AlertTriangle, CheckCircle2, Info, X, XCircle } from 'lucide-react';
import { useStore } from '../lib/store';

const icons = { info: Info, warning: AlertTriangle, error: XCircle, success: CheckCircle2 };
const colors = { info: 'text-ocean-hi', warning: 'text-warning', error: 'text-error', success: 'text-success' };

export function Toasts() {
  const { toasts, dismissToast } = useStore();
  return (
    <div className="pointer-events-none fixed right-6 bottom-6 z-50 flex w-[22rem] flex-col gap-2" aria-live="polite">
      {toasts.map((t) => {
        const Icon = icons[t.level];
        return (
          <div
            key={t.id}
            className="panel-in pointer-events-auto flex items-start gap-3 rounded-2xl border border-edge bg-boulevard p-3"
          >
            <Icon size={18} className={`mt-px shrink-0 ${colors[t.level]}`} aria-hidden />
            <div className="min-w-0 flex-1 text-[0.9375rem] leading-5 text-haze">{t.text}</div>
            <button
              type="button"
              aria-label="Dismiss"
              onClick={() => dismissToast(t.id)}
              className="-m-1 flex h-7 w-7 shrink-0 items-center justify-center rounded-full text-dusk hover:bg-white/8 hover:text-haze"
            >
              <X size={16} aria-hidden />
            </button>
          </div>
        );
      })}
    </div>
  );
}
