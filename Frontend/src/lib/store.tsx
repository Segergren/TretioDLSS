import { createContext, ReactNode, useCallback, useContext, useEffect, useState } from 'react';
import { AppState, Stats, emptyStats, Settings } from '../Models/types';
import { onConnection, onMessage, send, startBridge } from './bridge';

export interface Toast {
  id: number;
  level: 'info' | 'warning' | 'error' | 'success';
  text: string;
}

export interface CapabilityRow {
  group: string;
  name: string;
  value: string;
  ok: boolean;
}

export interface Capabilities {
  rows: CapabilityRow[];
}

const CAPS_KEY = 'tretio.capabilities';

function cachedCapabilities(): Capabilities | null {
  try {
    const raw = localStorage.getItem(CAPS_KEY);
    return raw ? (JSON.parse(raw) as Capabilities) : null;
  } catch {
    return null;
  }
}

interface Store {
  state: AppState | null;
  /** Machine capabilities: fetched when the bridge connects, cached between launches. */
  capabilities: Capabilities | null;
  capabilitiesChecking: boolean;
  refreshCapabilities: () => void;
  stats: Stats;
  preview: string | null;
  connected: boolean;
  toasts: Toast[];
  update: (patch: DeepPartial<Settings>) => void;
  send: typeof send;
  dismissToast: (id: number) => void;
}

export type DeepPartial<T> = { [K in keyof T]?: T[K] extends object ? DeepPartial<T[K]> : T[K] };

const StoreContext = createContext<Store | null>(null);

function merge<T>(base: T, patch: DeepPartial<T>): T {
  const out: any = Array.isArray(base) ? [...(base as any)] : { ...(base as any) };
  for (const [k, v] of Object.entries(patch as any)) {
    if (v && typeof v === 'object' && !Array.isArray(v) && typeof out[k] === 'object' && out[k] !== null) {
      out[k] = merge(out[k], v as any);
    } else {
      out[k] = v;
    }
  }
  return out;
}

let toastId = 0;

function windowActive() {
  return document.visibilityState === 'visible' && document.hasFocus();
}

export function StoreProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<AppState | null>(null);
  const [stats, setStats] = useState<Stats>(emptyStats);
  const [preview, setPreview] = useState<string | null>(null);
  const [connected, setConnected] = useState(false);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [capabilities, setCapabilities] = useState<Capabilities | null>(cachedCapabilities);
  const [capabilitiesChecking, setCapabilitiesChecking] = useState(false);

  const refreshCapabilities = useCallback(() => {
    setCapabilitiesChecking(true);
    send('GetCapabilities');
  }, []);

  const dismissToast = useCallback((id: number) => {
    setToasts((t) => t.filter((x) => x.id !== id));
  }, []);

  useEffect(() => {
    let pendingPreview: string | null = null;
    let previewScheduled = false;
    // The picture shows only while the window is in front.
    let last = windowActive();
    const offConn = onConnection((open) => {
      setConnected(open);
      if (open) {
        setCapabilitiesChecking(true);
        send('GetCapabilities');
        send('SetPreviewActive', { active: windowActive() });
      }
    });
    const offMsg = onMessage((method, content) => {
      switch (method) {
        case 'State':
          setState(content);
          if (content?.pipeline?.status !== 'running') setPreview(null);
          break;
        case 'Stats':
          setStats(content);
          break;
        case 'Preview':
          if (!last) break;
          // Show only the newest frame, at most once per screen refresh: rendering every frame in
          // order lets a backlog build up and the picture drifts into slow motion.
          pendingPreview = content;
          if (!previewScheduled) {
            previewScheduled = true;
            requestAnimationFrame(() => {
              previewScheduled = false;
              setPreview(pendingPreview);
            });
          }
          break;
        case 'Capabilities': {
          const caps: Capabilities = { rows: content.rows ?? [] };
          setCapabilities(caps);
          setCapabilitiesChecking(false);
          try {
            localStorage.setItem(CAPS_KEY, JSON.stringify(caps));
          } catch {
            // A missing cache only means the next launch shows the spinner once.
          }
          break;
        }
        case 'Toast': {
          const id = ++toastId;
          setToasts((t) => [...t.slice(-2), { id, level: content.level ?? 'info', text: content.text }]);
          setTimeout(() => setToasts((t) => t.filter((x) => x.id !== id)), 5000);
          break;
        }
      }
    });
    const onFocusChange = () => {
      const now = windowActive();
      if (now === last) return;
      last = now;
      if (!now) {
        pendingPreview = null;
        setPreview(null);
      }
      send('SetPreviewActive', { active: now });
    };
    window.addEventListener('focus', onFocusChange);
    window.addEventListener('blur', onFocusChange);
    document.addEventListener('visibilitychange', onFocusChange);
    startBridge();
    return () => {
      offConn();
      offMsg();
      window.removeEventListener('focus', onFocusChange);
      window.removeEventListener('blur', onFocusChange);
      document.removeEventListener('visibilitychange', onFocusChange);
    };
  }, []);

  const update = useCallback((patch: DeepPartial<Settings>) => {
    setState((s) => (s ? { ...s, settings: merge(s.settings, patch) } : s));
    send('UpdateSettings', patch);
  }, []);

  return (
    <StoreContext.Provider
      value={{ state, capabilities, capabilitiesChecking, refreshCapabilities, stats, preview, connected, toasts, update, send, dismissToast }}
    >
      {children}
    </StoreContext.Provider>
  );
}

export function useStore() {
  const ctx = useContext(StoreContext);
  if (!ctx) throw new Error('useStore outside StoreProvider');
  return ctx;
}
