import { GamePicker } from '../Components/GamePicker';
import { useEffect, useRef, useState } from 'react';
import { FolderOpen, RefreshCw } from 'lucide-react';
import { Button } from '../Components/Button';
import { Dropdown } from '../Components/Dropdown';
import { OptionRow, Panel, Slider, TextInput, Toggle } from '../Components/Controls';
import { useStore } from '../lib/store';
import { engineOptions, formatLabel, layersLabel, outputChoices } from '../lib/options';

const sections = [
  { id: 'source', label: 'Source' },
  { id: 'upscaling', label: 'Upscaling' },
  { id: 'framegen', label: 'Frame generation' },
  { id: 'neural', label: 'DLSS 5' },
  { id: 'display', label: 'Display' },
  { id: 'tv', label: 'TV' },
  { id: 'audio', label: 'Audio' },
  { id: 'hotkeys', label: 'Hotkeys' },
  { id: 'advanced', label: 'Advanced' },
];

export function SettingsPage({ section }: { section: string | null }) {
  const { state, update, send } = useStore();
  const scrollRef = useRef<HTMLDivElement>(null);
  const [current, setCurrent] = useState('source');

  useEffect(() => {
    if (section) document.getElementById(section)?.scrollIntoView({ block: 'start' });
  }, [section]);

  useEffect(() => {
    const root = document.querySelector('main');
    if (!root) return;
    const onScroll = () => {
      let best = 'source';
      for (const s of sections) {
        const el = document.getElementById(s.id);
        if (el && el.getBoundingClientRect().top < 160) best = s.id;
      }
      setCurrent(best);
    };
    root.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
    return () => root.removeEventListener('scroll', onScroll);
  }, []);

  if (!state) return null;
  const upscaler = state.upscalers.find((e) => e.id === state.settings.upscale.engine);
  const s = state.settings;
  const live = state.pipeline.status === 'running';
  const device = state.devices.find((d) => d.id === s.source.deviceId);
  const frameGen = state.frameGenerators.find((e) => e.id === s.frameGen.engine);
  const formatValue = `${s.source.width}x${s.source.height}@${s.source.fps}:${s.source.subtype}`;

  return (
    <div className="mx-auto flex max-w-[1400px] justify-center gap-8 px-9 pt-1 pb-9" ref={scrollRef}>
      <div className="flex min-w-0 max-w-[760px] flex-1 flex-col gap-6">
        <header className="flex items-center justify-between">
          <h1 className="font-display text-[1.75rem] leading-9 font-bold text-chalk">Settings</h1>
          {live && <span className="text-[0.8125rem] text-dusk">Source and display changes apply after Stop</span>}
        </header>

        <Panel
          id="source"
          eyebrow="Pipeline"
          title="Source"
          action={<Button size="sm" icon={RefreshCw} onClick={() => send('RefreshDevices')}>Refresh</Button>}
        >
          <OptionRow label="Kind" helper="Where the picture comes from">
            <Dropdown
              ariaLabel="Source kind"
              className="w-72"
              value={s.source.kind}
              disabled={live}
              options={[
                { value: 'uvc', label: 'Capture card' },
                { value: 'window', label: 'Window' },
                { value: 'pattern', label: 'Test pattern' },
                { value: 'file', label: 'Video file' },
                { value: 'raw', label: 'Raw recording' },
              ]}
              onChange={(kind) => update({ source: { kind } })}
            />
          </OptionRow>

          {s.source.kind === 'uvc' && (
            <>
              <OptionRow label="Device" helper={state.devices.length === 0 ? 'No capture device found. Plug the card into a USB 3 port.' : undefined}>
                <Dropdown
                  ariaLabel="Capture device"
                  className="w-72"
                  value={s.source.deviceId}
                  placeholder="No device"
                  disabled={live || state.devices.length === 0}
                  options={state.devices.map((d) => ({ value: d.id, label: d.name }))}
                  onChange={(deviceId) => update({ source: { deviceId } })}
                />
              </OptionRow>
              <OptionRow label="Format" helper="Uncompressed NV12 or YUY2 has the least delay">
                <Dropdown
                  ariaLabel="Capture format"
                  className="w-72"
                  value={formatValue}
                  disabled={live || !device}
                  options={(device?.formats ?? []).map((f) => ({
                    value: `${f.width}x${f.height}@${f.fps}:${f.subtype}`,
                    label: formatLabel(f),
                    detail: f.subtype === 'MJPG' ? 'Compressed, adds decode delay' : undefined,
                  }))}
                  onChange={(v) => {
                    const f = device?.formats.find((x) => `${x.width}x${x.height}@${x.fps}:${x.subtype}` === v);
                    if (f) update({ source: { width: f.width, height: f.height, fps: f.fps, subtype: f.subtype } });
                  }}
                />
              </OptionRow>
            </>
          )}

          {s.source.kind === 'window' && (
            <OptionRow label="Window" helper="The window must stay open, it can be covered">
              <Dropdown
                ariaLabel="Window"
                className="w-72"
                value={`${s.source.window.process}|${s.source.window.title}`}
                placeholder="No window picked"
                disabled={live}
                options={state.windows.map((w) => ({ value: `${w.process}|${w.title}`, label: w.title, detail: w.process }))}
                onChange={(v) => {
                  const [process, title] = v.split('|');
                  update({ source: { window: { process, title } } });
                }}
              />
            </OptionRow>
          )}


          {(s.source.kind === 'file' || s.source.kind === 'raw') && (
            <OptionRow
              label={s.source.kind === 'file' ? 'Video file' : 'Recording folder'}
              helper={s.source.kind === 'file' ? 'Plays looped, at its own frame rate' : 'Written by the raw recorder'}
              stacked
            >
              <div className="flex gap-2">
                <TextInput
                  label="Path"
                  className="flex-1"
                  value={s.source.kind === 'file' ? s.source.file.path : s.source.raw.path}
                  placeholder="C:\\..."
                  onCommit={(path) => update(s.source.kind === 'file' ? { source: { file: { path } } } : { source: { raw: { path } } })}
                />
                <Button icon={FolderOpen} disabled={live} onClick={() => send('PickPath', { kind: s.source.kind })}>
                  Browse
                </Button>
              </div>
            </OptionRow>
          )}

          {(s.source.kind === 'uvc' || s.source.kind === 'raw') && (
            <>
              <OptionRow label="Color matrix" helper="Change if colors look off">
                <Dropdown
                  ariaLabel="Color matrix"
                  className="w-72"
                  value={s.source.colorMatrix}
                  options={[
                    { value: 'auto', label: 'Automatic' },
                    { value: 'bt709', label: 'BT.709' },
                    { value: 'bt601', label: 'BT.601' },
                  ]}
                  onChange={(colorMatrix) => update({ source: { colorMatrix } })}
                />
              </OptionRow>
              <OptionRow label="Color range" helper="Change if blacks look grey or shadows are crushed">
                <Dropdown
                  ariaLabel="Color range"
                  className="w-72"
                  value={s.source.colorRange}
                  options={[
                    { value: 'auto', label: 'Automatic' },
                    { value: 'limited', label: 'Limited' },
                    { value: 'full', label: 'Full' },
                  ]}
                  onChange={(colorRange) => update({ source: { colorRange } })}
                />
              </OptionRow>
            </>
          )}
        </Panel>

        <Panel id="upscaling" eyebrow="Pipeline" title="Upscaling">
          <OptionRow label="Engine">
            <Dropdown
              ariaLabel="Upscaling engine"
              className="w-72"
              align="right"
              value={s.upscale.engine}
              options={engineOptions(state.upscalers, (id) => send('InstallComponent', { id }))}
              onChange={(engine) => update({ upscale: { engine } })}
            />
          </OptionRow>
          {upscaler?.options?.map((o) => (
            <OptionRow key={o.key} label={o.label} helper={o.key === 'quality' ? 'Higher looks better and costs more GPU time' : undefined}>
              <Dropdown
                ariaLabel={o.label}
                className="w-72"
                align="right"
                value={String(s.upscale.options[o.key] ?? (o.key === 'quality' ? 'high' : o.values[0]?.value))}
                options={o.values.map((v) => ({ value: v.value, label: v.label }))}
                onChange={(v) => update({ upscale: { options: { [o.key]: v } } })}
              />
            </OptionRow>
          ))}
          <OptionRow label="Output size">
            <Dropdown
              ariaLabel="Output size"
              className="w-72"
              value={s.upscale.size}
              options={[
                { value: 'display', label: 'Fill the display' },
                { value: 'supersample', label: 'Supersample 2x' },
                { value: 'integer', label: 'Integer scale' },
                { value: 'source', label: 'Source size' },
              ]}
              onChange={(size) => update({ upscale: { size } })}
            />
          </OptionRow>
          <OptionRow label="Sharpness" helper="Applied after any upscaler" stacked>
            <Slider
              label="Sharpness"
              min={0}
              max={1}
              step={0.05}
              value={s.upscale.sharpness}
              format={(v) => `${Math.round(v * 100)}%`}
              onChange={(sharpness) => update({ upscale: { sharpness } })}
            />
          </OptionRow>
        </Panel>

        <Panel id="framegen" eyebrow="Pipeline" title="Frame generation">
          <OptionRow label="Engine">
            <Dropdown
              ariaLabel="Frame generation engine"
              className="w-72"
              align="right"
              value={s.frameGen.engine}
              options={engineOptions(state.frameGenerators, (id) => send('InstallComponent', { id }))}
              onChange={(engine) => send('SetFrameGenEngine', { engine })}
            />
          </OptionRow>
          {frameGen?.multipliers && frameGen.multipliers.length > 1 && (
            <OptionRow label="Multiplier" helper="Each extra frame adds delay">
              <Dropdown
                ariaLabel="Multiplier"
                className="w-72"
                value={String(s.frameGen.multiplier)}
                options={frameGen.multipliers.map((m) => ({ value: String(m), label: `x${m}` }))}
                onChange={(v) => update({ frameGen: { multiplier: Number(v) } })}
              />
            </OptionRow>
          )}
          {frameGen?.options?.map((opt) => (
            <OptionRow key={opt.key} label={opt.label}>
              <Dropdown
                ariaLabel={opt.label}
                className="w-72"
                value={opt.key === 'model' ? s.frameGen.model : opt.key === 'version' ? s.frameGen.version : String(s.frameGen.options[opt.key] ?? '')}
                options={opt.values}
                onChange={(v) =>
                  update(
                    opt.key === 'model'
                      ? { frameGen: { model: v } }
                      : opt.key === 'version'
                        ? { frameGen: { version: v } }
                        : { frameGen: { options: { [opt.key]: v } } },
                  )
                }
              />
            </OptionRow>
          ))}
        </Panel>

        <Panel id="neural" eyebrow="Pipeline" title="DLSS 5">
          <OptionRow label="Neural rendering" helper="NVIDIA's model reworks light and materials in every real frame, the HUD included">
            <Dropdown
              ariaLabel="DLSS 5"
              className="w-72"
              align="right"
              value={s.neural.engine}
              options={engineOptions(state.neuralEngines ?? [], (id) => send('InstallComponent', { id }))}
              onChange={(engine) => update({ neural: { engine } })}
            />
          </OptionRow>
          {s.neural.engine === 'dlss5' && (
            <>
              <OptionRow label="Layers" helper="Each layer strengthens the look and adds about 3 ms at 1080p on an RTX 4090">
                <Dropdown
                  ariaLabel="Layers"
                  className="w-72"
                  value={String(s.neural.layers)}
                  options={[1, 2, 3, 4].map((n) => ({ value: String(n), label: layersLabel(n) }))}
                  onChange={(v) => update({ neural: { layers: Number(v) } })}
                />
              </OptionRow>
              <OptionRow label="Style">
                <Dropdown
                  ariaLabel="Style"
                  className="w-72"
                  value={s.neural.style}
                  options={[
                    { value: 'default', label: 'Default' },
                    { value: 'natural', label: 'Natural' },
                    { value: 'cinematic', label: 'Cinematic' },
                  ]}
                  onChange={(style) => update({ neural: { style } })}
                />
              </OptionRow>
              <OptionRow label="Intensity" helper="0% leaves the picture as it is" stacked>
                <Slider
                  label="Intensity"
                  min={0}
                  max={1}
                  step={0.05}
                  value={s.neural.intensity}
                  format={(v) => `${Math.round(v * 100)}%`}
                  onChange={(intensity) => update({ neural: { intensity } })}
                />
              </OptionRow>
              <OptionRow label="Model" helper="The original model costs about twice as much">
                <Dropdown
                  ariaLabel="Model"
                  className="w-72"
                  value={s.neural.model}
                  options={[
                    { value: 'fast', label: 'Fast, October 2026' },
                    { value: 'original', label: 'Original, September 2026' },
                  ]}
                  onChange={(model) => update({ neural: { model } })}
                />
              </OptionRow>
              <OptionRow label="Runs on" helper="Before upscaling costs less: fewer pixels per layer">
                <Dropdown
                  ariaLabel="Runs on"
                  className="w-72"
                  value={s.neural.placement}
                  options={[
                    { value: 'source', label: 'Capture size, before upscaling' },
                    { value: 'output', label: 'Output size, after upscaling' },
                  ]}
                  onChange={(placement) => update({ neural: { placement } })}
                />
              </OptionRow>
            </>
          )}
        </Panel>

        <Panel id="display" eyebrow="Output" title="Display">
          <OptionRow label="Display">
            <Dropdown
              ariaLabel="Display"
              className="w-72"
              align="right"
              value={s.display.monitorId}
              disabled={live}
              options={outputChoices(state)}
              onChange={(monitorId) => update({ display: { monitorId } })}
            />
          </OptionRow>
          <OptionRow label="Mode">
            <Dropdown
              ariaLabel="Display mode"
              className="w-72"
              value={s.display.mode}
              disabled={live}
              options={[
                { value: 'borderless', label: 'Fullscreen' },
                { value: 'windowed', label: 'Window' },
              ]}
              onChange={(mode) => update({ display: { mode } })}
            />
          </OptionRow>
          <OptionRow label="Sync" helper="VRR has the least delay on a G-SYNC or FreeSync display">
            <Dropdown
              ariaLabel="Sync"
              className="w-72"
              value={s.display.sync}
              options={[
                { value: 'auto', label: 'Automatic' },
                { value: 'vrr', label: 'VRR' },
                { value: 'vsync', label: 'V-Sync' },
                { value: 'tearing', label: 'Tearing' },
              ]}
              onChange={(sync) => update({ display: { sync } })}
            />
          </OptionRow>
          <OptionRow label="Stats overlay" helper={`Toggle while playing with ${s.hotkeys.toggleOverlay}`}>
            <Toggle label="Stats overlay" checked={s.display.overlay} onChange={(overlay) => update({ display: { overlay } })} />
          </OptionRow>
          <OptionRow label="Window title" helper="The output window's name, as other apps see it. Empty uses the Discord game's name">
            <TextInput label="Window title" className="w-72" value={s.display.windowTitle} placeholder={s.discord.game.trim() || 'TretioDLSS output'} onCommit={(windowTitle) => update({ display: { windowTitle } })} />
          </OptionRow>
          <OptionRow
            label="Game icon"
            helper={s.discord.game.trim() ? `The output window and its taskbar button show the icon of ${s.discord.game.trim()}` : 'Set a Discord game to use its icon'}
          >
            <Toggle label="Game icon" checked={s.display.gameIcon} onChange={(gameIcon) => update({ display: { gameIcon } })} />
          </OptionRow>
          <OptionRow
            label="Run as the game"
            helper={
              s.discord.game.trim()
                ? `The picture runs in its own process named after ${s.discord.game.trim()}, so Task Manager, Discord and other apps see the game`
                : 'Set a Discord game to run the picture under its name'
            }
          >
            <Toggle label="Run as the game" checked={s.display.gameProcess} onChange={(gameProcess) => update({ display: { gameProcess } })} />
          </OptionRow>
          <OptionRow label="HDR" helper={hdrHelper(state.monitors.find((m) => m.id === s.display.monitorId)?.hdr)}>
            <Dropdown
              ariaLabel="HDR"
              className="w-72"
              value={s.display.hdr.mode}
              options={[
                { value: 'auto', label: 'Automatic', detail: 'HDR when Windows HDR is on for the display' },
                { value: 'off', label: 'Off', detail: 'Always SDR' },
              ]}
              onChange={(mode) => update({ display: { hdr: { mode } } })}
            />
          </OptionRow>
          {s.display.hdr.mode === 'auto' && (
            <>
              <OptionRow label="Paper white" helper="How bright normal white is in HDR" stacked>
                <Slider label="Paper white" min={80} max={500} step={10} value={s.display.hdr.paperWhite} format={(v) => `${v} nits`} onChange={(paperWhite) => update({ display: { hdr: { paperWhite } } })} />
              </OptionRow>
              <OptionRow label="Highlights" helper="How far bright parts rise above paper white" stacked>
                <Slider label="Highlights" min={0} max={1} step={0.05} value={s.display.hdr.expand} format={(v) => `${Math.round(v * 100)}%`} onChange={(expand) => update({ display: { hdr: { expand } } })} />
              </OptionRow>
              <OptionRow label="Brightness" stacked>
                <Slider label="Brightness" min={-0.2} max={0.2} step={0.01} value={s.display.hdr.brightness} format={(v) => `${v > 0 ? '+' : ''}${Math.round(v * 100)}`} onChange={(brightness) => update({ display: { hdr: { brightness } } })} />
              </OptionRow>
              <OptionRow label="Contrast" stacked>
                <Slider label="Contrast" min={0.8} max={1.2} step={0.01} value={s.display.hdr.contrast} format={(v) => `${Math.round(v * 100)}%`} onChange={(contrast) => update({ display: { hdr: { contrast } } })} />
              </OptionRow>
              <OptionRow label="Saturation" stacked>
                <Slider label="Saturation" min={0.8} max={1.4} step={0.01} value={s.display.hdr.saturation} format={(v) => `${Math.round(v * 100)}%`} onChange={(saturation) => update({ display: { hdr: { saturation } } })} />
              </OptionRow>
            </>
          )}
        </Panel>

        <Panel id="tv" eyebrow="Output" title="TV">
          <OptionRow label="TV path" helper={tvHelper(s.tv.mode)}>
            <Dropdown
              ariaLabel="TV path"
              className="w-72"
              value={s.tv.mode}
              options={[
                { value: 'off', label: 'Not used' },
                { value: 'display', label: 'TV as a display (HDMI)', detail: 'Least delay' },
                { value: 'sunshine', label: 'Sunshine and Moonlight', detail: 'For a TV out of cable reach' },
              ]}
              onChange={(mode) => update({ tv: { mode } })}
            />
          </OptionRow>
          {s.tv.mode === 'sunshine' && (
            <OptionRow label="Sunshine" helper="Adds a TretioDLSS entry in Sunshine so you can start from the TV">
              <Button onClick={() => send('SunshineRegister')}>Add to Sunshine</Button>
            </OptionRow>
          )}
        </Panel>

        <Panel id="audio" eyebrow="Output" title="Audio">
          <OptionRow label="Input" helper="Automatic uses the capture card's own sound">
            <Dropdown
              ariaLabel="Audio input"
              className="w-72"
              align="right"
              value={s.audio.inputId}
              options={[{ value: 'auto', label: 'Automatic' }, { value: 'none', label: 'No sound' }, ...state.audioInputs.map((d) => ({ value: d.id, label: d.name }))]}
              onChange={(inputId) => update({ audio: { inputId } })}
            />
          </OptionRow>
          <OptionRow label="Output">
            <Dropdown
              ariaLabel="Audio output"
              className="w-72"
              align="right"
              value={s.audio.outputId}
              options={[{ value: 'default', label: 'Windows default' }, ...state.audioOutputs.map((d) => ({ value: d.id, label: d.name }))]}
              onChange={(outputId) => update({ audio: { outputId } })}
            />
          </OptionRow>
          <OptionRow label="Volume" stacked>
            <Slider label="Volume" min={0} max={1} step={0.01} value={s.audio.volume} format={(v) => `${Math.round(v * 100)}%`} onChange={(volume) => update({ audio: { volume } })} />
          </OptionRow>
          <OptionRow label="Match sound to picture" helper="Delays sound by the picture's delay">
            <Toggle label="Match sound to picture" checked={s.audio.autoSync} onChange={(autoSync) => update({ audio: { autoSync } })} />
          </OptionRow>
          <OptionRow label="Extra offset" helper="Positive values delay the sound more" stacked>
            <Slider label="Extra offset" min={-200} max={300} step={5} value={s.audio.offsetMs} format={(v) => `${v > 0 ? '+' : ''}${v} ms`} onChange={(offsetMs) => update({ audio: { offsetMs } })} />
          </OptionRow>
        </Panel>

        <Panel id="hotkeys" eyebrow="Controls" title="Hotkeys">
          {(
            [
              ['toggleRun', 'Start or stop'],
              ['toggleFrameGen', 'Frame generation on or off'],
              ['toggleOverlay', 'Stats overlay'],
              ['compareUpscaler', 'Compare upscaler with plain scaling'],
              ['exitFullscreen', 'Leave fullscreen'],
            ] as const
          ).map(([key, label]) => (
            <OptionRow key={key} label={label}>
              <TextInput label={label} className="w-72" value={s.hotkeys[key]} onCommit={(v) => update({ hotkeys: { [key]: v } })} />
            </OptionRow>
          ))}
        </Panel>

        <Panel id="advanced" eyebrow="Other" title="Advanced">
          <OptionRow label="Close to tray" helper="The close button hides the window; the picture keeps running">
            <Toggle label="Close to tray" checked={s.ui.closeToTray} onChange={(closeToTray) => update({ ui: { closeToTray } })} />
          </OptionRow>
          <OptionRow
            label="Discord status"
            helper={state.discordStatus || 'Shows the game on your Discord profile while the picture runs. The time keeps counting across restarts and starts over after a 30 minute break'}
          >
            <Toggle label="Discord status" checked={s.discord.enabled} onChange={(enabled) => update({ discord: { enabled } })} />
          </OptionRow>
          <OptionRow label="Discord game" helper="The game's name as Discord lists it. The game icon comes from here too">
            <GamePicker className="w-72" value={s.discord.game} onChange={(game) => update({ discord: { game } })} />
          </OptionRow>
          <OptionRow label="Updates" helper={state.update?.status || 'Not checked yet'}>
            {state.update?.ready ? (
              <Button size="sm" onClick={() => send('InstallUpdate')}>
                Restart and install
              </Button>
            ) : (
              <Button size="sm" onClick={() => send('CheckForUpdates')}>
                Check now
              </Button>
            )}
          </OptionRow>
          <OptionRow label="Log level">
            <Dropdown
              ariaLabel="Log level"
              className="w-72"
              value={s.advanced.logLevel}
              options={[
                { value: 'info', label: 'Normal' },
                { value: 'debug', label: 'Detailed' },
              ]}
              onChange={(logLevel) => update({ advanced: { logLevel } })}
            />
          </OptionRow>
        </Panel>
        <p className="pb-2 text-center text-[0.8125rem] text-dusk">Version {state.version}</p>
      </div>

      <nav className="sticky top-6 hidden h-fit w-44 shrink-0 flex-col gap-0.5 pt-[4.25rem] min-[1200px]:flex" aria-label="Settings sections">
        {sections.map((sec) => (
          <button
            key={sec.id}
            type="button"
            onClick={() => document.getElementById(sec.id)?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
            className={`flex h-8 items-center rounded-lg px-3 text-left text-[0.8125rem] leading-none transition-colors duration-200 ${
              current === sec.id ? 'bg-marquee text-chalk' : 'text-dusk hover:text-haze'
            }`}
          >
            {sec.label}
          </button>
        ))}
      </nav>
    </div>
  );
}

function tvHelper(mode: string) {
  if (mode === 'display') return 'Pick the TV under Display and turn on its game mode';
  if (mode === 'sunshine') return 'Sunshine streams the output display to Moonlight on the TV';
  return 'Miracast (Win+K) also works, with more delay';
}

function hdrHelper(displayHdr?: boolean) {
  return displayHdr ? 'Windows HDR is on for this display' : 'Windows HDR is off for this display, so the output stays SDR';
}
