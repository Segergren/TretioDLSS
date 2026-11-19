import { DropdownOption } from '../Components/Dropdown';
import { AppState, CaptureFormat, EngineInfo, MonitorInfo } from '../Models/types';

/** Multiplier buttons in each frame generation row: the current engine and multiplier, and how to pick one. */
export interface MultiplierPick {
  engine: string;
  multiplier: number;
  pick: (engine: string, multiplier: number) => void;
}

export function engineOptions(engines: EngineInfo[], onInstall?: (id: string) => void, multiplier?: MultiplierPick): DropdownOption<any>[] {
  return engines.map((e) => {
    const opt: DropdownOption<any> = {
      value: e.id,
      label: e.name,
      disabled: !e.available,
      reason: e.available ? undefined : (e.fixHint ?? e.reason),
    };
    if (multiplier && e.available && e.multipliers && e.multipliers.length > 1) {
      opt.choices = e.multipliers.map((m) => ({
        label: `x${m}`,
        selected: multiplier.engine === e.id && multiplier.multiplier === m,
        run: () => multiplier.pick(e.id, m),
      }));
    }
    const inst = e.install;
    if (!e.available && inst && inst.state !== 'installed' && onInstall) {
      const size = formatSize(inst.bytes);
      if (inst.state === 'downloading') {
        opt.reason = `Downloading, ${Math.round(inst.progress * 100)}% of ${size}`;
        opt.action = { label: 'Downloading', busy: true, progress: inst.progress, run: () => {} };
      } else if (inst.state === 'failed') {
        opt.reason = inst.error ?? 'Download failed';
        opt.action = { label: 'Retry', run: () => onInstall(e.id) };
      } else {
        opt.reason = `${size} from NVIDIA`;
        opt.action = { label: 'Download', run: () => onInstall(e.id) };
      }
    }
    return opt;
  });
}

export function formatSize(bytes: number): string {
  return bytes >= 1e9 ? `${(bytes / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(bytes / 1e6))} MB`;
}

export function formatLabel(f: CaptureFormat): string {
  return `${f.width} x ${f.height}, ${formatHz(f.fps)} Hz, ${f.subtype}`;
}

export function formatHz(hz: number): string {
  return Number.isInteger(hz) ? String(hz) : hz.toFixed(2);
}

export function monitorLabel(m: MonitorInfo): string {
  return m.name;
}

export function monitorDetail(m: MonitorInfo, withPrimary = true): string {
  const parts = [`${m.width} x ${m.height}`, `${formatHz(m.refresh)} Hz`];
  if (m.vrr) parts.push('VRR');
  if (m.hdr) parts.push('HDR');
  if (m.tv) parts.push('TV');
  if (m.primary && withPrimary) parts.push('primary');
  return parts.join(', ');
}


export interface SourceChoice {
  value: string;
  label: string;
  detail: string;
}

/** Every pickable source as one flat list: capture devices, windows, test pattern, file. */
export function sourceChoices(state: AppState): SourceChoice[] {
  const list: SourceChoice[] = [];
  for (const d of state.devices) {
    list.push({ value: `uvc:${d.id}`, label: d.name, detail: 'Capture card' });
  }
  const seen = new Set<string>();
  for (const w of state.windows) {
    const value = `window:${w.process}|${w.title}`;
    if (seen.has(value)) continue;
    seen.add(value);
    list.push({ value, label: w.title, detail: `Window, ${w.process}` });
  }
  list.push({ value: 'pattern', label: 'Test pattern', detail: 'Generated, for checks without a console' });
  list.push({ value: 'file', label: 'Video file', detail: 'Plays a local clip as if it were live' });
  return list;
}

export function currentSourceValue(state: AppState): string {
  const s = state.settings.source;
  switch (s.kind) {
    case 'uvc':
      return `uvc:${s.deviceId}`;
    case 'window':
      return `window:${s.window.process}|${s.window.title}`;
    case 'raw':
      return 'raw';
    default:
      return s.kind;
  }
}

export function currentSourceName(state: AppState): string {
  const s = state.settings.source;
  switch (s.kind) {
    case 'uvc':
      return state.devices.find((d) => d.id === s.deviceId)?.name ?? 'Capture card not found';
    case 'window':
      return s.window.title || 'No window picked';
    case 'pattern':
      return 'Test pattern';
    case 'file':
      return s.file.path ? (s.file.path.split(/[\\/]/).pop() ?? 'Video file') : 'Video file';
    case 'raw':
      return 'Raw recording';
  }
}

export function sourceDetail(state: AppState): string {
  const s = state.settings.source;
  switch (s.kind) {
    case 'uvc':
      return `${s.width} x ${s.height}, ${formatHz(s.fps)} Hz, ${s.subtype}`;
    case 'window':
      return s.window.process ? `Window, ${s.window.process}` : 'Pick a window in Settings';
    case 'pattern':
      return `${s.pattern.cadence.replace('in', ' fps in ')} Hz, ${s.pattern.layout.toUpperCase()}`;
    case 'file':
      return s.file.path ? `Looped, in a ${s.file.container} Hz container` : 'Pick a file in Settings';
    case 'raw':
      return s.raw.path || 'Pick a recording in Settings';
  }
}

/** Delay in ms: one decimal below 10 ms, so a small delay never reads as zero. */
export function formatDelay(ms: number): string {
  if (!Number.isFinite(ms)) return '--';
  return ms < 9.95 ? ms.toFixed(1) : String(Math.round(ms));
}
