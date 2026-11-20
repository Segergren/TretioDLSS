import { useEffect, useState } from 'react';
import { ArrowLeft, Check, Monitor, RefreshCw, Tv, Video, type LucideIcon } from 'lucide-react';
import { Button } from '../Components/Button';
import { useStore } from '../lib/store';
import { deviceDetail, deviceKind, formatHz, monitorDetail, sortedDevices } from '../lib/options';
import { AppState } from '../Models/types';

type Choice = { value: string; label: string; detail?: string; icon?: LucideIcon; disabled?: boolean };

const steps = ['Output', 'Capture card', 'Game', 'Smoothness'] as const;

/** First run: where the picture goes, which card it comes from, and the frame rates in and out. */
export function Onboarding() {
  const { state, send } = useStore();
  const [welcome, setWelcome] = useState(true);
  const [leaving, setLeaving] = useState(false);
  const [endWelcome] = useState(() => () => setWelcome(false));
  const [step, setStep] = useState(0);
  const [output, setOutput] = useState('');
  const [device, setDevice] = useState('');
  const [inputFps, setInputFps] = useState(0);
  const [outputFps, setOutputFps] = useState(0);
  if (!state) return null;
  if (welcome) return <Welcome onDone={endWelcome} />;

  const outputs: Choice[] = [
    ...state.monitors.map((m) => ({ value: m.id, label: m.name, detail: monitorDetail(m), icon: m.tv ? Tv : Monitor })),
    ...state.tvs.map((t) => ({ value: t.id, label: t.name, detail: 'TV on the network, through Moonlight', icon: Tv })),
  ];
  const devices: Choice[] = sortedDevices(state.devices).map((d) => ({ value: d.id, label: d.name, detail: deviceDetail(d), icon: Video }));
  const formats = state.devices.find((d) => d.id === device)?.formats ?? [];
  const inputs: Choice[] = [
    { value: '30', label: '30 fps', detail: 'Quality or fidelity mode' },
    { value: '60', label: '60 fps', detail: 'Performance mode' },
    { value: '120', label: '120 fps', detail: '120 Hz mode, on a 120 Hz output' },
  ].map((c) => ({ ...c, disabled: formats.length > 0 && !formats.some((f) => Math.abs(f.fps - Number(c.value)) <= Number(c.value) * 0.01) }));
  const maxHz = maxRefresh(state, output);
  const outputsFps = outputRates(inputFps, maxHz);

  const pick = [
    { choices: outputs, value: output, set: setOutput },
    { choices: devices, value: device, set: setDevice },
    { choices: inputs, value: inputFps ? String(inputFps) : '', set: (v: string) => setInputFps(Number(v)) },
    { choices: outputsFps, value: outputFps ? String(outputFps) : '', set: (v: string) => setOutputFps(Number(v)) },
  ][step];

  const titles = [
    'Where should the picture go?',
    'Which capture card is the console on?',
    'What frame rate is the game set to?',
    'How smooth should it look?',
  ];
  const helpers = [
    'A monitor, or a TV on your network that has Moonlight.',
    'Connect the card with USB and turn the console on.',
    "What the console sends now, set by the game's graphics mode. You choose how smooth it should look on the next step.",
    `Up to ${formatHz(maxHz)} fps on ${outputs.find((o) => o.value === output)?.label ?? 'this output'}.`,
  ];

  const next = () => {
    if (step === 0 && !device) {
      const cards = state.devices.filter((d) => deviceKind(d) === 'card');
      if (cards.length === 1) setDevice(cards[0].id);
    }
    if (step === 1 && !inputFps) {
      const has = (r: number) => formats.some((f) => Math.abs(f.fps - r) <= r * 0.01);
      setInputFps(has(30) ? 30 : has(60) ? 60 : 0);
    }
    if (step === 2) {
      const best = outputRates(inputFps, maxHz).filter((c) => !c.disabled).at(-1);
      if (!outputFps && best) setOutputFps(Number(best.value));
    }
    if (step < steps.length - 1) setStep(step + 1);
    else {
      // Fade out first; the main view fades in when the settings arrive.
      setLeaving(true);
      const done = { monitorId: output, deviceId: device || null, inputFps, outputFps: outputFps || inputFps };
      setTimeout(() => {
        // An unpaired TV goes straight on to the guide for Moonlight, without showing the main view first.
        if (output.startsWith('tv:') && state.stream.clients.length === 0) window.dispatchEvent(new Event('tretio:tvhelp'));
        send('CompleteOnboarding', done);
      }, 380);
    }
  };

  const canNext = step === 1 ? true : !!pick.value;

  return (
    <div className={`intro-fade flex h-full flex-col px-9 pt-6 pb-6 transition-opacity duration-[380ms] ${leaving ? 'opacity-0' : ''}`}>
      <ol className="grid w-full grid-cols-4 gap-3" aria-label="Setup steps">
        {steps.map((name, i) => (
          <li key={name} aria-current={i === step ? 'step' : undefined} className="flex flex-col gap-2.5">
            <span className={`h-1 rounded-full transition-colors duration-300 ${i <= step ? 'bg-flamingo' : 'bg-edge-strong'}`} aria-hidden />
            <span
              className={`text-[0.8125rem] leading-4 font-medium ${
                i === step ? 'text-chalk' : i < step ? 'text-haze' : 'text-dusk'
              }`}
            >
              {name}
            </span>
          </li>
        ))}
      </ol>

      <h1 className="mt-8 font-display text-[2.25rem] leading-10 font-bold text-chalk">{titles[step]}</h1>
      <p className="mt-2 text-[0.9375rem] leading-5 text-dusk">{helpers[step]}</p>

      <div className="mt-6 min-h-0 flex-1 overflow-y-auto pr-1">
        {pick.choices.length === 0 ? (
          <div className="flex items-center gap-3 rounded-[20px] border border-edge bg-boulevard p-5">
            <p className="flex-1 text-[0.9375rem] leading-5 text-haze">
              {step === 1 ? 'No capture card found. You can pick one later under Source.' : 'Nothing found.'}
            </p>
            <Button size="sm" icon={RefreshCw} onClick={() => send(step === 0 ? 'ScanTvs' : 'RefreshDevices')}>
              Look again
            </Button>
          </div>
        ) : step === 1 ? (
          <DeviceList state={state} value={device} onPick={setDevice} />
        ) : (
          <div className="grid grid-cols-1 gap-3 min-[760px]:grid-cols-2">
            {pick.choices.map((c) => (
              <ChoiceCard key={c.value} choice={c} selected={pick.value === c.value} onClick={() => pick.set(c.value)} />
            ))}
          </div>
        )}
        {step === 0 && pick.choices.length > 0 && (
          <div className="mt-4 flex items-center gap-2 text-[0.875rem] leading-5 text-dusk">
            <span>TV not listed? Turn it on, then</span>
            <Button size="sm" variant="ghost" icon={RefreshCw} onClick={() => send('ScanTvs')}>
              Look again
            </Button>
          </div>
        )}
      </div>

      <div className="mt-5 flex items-center gap-3">
        {step > 0 && (
          <Button variant="ghost" icon={ArrowLeft} onClick={() => setStep(step - 1)}>
            Back
          </Button>
        )}
        <span className="flex-1" />
        <button
          type="button"
          disabled={!canNext}
          onClick={next}
          className="h-12 rounded-full bg-flamingo px-7 text-[1rem] leading-none font-semibold text-night transition-opacity duration-200 disabled:opacity-40"
        >
          {step < steps.length - 1 ? 'Continue' : 'Done'}
        </button>
      </div>
    </div>
  );
}

/** The opening: the app mark assembles, the name rises, then it all fades into the first step. */
function Welcome({ onDone }: { onDone: () => void }) {
  const [leaving, setLeaving] = useState(false);
  useEffect(() => {
    const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const fade = setTimeout(() => setLeaving(true), reduce ? 600 : 2000);
    const done = setTimeout(onDone, reduce ? 700 : 2450);
    return () => {
      clearTimeout(fade);
      clearTimeout(done);
    };
  }, [onDone]);
  return (
    <div
      className={`flex h-full flex-col items-center justify-center transition-opacity duration-[450ms] ${leaving ? 'opacity-0' : 'opacity-100'}`}
      aria-label="TretioDLSS"
    >
      <div>
        <svg viewBox="37 67 182 130" className="relative h-[120px] w-auto overflow-visible" aria-hidden>
          <g className="intro-back" style={{ transformOrigin: '104px 117px', transformBox: 'view-box' }}>
            <rect x="44" y="74" width="120" height="86" rx="14" fill="none" stroke="#4d5565" strokeWidth="14" />
          </g>
          <g className="intro-front" style={{ transformOrigin: '152px 147px', transformBox: 'view-box' }}>
            <rect x="92" y="104" width="120" height="86" rx="14" fill="none" stroke="var(--color-flamingo)" strokeWidth="14" />
          </g>
        </svg>
      </div>
      <h1 className="intro-rise mt-9 font-display text-[3rem] leading-[52px] font-bold text-chalk" style={{ animationDelay: '800ms' }}>
        TretioDLSS
      </h1>
    </div>
  );
}

/** Capture cards large and first; webcams and software cameras below a divider. */
function DeviceList({ state, value, onPick }: { state: AppState; value: string; onPick: (id: string) => void }) {
  const cards = state.devices.filter((d) => deviceKind(d) === 'card');
  const others = sortedDevices(state.devices.filter((d) => deviceKind(d) !== 'card'));
  const best = (id: string) => {
    const f = [...(state.devices.find((d) => d.id === id)?.formats ?? [])].sort((a, b) => b.width * b.height - a.width * a.height || b.fps - a.fps)[0];
    return f ? `Capture card, up to ${f.width} x ${f.height} at ${Math.round(f.fps)} fps` : 'Capture card';
  };
  return (
    <div className="flex flex-col gap-3">
      {cards.map((d) => (
        <button
          key={d.id}
          type="button"
          onClick={() => onPick(d.id)}
          aria-pressed={value === d.id}
          className={`flex min-h-[104px] items-center gap-5 rounded-[22px] border bg-boulevard px-6 py-5 text-left transition-colors duration-200 ${
            value === d.id ? 'border-flamingo' : 'border-edge hover:border-edge-strong'
          }`}
        >
          <span className={`flex h-14 w-14 shrink-0 items-center justify-center rounded-2xl ${value === d.id ? 'bg-flamingo/15 text-flamingo' : 'bg-white/6 text-dusk'}`}>
            <Video size={28} aria-hidden />
          </span>
          <span className="min-w-0 flex-1">
            <span className="block truncate text-[1.25rem] leading-7 font-semibold text-chalk">{d.name}</span>
            <span className="block truncate text-[0.875rem] leading-5 text-dusk tabular-nums">{best(d.id)}</span>
          </span>
          {value === d.id && <Check size={22} className="shrink-0 text-flamingo" aria-hidden />}
        </button>
      ))}
      {cards.length > 0 && others.length > 0 && (
        <div className="mt-3 mb-1 flex items-center gap-3">
          <span className="eyebrow shrink-0 leading-4">Other video devices</span>
          <span className="h-px flex-1 bg-edge" aria-hidden />
        </div>
      )}
      {others.length > 0 && (
        <div className="grid grid-cols-1 gap-3 min-[760px]:grid-cols-2">
          {others.map((d) => (
            <ChoiceCard
              key={d.id}
              choice={{ value: d.id, label: d.name, detail: deviceDetail(d), icon: Video }}
              selected={value === d.id}
              onClick={() => onPick(d.id)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function ChoiceCard({ choice, selected, onClick }: { choice: Choice; selected: boolean; onClick: () => void }) {
  const Icon = choice.icon;
  return (
    <button
      type="button"
      disabled={choice.disabled}
      onClick={onClick}
      aria-pressed={selected}
      className={`flex min-h-[76px] items-center gap-4 rounded-[20px] border bg-boulevard px-5 py-4 text-left transition-colors duration-200 disabled:cursor-not-allowed disabled:opacity-40 ${
        selected ? 'border-flamingo' : 'border-edge enabled:hover:border-edge-strong'
      }`}
    >
      {Icon && <Icon size={22} className={`shrink-0 ${selected ? 'text-flamingo' : 'text-dusk'}`} aria-hidden />}
      <span className="min-w-0 flex-1">
        <span className="block truncate text-[1rem] leading-6 font-semibold text-chalk">{choice.label}</span>
        {choice.detail && <span className="block truncate text-[0.8125rem] leading-[18px] text-dusk">{choice.detail}</span>}
      </span>
      {selected && <Check size={20} className="shrink-0 text-flamingo" aria-hidden />}
    </button>
  );
}

/** The highest rate the output can show: the monitor's refresh, 120 for a TV through Moonlight. */
function maxRefresh(state: AppState, output: string): number {
  if (output.startsWith('tv:')) return 120;
  return state.monitors.find((m) => m.id === output)?.refresh ?? 60;
}

/** Whole multiples of the game's rate up to x4, within what the output shows. */
function outputRates(input: number, maxHz: number): Choice[] {
  if (!input) return [];
  return [1, 2, 3, 4]
    .map((k) => ({ k, fps: input * k }))
    .filter(({ k, fps }) => k === 1 || fps <= maxHz + 1)
    .map(({ k, fps }) => ({
      value: String(fps),
      label: `${fps} fps`,
      detail: k === 1 ? 'As the game runs, no frame generation' : `Frame generation x${k}`,
    }));
}
