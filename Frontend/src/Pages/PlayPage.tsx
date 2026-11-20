import {
  AlertTriangle,
  ChevronDown,
  GalleryHorizontalEnd,
  Info,
  Maximize2,
  Monitor,
  SlidersHorizontal,
  Video,
  XCircle,
  type LucideIcon,
} from 'lucide-react';
import { Button } from '../Components/Button';
import { Dropdown, type DropdownOption } from '../Components/Dropdown';
import { StartButton } from '../Components/StartButton';
import { StatTile } from '../Components/StatTile';
import { TvPanel } from '../Components/TvPanel';
import { useStore } from '../lib/store';
import {
  currentSourceName,
  currentSourceValue,
  engineOptions,
  formatDelay,
  formatHz,
  isTvOutput,
  outputChoices,
  outputName,
  sourceChoices,
  sourceDetail,
} from '../lib/options';
import { AppState } from '../Models/types';

export function PlayPage({ onOpenSettings }: { onOpenSettings: (section: string) => void }) {
  const { state, stats, preview, update, send } = useStore();
  if (!state) return null;
  const s = state.settings;
  const status = state.pipeline.status;
  const live = status === 'running';

  const upscaler = state.upscalers.find((e) => e.id === s.upscale.engine);
  const frameGen = state.frameGenerators.find((e) => e.id === s.frameGen.engine);
  const tvOut = isTvOutput(state);
  const sourceOk = sourceReady(state);

  const frameGenTitle = s.frameGen.engine === 'off' ? 'Off' : (frameGen?.name ?? s.frameGen.engine);

  const onSource = (v: string) => {
    if (v.startsWith('uvc:')) {
      const id = v.slice(4);
      const dev = state.devices.find((d) => d.id === id);
      update({ source: { kind: 'uvc', deviceId: id } });
      if (dev) send('ProbeSource', { deviceId: id });
    } else if (v.startsWith('window:')) {
      const [process, title] = v.slice(7).split('|');
      update({ source: { kind: 'window', window: { process, title } } });
    } else {
      update({ source: { kind: v as any } });
    }
  };

  const statusTag =
    status === 'running' ? 'Live' : status === 'starting' ? 'Starting' : status === 'waiting' ? 'Waiting' : status === 'faulted' ? 'Error' : 'Ready';

  return (
    <div className="mx-auto flex max-w-[1400px] flex-col gap-4 px-9 pt-1 pb-5">
      <section
        className={`relative isolate z-20 rounded-[28px] border border-edge bg-boulevard ${tvOut ? 'h-[344px]' : 'h-[438px]'}`}
        aria-label="Picture"
      >
        {/* Only the picture is clipped to the rounded corners, so menus from the tiles can extend past the hero. */}
        <div className="absolute inset-0 -z-10 overflow-hidden rounded-[27px]">
          {status === 'running' && preview ? (
            <img src={`data:image/jpeg;base64,${preview}`} alt="Live picture" className="absolute inset-0 h-full w-full object-cover" />
          ) : (
            <IdleBackdrop compact={tvOut} />
          )}
          <div className="hero-shade absolute inset-0" aria-hidden />
        </div>

        {/* With a TV picked, the TV panel below needs room, so the hero is shorter and the window does not scroll. */}
        <div className={`absolute left-9 w-[min(520px,60%)] ${tvOut ? 'top-8' : 'top-12'}`}>
          <h1 className="truncate pb-1.5 font-display text-[2.75rem] leading-[48px] font-bold text-chalk">{currentSourceName(state)}</h1>
          <div className="mt-1.5 flex flex-wrap gap-2">
            <Tag>
              <span
                className={`h-[7px] w-[7px] shrink-0 rounded-full ${status === 'running' ? 'live-pulse bg-flamingo' : status === 'faulted' ? 'bg-error' : status === 'waiting' ? 'bg-warning' : 'bg-dusk'}`}
                aria-hidden
              />
              {statusTag}
            </Tag>
            <Tag>{live && stats.sourceDetail ? stats.sourceDetail : sourceDetail(state)}</Tag>
            <Tag>{outputSummary(state)}</Tag>
          </div>
          <div className="mt-6 flex items-center gap-3">
            <StartButton
              status={status}
              canStart={sourceOk}
              onStart={() => send('StartPipeline')}
              onStop={() => send('StopPipeline')}
            />
            <button
              type="button"
              onClick={() => onOpenSettings('source')}
              className="frost-light inline-flex h-13 items-center gap-2 rounded-full px-6 text-[1rem] leading-none font-medium text-chalk transition-colors duration-200 hover:bg-white/16"
            >
              <SlidersHorizontal size={18} className="shrink-0" aria-hidden />
              Tune
            </button>
          </div>
          <p className="mt-3 text-[0.8125rem] leading-[18px] text-dusk">
            {live
              ? `Stop with ${s.hotkeys.toggleRun}, leave fullscreen with ${s.hotkeys.exitFullscreen}`
              : sourceOk
                ? tvOut
                  ? 'The picture goes to the TV through Moonlight'
                  : 'The picture opens on the output display'
                : 'Pick a source below to start'}
          </p>
        </div>

        <div className="absolute right-6 bottom-6 left-6 grid grid-cols-2 gap-2.5 min-[1000px]:grid-cols-4">
          <StageTile
            icon={Video}
            label="Source"
            value={currentSourceName(state)}
            ariaLabel="Source"
            disabled={live}
            current={currentSourceValue(state)}
            options={sourceChoices(state)}
            onChange={onSource}
          />
          <StageTile
            icon={Maximize2}
            label="Upscale"
            value={upscaler?.name ?? 'Off'}
            ariaLabel="Upscaler"
            current={s.upscale.engine}
            options={engineOptions(state.upscalers, (id) => send('InstallComponent', { id }))}
            onChange={(engine) => update({ upscale: { engine } })}
          />
          <StageTile
            icon={GalleryHorizontalEnd}
            label="Frame gen"
            value={frameGenTitle}
            badge={s.frameGen.engine === 'off' ? undefined : `x${s.frameGen.multiplier}`}
            ariaLabel="Frame generation"
            current={s.frameGen.engine}
            options={engineOptions(
              state.frameGenerators,
              (id) => send('InstallComponent', { id }),
              { engine: s.frameGen.engine, multiplier: s.frameGen.multiplier, pick: (engine, multiplier) => send('SetFrameGenEngine', { engine, multiplier }) },
            )}
            onChange={(engine) => send('SetFrameGenEngine', { engine })}
          />
          <StageTile
            icon={Monitor}
            label="Output"
            value={outputName(state)}
            ariaLabel="Output display"
            disabled={live}
            current={s.display.monitorId}
            options={outputChoices(state)}
            align="right"
            onChange={(monitorId) => update({ display: { monitorId } })}
          />
        </div>
      </section>

      {(state.pipeline.status === 'faulted' || (state.pipeline.status === 'waiting' && !tvOut)) && state.pipeline.message && (
        <Notice level={state.pipeline.status === 'faulted' ? 'error' : 'warning'} text={state.pipeline.message} />
      )}
      {tvOut && <TvPanel />}
      {state.hints.filter((h) => !(tvOut && h.id === 'tvwait' && h.level === 'info')).map((h) => (
        <Notice
          key={h.id}
          level={h.level}
          text={h.text}
          action={h.action ? { label: h.action.label, run: () => send(h.action!.method, h.action!.parameters) } : undefined}
        />
      ))}

      {status === 'running' && <LiveStats />}
    </div>
  );
}

/** Shown in the hero while nothing runs: the app icon, large and quiet. */
function IdleBackdrop({ compact }: { compact?: boolean }) {
  return (
    <div className={`absolute inset-0 flex items-center justify-end pr-[8%] ${compact ? 'pb-20' : ''}`} aria-hidden>
      <svg viewBox="0 0 256 200" className="h-[62%] text-white/[0.05]">
        {/* The gray frame is cut away under the coral one, so the coral frame reads as lying on top. */}
        <mask id="idle-front-cut" maskUnits="userSpaceOnUse" x="0" y="0" width="256" height="200">
          <rect width="256" height="200" fill="white" />
          <rect x="86" y="70" width="150" height="110" rx="18" fill="none" stroke="black" strokeWidth="14" />
        </mask>
        <rect x="20" y="20" width="150" height="110" rx="18" fill="none" stroke="currentColor" strokeWidth="14" mask="url(#idle-front-cut)" />
        <rect x="86" y="70" width="150" height="110" rx="18" fill="none" stroke="var(--color-flamingo)" strokeOpacity="0.16" strokeWidth="14" />
      </svg>
    </div>
  );
}

function Tag({ children }: { children: React.ReactNode }) {
  return (
    <span className="frost-light inline-flex h-7 max-w-full items-center gap-1.5 rounded-full px-3 text-[0.8125rem] leading-none text-chalk">
      <span className="flex min-w-0 items-center gap-1.5 truncate leading-[18px]">{children}</span>
    </span>
  );
}

function StageTile<T extends string>({
  icon: Icon,
  label,
  value,
  badge,
  ariaLabel,
  disabled,
  current,
  options,
  align,
  onChange,
}: {
  icon: LucideIcon;
  label: string;
  value: string;
  /** Short value that must never be cut off, shown after the truncated title. */
  badge?: string;
  ariaLabel: string;
  disabled?: boolean;
  current: T;
  options: DropdownOption<T>[];
  align?: 'left' | 'right';
  onChange: (v: T) => void;
}) {
  return (
    <Dropdown
      ariaLabel={ariaLabel}
      value={current}
      options={options}
      onChange={onChange}
      disabled={disabled}
      align={align}
      triggerClassName="frost flex h-16 items-center gap-3 rounded-[18px] border border-edge px-4 transition-colors duration-200 enabled:hover:border-edge-strong"
      renderTrigger={(open) => (
        <>
          <Icon size={20} className="shrink-0 text-dusk" aria-hidden />
          <span className="min-w-0 flex-1">
            <span className="block text-[0.75rem] leading-4 font-medium text-dusk">{label}</span>
            <span className="flex min-w-0 items-center gap-1.5">
              <span className="truncate text-[0.9375rem] leading-5 font-semibold text-chalk">{value}</span>
              {badge && (
                <span className="shrink-0 rounded-full bg-chalk px-1.5 text-[0.75rem] leading-[18px] font-bold text-night tabular-nums">{badge}</span>
              )}
            </span>
          </span>
          {!disabled && (
            <ChevronDown size={16} className={`shrink-0 text-dusk transition-transform duration-200 ${open ? 'rotate-180' : ''}`} aria-hidden />
          )}
        </>
      )}
    />
  );
}

function LiveStats() {
  const { stats } = useStore();
  return (
    <section className="panel-in grid grid-cols-2 gap-3.5 min-[900px]:grid-cols-5" aria-label="Live numbers">
      <StatTile label="Real" value={fmt(stats.realFps)} unit="fps" spark={stats.history.real} sparkColor="var(--color-lagoon)" />
      <StatTile
        label="Output"
        value={fmt(stats.outputFps)}
        unit="fps"
        estimated={stats.outputEstimated}
        spark={stats.history.generated}
        sparkColor="var(--color-tangerine)"
      />
      <StatTile
        label="Added delay"
        value={stats.delayNotMeasured ? '--' : formatDelay(stats.addedDelayMs)}
        unit="ms"
        estimated={stats.delayNotMeasured}
      />
      <StatTile label="GPU" value={fmt(stats.gpuPercent)} unit="%" warning={stats.gpuPercent > 85} />
      <StatTile label="Dropped" value={String(stats.dropped)} warning={stats.dropped > 0} />
    </section>
  );
}

function Notice({
  level,
  text,
  action,
}: {
  level: 'info' | 'warning' | 'error';
  text: string;
  action?: { label: string; run: () => void };
}) {
  const Icon = level === 'error' ? XCircle : level === 'warning' ? AlertTriangle : Info;
  const color = level === 'error' ? 'text-error' : level === 'warning' ? 'text-warning' : 'text-ocean-hi';
  const fill = level === 'error' ? 'bg-error/10' : level === 'warning' ? 'bg-warning/10' : 'frost';
  return (
    <div className={`flex items-center gap-3 rounded-2xl border border-edge px-4 py-3 ${fill}`} role="status">
      <Icon size={18} className={`shrink-0 ${color}`} aria-hidden />
      <span className="flex-1 text-[0.9375rem] leading-5 text-haze">{text}</span>
      {action && (
        <Button size="sm" onClick={action.run}>
          {action.label}
        </Button>
      )}
    </div>
  );
}

function fmt(n: number) {
  return Number.isFinite(n) ? String(Math.round(n)) : '--';
}

function sourceReady(state: AppState) {
  const s = state.settings.source;
  if (s.kind === 'uvc') return state.devices.some((d) => d.id === s.deviceId);
  if (s.kind === 'window') return !!s.window.title;
  if (s.kind === 'file') return !!s.file.path;
  if (s.kind === 'raw') return !!s.raw.path;
  return true;
}

function outputSummary(state: AppState) {
  const s = state.settings;
  if (isTvOutput(state)) {
    const ss = state.stream.session;
    return ss?.width ? `${outputName(state)}, ${ss.width} x ${ss.height}, ${ss.fps} fps` : `${outputName(state)}, through Moonlight`;
  }
  const m = state.monitors.find((x) => x.id === s.display.monitorId) ?? state.monitors.find((x) => x.primary);
  if (!m) return 'No display';
  const sync =
    s.display.sync === 'auto' ? (m.vrr ? 'VRR' : 'V-Sync') : s.display.sync === 'vsync' ? 'V-Sync' : s.display.sync === 'vrr' ? 'VRR' : 'Tearing';
  return `${m.name}, ${formatHz(m.refresh)} Hz, ${sync}`;
}
