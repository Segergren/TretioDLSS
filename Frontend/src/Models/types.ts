export type SourceKind = 'uvc' | 'window' | 'file' | 'pattern' | 'raw';
export type CaptureApi = 'auto' | 'mf' | 'dshow';
export type ColorMatrix = 'auto' | 'bt601' | 'bt709';
export type ColorRange = 'auto' | 'limited' | 'full';
export type CadenceMode = 'auto' | 'off' | 'fixed';
export type UpscaleEngineId = 'off' | 'bilinear' | 'bicubic' | 'fsr1' | 'nis' | 'vfx-vsr';
export type UpscaleSize = 'display' | 'supersample' | 'source' | 'integer' | 'custom';
export type FrameGenEngineId = 'off' | 'blend' | 'vfx-vfg' | 'dlss-fg' | 'rife' | 'amd-frc';
export type StageOrder = 'auto' | 'upscale-first' | 'framegen-first';
export type DisplayMode = 'borderless' | 'windowed';
export type SyncMode = 'auto' | 'vrr' | 'tearing' | 'vsync';
export type PacingMode = 'auto' | 'immediate' | 'clocked';
export type TvMode = 'off' | 'display' | 'sunshine';

export interface SourceSettings {
  kind: SourceKind;
  api: CaptureApi;
  deviceId: string;
  width: number;
  height: number;
  fps: number;
  subtype: string;
  colorMatrix: ColorMatrix;
  colorRange: ColorRange;
  window: { process: string; title: string };
  file: { path: string; container: number };
  raw: { path: string };
  pattern: { cadence: string; layout: string };
}

export interface Settings {
  source: SourceSettings;
  cadence: { mode: CadenceMode; fixedFps: number };
  upscale: { engine: UpscaleEngineId; size: UpscaleSize; sharpness: number; options: Record<string, unknown> };
  frameGen: {
    engine: FrameGenEngineId;
    multiplier: number;
    model: string;
    version: string;
    stageOrder: StageOrder;
    options: Record<string, unknown>;
  };
  display: { monitorId: string; mode: DisplayMode; sync: SyncMode; pacing: PacingMode; overlay: boolean; windowTitle: string; hdr: HdrSettings };
  tv: { mode: TvMode };
  audio: { inputId: string; outputId: string; volume: number; offsetMs: number; autoSync: boolean };
  hotkeys: {
    toggleRun: string;
    toggleFrameGen: string;
    toggleOverlay: string;
    compareUpscaler: string;
    exitFullscreen: string;
  };
  advanced: { adapter: string; logLevel: string; vfxRuntimePath: string };
  ui: { closeToTray: boolean };
  discord: { enabled: boolean; game: string; startTime: string };
  activeProfile: string;
}

export interface EngineOption {
  key: string;
  label: string;
  values: { value: string; label: string }[];
}

export interface EngineInfo {
  id: string;
  name: string;
  available: boolean;
  reason?: string;
  fixHint?: string;
  multipliers?: number[];
  options?: EngineOption[];
  install?: InstallState | null;
}

/** A download the app can do itself, for engines whose files are not on this PC yet. */
export interface InstallState {
  state: 'none' | 'downloading' | 'failed' | 'installed';
  progress: number;
  bytes: number;
  error?: string | null;
}

export interface CaptureFormat {
  width: number;
  height: number;
  fps: number;
  subtype: string;
}

export interface CaptureDevice {
  id: string;
  name: string;
  formats: CaptureFormat[];
  audioEndpointId?: string;
}

export interface CaptureWindow {
  hwnd: string;
  title: string;
  process: string;
}

export interface MonitorInfo {
  id: string;
  name: string;
  width: number;
  height: number;
  refresh: number;
  vrr: boolean;
  hdr: boolean;
  primary: boolean;
  tv: boolean;
  adapter: string;
}

export interface AudioDevice {
  id: string;
  name: string;
  isDefault?: boolean;
}

export interface Profile {
  name: string;
  builtIn: boolean;
  summary: string;
}

export type PipelineStatus = 'idle' | 'starting' | 'running' | 'stopping' | 'waiting' | 'faulted';

export interface PipelineState {
  status: PipelineStatus;
  message?: string;
}

export interface AppState {
  version: string;
  settings: Settings;
  profiles: Profile[];
  devices: CaptureDevice[];
  windows: CaptureWindow[];
  monitors: MonitorInfo[];
  audioInputs: AudioDevice[];
  audioOutputs: AudioDevice[];
  upscalers: EngineInfo[];
  frameGenerators: EngineInfo[];
  pipeline: PipelineState;
  gpuName: string;
  update?: { status: string; ready: string | null };
  discordStatus?: string;
  hints: Hint[];
}

export interface HdrSettings {
  mode: 'auto' | 'off';
  paperWhite: number;
  expand: number;
  brightness: number;
  contrast: number;
  saturation: number;
}

export interface Hint {
  id: string;
  level: 'info' | 'warning' | 'error';
  text: string;
  action?: { label: string; method: string; parameters?: unknown } | null;
}

export interface Stats {
  sourceFps: number;
  realFps: number;
  outputFps: number;
  outputEstimated: boolean;
  addedDelayMs: number;
  delayNotMeasured: boolean;
  gpuPercent: number;
  dropped: number;
  late: number;
  cadence: string;
  containerHz: number;
  sourceDetail: string;
  upscaleDetail: string;
  frameGenDetail: string;
  outputDetail: string;
  history: { real: number[]; generated: number[] };
}

export const emptyStats: Stats = {
  sourceFps: 0,
  realFps: 0,
  outputFps: 0,
  outputEstimated: false,
  addedDelayMs: 0,
  delayNotMeasured: false,
  gpuPercent: 0,
  dropped: 0,
  late: 0,
  cadence: '',
  containerHz: 0,
  sourceDetail: '',
  upscaleDetail: '',
  frameGenDetail: '',
  outputDetail: '',
  history: { real: [], generated: [] },
};
