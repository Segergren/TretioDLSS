// Mock backend: serves the WebSocket the frontend expects and starts Vite.
// Usage: npm run dev:mock [-- --scenario=<name>] then open http://localhost:2992/?token=dev
import { WebSocketServer } from 'ws';
import { spawn } from 'node:child_process';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const scenario = (process.argv.find((a) => a.startsWith('--scenario=')) ?? '--scenario=default').split('=')[1];
const previewPath = path.join(here, 'preview.jpg');
const preview = existsSync(previewPath) ? readFileSync(previewPath).toString('base64') : null;

// Real gameplay for the live picture: JPEG frames in TestMedia/mock-frames (see TestMedia/README.md),
// or another folder with --frames=<dir>. Falls back to the drawn preview.jpg.
const framesArg = process.argv.find((a) => a.startsWith('--frames='));
const framesDir = framesArg ? framesArg.split('=')[1] : path.join(here, '..', '..', 'TestMedia', 'mock-frames');
const frames = existsSync(framesDir)
  ? readdirSync(framesDir)
      .filter((f) => f.endsWith('.jpg'))
      .sort()
      .map((f) => readFileSync(path.join(framesDir, f)).toString('base64'))
  : [];
if (frames.length) console.log(`Live picture: ${frames.length} frames from ${framesDir}`);

const settings = {
  source: {
    kind: 'uvc',
    api: 'auto',
    deviceId: 'gm29f',
    width: 1920,
    height: 1080,
    fps: 60,
    subtype: 'NV12',
    colorMatrix: 'auto',
    colorRange: 'auto',
    window: { process: '', title: '' },
    file: { path: '', container: 60 },
    raw: { path: '' },
    pattern: { cadence: '30in60', layout: 'nv12' },
  },
  cadence: { mode: 'auto', fixedFps: 30 },
  upscale: { engine: 'vfx-vsr', size: 'display', sharpness: 0.2, options: { quality: 'high' } },
  frameGen: { engine: 'vfx-vfg', multiplier: 2, model: 'medium', version: '', stageOrder: 'auto', options: {} },
  display: {
    monitorId: scenario === 'tv' ? 'tv:uuid:lg-g2' : 'mon1',
    mode: 'borderless',
    sync: 'auto',
    pacing: 'auto',
    overlay: false,
    windowTitle: 'TretioDLSS output',
    hdr: { mode: 'auto', paperWhite: 200, expand: 0.35, brightness: 0, contrast: 1, saturation: 1 },
  },
  tv: { mode: 'off' },
  audio: { inputId: 'auto', outputId: 'default', volume: 1, offsetMs: 0, autoSync: true },
  hotkeys: {
    toggleRun: 'Ctrl+Alt+F10',
    toggleFrameGen: 'Ctrl+Alt+F9',
    toggleOverlay: 'Ctrl+Alt+F8',
    compareUpscaler: 'Ctrl+Alt+F7',
    exitFullscreen: 'Ctrl+Alt+F11',
  },
  advanced: { adapter: 'auto', logLevel: 'info', vfxRuntimePath: '' },
  ui: { closeToTray: false, lastVersion: '0.1.0', onboarded: scenario !== 'setup' },
  discord: { enabled: false, game: 'Grand Theft Auto VI', startTime: '' },
  activeProfile: 'Profile 1',
};

const formats = [
  { width: 1920, height: 1080, fps: 60, subtype: 'NV12' },
  { width: 1920, height: 1080, fps: 60, subtype: 'YUY2' },
  { width: 1920, height: 1080, fps: 120, subtype: 'NV12' },
  { width: 2560, height: 1440, fps: 50, subtype: 'YUY2' },
  { width: 3840, height: 2160, fps: 60, subtype: 'MJPG' },
];

const state = {
  version: '0.1.0',
  settings,
  profiles: [
    { name: 'Profile 1', summary: '1440p30, NVIDIA FG x4, RTX Video' },
  ],
  devices:
    scenario === 'nodevice'
      ? []
      : [
          { id: 'gm29f', name: process.env.MOCK_DEVICE ?? 'USB3 Video (GM-29F)', formats },
          ...(scenario === 'setup'
            ? [
                { id: 'root#image#0000', name: 'e2eSoft iVCam', formats: [{ width: 1920, height: 1080, fps: 60, subtype: 'YUY2' }] },
                { id: 'usb#vid_046d&pid_085e', name: 'Logitech BRIO', formats: [{ width: 1280, height: 720, fps: 30, subtype: 'YUY2' }] },
              ]
            : []),
        ],
  windows: [
    { hwnd: '1', title: 'PS Remote Play', process: 'RemotePlay.exe' },
    { hwnd: '2', title: 'chiaki-ng', process: 'chiaki.exe' },
  ],
  monitors: [
    { id: 'mon1', name: 'Odyssey G60SD', width: 2560, height: 1440, refresh: 360, vrr: true, hdr: false, primary: true, tv: false, adapter: 'NVIDIA GeForce RTX 4090' },
    { id: 'mon2', name: 'LG ULTRAGEAR', width: 2560, height: 1440, refresh: 144, vrr: true, hdr: false, primary: false, tv: false, adapter: 'NVIDIA GeForce RTX 4090' },
  ],
  update: { status: '', ready: scenario === 'update' ? '0.7.0' : null },
  releaseNotes:
    scenario === 'notes'
      ? [
          { version: '0.7.0', notes: `* **TV:** Added streaming to the TV through Moonlight, with the TV picked by name under Output.
* **Interface:** Added profiles in the top bar.
* **TV:** Fixed the picture stopping after about 15 seconds at 4K 120 fps.` },
          { version: '0.6.2', notes: '* **Capture:** Fixed the picture staying black when the console was turned on after Start.' },
        ]
      : scenario === 'notes-long'
        ? Array.from({ length: 8 }, (_, v) => ({
            version: `0.${9 - v}.0`,
            notes: Array.from({ length: 6 }, (_, k) => `* **Area ${k + 1}:** Fixed something that was noticeable in version 0.${9 - v}, described in one plain sentence.`).join(String.fromCharCode(10)),
          }))
        : [],
  tvs: [{ id: 'tv:uuid:lg-g2', name: 'LG OLED55G26LA', model: 'OLED55G26LA', address: '192.168.1.30', webOs: true }],
  stream:
    scenario === 'tv'
      ? { running: true, port: 47989, error: null, waitingPin: 'LG webOS TV', clients: ['roth'], session: null }
      : { running: false, port: null, error: null, waitingPin: null, clients: [], session: null },
  audioInputs: [{ id: 'cardaudio', name: 'Digital Audio Interface (USB3 Video)' }],
  audioOutputs: [{ id: 'spk', name: 'Speakers (Sound BlasterX AE-5 Plus)', isDefault: true }],
  upscalers: [
    { id: 'off', name: 'Off', available: true },
    { id: 'bilinear', name: 'Bilinear', available: true },
    { id: 'bicubic', name: 'Bicubic', available: true },
    { id: 'fsr1', name: 'FSR 1', available: true },
    { id: 'nis', name: 'NVIDIA Image Scaling', available: true },
    { id: 'vfx-vsr', name: 'RTX Video', available: true, options: [{ key: 'quality', label: 'Quality', values: [{ value: 'low', label: 'Low' }, { value: 'medium', label: 'Medium' }, { value: 'high', label: 'High' }, { value: 'ultra', label: 'Ultra' }] }] },
  ],
  frameGenerators: [
    { id: 'off', name: 'Off', available: true },
    {
      id: 'vfx-vfg',
      name: 'NVIDIA Frame Generation',
      available: scenario === 'vfx',
      reason: 'Not downloaded',
      fixHint: 'Download it from NVIDIA',
      install: scenario === 'vfx' ? null : { state: 'none', progress: 0, bytes: 210_700_000 },
      multipliers: [2, 3, 4, 5, 6, 7, 8],
      options: [
        { key: 'model', label: 'Model', values: [{ value: 'low', label: 'Low' }, { value: 'medium', label: 'Medium' }, { value: 'high', label: 'High' }] },
      ],
    },
    { id: 'dlss-fg', name: 'DLSS Frame Generation', available: true, multipliers: [2, 3, 4] },
    { id: 'amd-frc', name: 'AMD Frame Generation', available: false, reason: 'The picture runs on a non-AMD GPU', fixHint: 'Runs only when the picture is on the AMD GPU', multipliers: [2] },
    { id: 'rife', name: 'RIFE', available: true, multipliers: [2, 3, 4], options: [{ key: 'model', label: 'Model', values: [{ value: 'rife426', label: 'Quality (RIFE 4.26)' }, { value: 'rife46', label: 'Fast (RIFE 4.6)' }] }] },
  ],
  pipeline: { status: process.env.MOCK_AUTOSTART ? 'running' : 'idle' },
  gpuName: 'NVIDIA GeForce RTX 4090',
  hints: scenario === 'hint' ? [{ id: 'nosignal', level: 'warning', text: 'The picture is black. Check the HDMI cable. On a PS5, turn off HDCP under Settings, System, HDMI.' }] : [],
};

const wsPort = Number(process.env.MOCK_WS_PORT ?? 44130);
const wss = new WebSocketServer({ port: wsPort });
let ticker = null;
let frameTicker = null;
let previewActive = true;
const history = { real: [], generated: [] };

function broadcast(method, content) {
  const msg = JSON.stringify({ method, content });
  wss.clients.forEach((c) => c.readyState === 1 && c.send(msg));
}

function deepMerge(base, patch) {
  for (const [k, v] of Object.entries(patch)) {
    if (v && typeof v === 'object' && !Array.isArray(v) && base[k] && typeof base[k] === 'object') deepMerge(base[k], v);
    else base[k] = v;
  }
}

function startTicker() {
  clearInterval(ticker);
  clearInterval(frameTicker);
  if (frames.length) {
    // Like the app: 30 fps while the window is in front, a still while it is not. The frame comes
    // from the clock, so the clip plays at real speed and loops, however late a tick fires.
    const start = Date.now();
    let lastSent = -1;
    frameTicker = setInterval(() => {
      if (!previewActive) return;
      const index = Math.floor(((Date.now() - start) / 1000) * 30) % frames.length;
      if (index === lastSent) return;
      lastSent = index;
      broadcast('Preview', frames[index]);
    }, 1000 / 60);
  }
  let t = 0;
  ticker = setInterval(() => {
    t++;
    const sm = state.settings.frameGen.engine;
    const mult = sm === 'off' ? 1 : state.settings.frameGen.multiplier;
    const real = 30 - (t % 17 === 0 ? 1 : 0);
    history.real = [...history.real, real].slice(-40);
    history.generated = [...history.generated, real * (mult - 1)].slice(-40);
    broadcast('Stats', {
      sourceFps: 60,
      realFps: real,
      outputFps: real * mult,
      outputEstimated: false,
      addedDelayMs: mult > 1 ? 20.1 + Math.random() : 3.2,
      delayNotMeasured: false,
      gpuPercent: 18 + Math.random() * 3,
      dropped: 0,
      late: 0,
      cadence: '2:2',
      containerHz: 60,
      sourceDetail: '30 real / 60 Hz',
      upscaleDetail: '1080p to 1440p, 2.1 ms',
      frameGenDetail: mult > 1 ? `x${mult}, ${real * mult} fps out` : 'Real frames only',
      outputDetail: '360 Hz, VRR',
      history,
    });
    if (!frames.length && preview && t % 2 === 0) broadcast('Preview', preview);
  }, 250);
}

wss.on('connection', (ws, req) => {
  const url = new URL(req.url, 'http://x');
  if (url.searchParams.get('token') !== 'dev') {
    ws.close(1008, 'bad token');
    return;
  }
  ws.on('message', (raw) => {
    const text = raw.toString();
    if (text === 'ping') return ws.send('pong');
    const { Method, Parameters } = JSON.parse(text);
    switch (Method) {
      case 'NewConnection':
        ws.send(JSON.stringify({ method: 'State', content: state }));
        break;
      case 'UpdateSettings':
        deepMerge(state.settings, Parameters);
        broadcast('State', state);
        break;
      case 'SetFrameGenEngine':
        state.settings.frameGen.engine = Parameters.engine;
        if (Parameters.multiplier) state.settings.frameGen.multiplier = Parameters.multiplier;
        broadcast('State', state);
        break;
      case 'SelectProfile':
        state.settings.activeProfile = Parameters.name;
        broadcast('State', state);
        break;
      case 'NewProfile': {
        let i = 2;
        while (state.profiles.some((p) => p.name === `Profile ${i}`)) i++;
        const current = state.profiles.find((p) => p.name === state.settings.activeProfile);
        state.profiles.push({ name: `Profile ${i}`, summary: current?.summary ?? '' });
        state.settings.activeProfile = `Profile ${i}`;
        broadcast('State', state);
        break;
      }
      case 'RenameProfile': {
        const p = state.profiles.find((x) => x.name === Parameters.name);
        const to = String(Parameters.to ?? '').trim();
        if (p && to && !state.profiles.some((x) => x !== p && x.name.toLowerCase() === to.toLowerCase())) {
          if (state.settings.activeProfile === p.name) state.settings.activeProfile = to;
          p.name = to;
        }
        broadcast('State', state);
        break;
      }
      case 'SubmitPin':
      case 'CancelPin':
        state.stream.waitingPin = null;
        broadcast('State', state);
        break;
      case 'CompleteOnboarding':
        state.settings.ui.onboarded = true;
        state.settings.display.monitorId = Parameters.monitorId;
        broadcast('State', state);
        break;
      case 'DismissReleaseNotes':
        state.releaseNotes = [];
        broadcast('State', state);
        break;
      case 'ScanTvs':
        broadcast('State', state);
        break;
      case 'DeleteProfile':
        if (state.profiles.length > 1) {
          state.profiles = state.profiles.filter((x) => x.name !== Parameters.name);
          if (state.settings.activeProfile === Parameters.name) state.settings.activeProfile = state.profiles[0].name;
        }
        broadcast('State', state);
        break;
      case 'StartPipeline':
        if (state.settings.display.monitorId.startsWith('tv:')) {
          state.pipeline = { status: 'waiting', message: 'Waiting for the TV' };
          broadcast('State', state);
          break;
        }
        state.pipeline = { status: 'starting' };
        broadcast('State', state);
        setTimeout(() => {
          state.pipeline = { status: 'running' };
          broadcast('State', state);
          startTicker();
        }, 700);
        break;
      case 'SearchDiscordGames': {
        const q = String(Parameters?.query ?? '').toLowerCase();
        const all = ['Grand Theft Auto VI', 'Grand Theft Auto V Enhanced', 'Grand Theft Auto Online', "Astro's Playroom", 'EA SPORTS FC 26'];
        const games = all.filter((n) => n.toLowerCase().includes(q)).map((name, i) => ({ id: String(i), name, icon: '' }));
        ws.send(JSON.stringify({ method: 'DiscordGames', content: { query: Parameters?.query ?? '', games } }));
        break;
      }
      case 'StopPipeline':
        clearInterval(ticker);
        clearInterval(frameTicker);
        state.pipeline = { status: 'idle' };
        broadcast('State', state);
        break;
      case 'SetPreviewActive':
        previewActive = !!Parameters?.active;
        break;
      case 'GetCapabilities':
        setTimeout(
          () =>
            ws.send(
              JSON.stringify({
                method: 'Capabilities',
                content: {
                  rows: [
                    { group: 'GPU', name: 'Graphics card', value: 'NVIDIA GeForce RTX 4090, driver 617.42', ok: true },
                    { group: 'GPU', name: 'Second GPU', value: 'AMD Radeon(TM) Graphics, not used', ok: true },
                    { group: 'NVIDIA', name: 'RTX Video', value: 'Ready', ok: true },
                    { group: 'NVIDIA', name: 'NVIDIA Frame Generation', value: 'Not downloaded yet', ok: false },
                    { group: 'Windows', name: 'Tearing presents', value: 'Supported', ok: true },
                    { group: 'Windows', name: 'Capture above 60 fps', value: 'Supported (build 26300)', ok: true },
                    { group: 'Windows', name: 'Camera access', value: 'Allowed for desktop apps', ok: true },
                  ],
                },
              }),
            ),
          400,
        );
        break;
      case 'InstallComponent': {
        // Like the app: progress pushed with the state, then the engine reads as downloaded.
        const engine = [...state.upscalers, ...state.frameGenerators].find((e) => e.id === Parameters.id);
        if (!engine?.install || engine.install.state === 'downloading') break;
        const fail = scenario === 'installfail';
        let progress = 0;
        const timer = setInterval(() => {
          progress = Math.min(1, progress + 0.04 + Math.random() * 0.04);
          if (fail && progress > 0.45) {
            clearInterval(timer);
            engine.install = { ...engine.install, state: 'failed', progress: 0, error: 'Download failed: the connection was reset' };
            broadcast('Toast', { level: 'error', text: engine.install.error });
          } else if (progress >= 1) {
            clearInterval(timer);
            engine.install = null;
            if (engine.id === 'vfx-vfg') {
              engine.available = true;
              delete engine.reason;
              delete engine.fixHint;
            } else {
              engine.reason = 'Engine not built yet';
              engine.fixHint = 'Downloaded, engine not built yet';
            }
            broadcast('Toast', { level: 'info', text: `${engine.name} downloaded` });
          } else {
            engine.install = { ...engine.install, state: 'downloading', progress };
          }
          broadcast('State', state);
        }, 250);
        break;
      }
      case 'RunLatencyTest':
        setTimeout(() => ws.send(JSON.stringify({ method: 'LatencyResult', content: { samples: 50, minMs: 38.2, medianMs: 46.9, maxMs: 55.4 } })), 2500);
        break;
      default:
        console.log('[mock]', Method, Parameters ?? '');
    }
  });
});

if (process.env.MOCK_AUTOSTART) startTicker();
console.log(`[mock] WebSocket on ws://localhost:${wsPort}, scenario "${scenario}"`);
console.log('[mock] open http://localhost:2992/?token=dev');

if (!process.argv.includes('--no-vite')) {
  const vite = spawn(process.platform === 'win32' ? 'npx.cmd' : 'npx', ['vite'], { stdio: 'inherit', shell: true, cwd: path.join(here, '..') });
  process.on('exit', () => vite.kill());
}
