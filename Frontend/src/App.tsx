import { useCallback, useEffect, useRef, useState } from 'react';
import { TopBar, Page } from './Components/TopBar';
import { Toasts } from './Components/Toasts';
import { ReleaseNotesDialog } from './Components/ReleaseNotesDialog';
import { PlayPage } from './Pages/PlayPage';
import { SettingsPage } from './Pages/SettingsPage';
import { DiagnosticsPage } from './Pages/DiagnosticsPage';
import { useStore } from './lib/store';
import { Spinner } from './Components/StartButton';
import { Onboarding } from './Pages/Onboarding';
import { PinScreen } from './Components/PinScreen';
import { TvHelp } from './Components/TvHelp';

export function App() {
  const { state, preview } = useStore();
  const [page, setPage] = useState<Page>('play');
  const [section, setSection] = useState<string | null>(null);
  const live = state?.pipeline.status === 'running';
  const backdrop = useBackdrop(live ? preview : null);

  // Only right after the setup: the main view enters with a fade, not on every start.
  const onboarded = state?.settings.ui.onboarded;
  const wasOnboarding = useRef(false);
  const justOnboarded = useRef(false);
  const [entering, setEntering] = useState(false);
  useEffect(() => {
    if (onboarded === false) wasOnboarding.current = true;
    else if (onboarded && wasOnboarding.current) {
      wasOnboarding.current = false;
      justOnboarded.current = true;
      setEntering(true);
    }
  }, [onboarded]);

  // The TV guide covers the whole window, so it lives here, above the top bar.
  const [tvHelp, setTvHelp] = useState(false);
  useEffect(() => {
    const open = () => setTvHelp(true);
    window.addEventListener('tretio:tvhelp', open);
    return () => window.removeEventListener('tretio:tvhelp', open);
  }, []);
  const closeTvHelp = useCallback(() => setTvHelp(false), []);

  // Picking a TV that has never paired opens the guide by itself; it closes once the stream runs.
  const outputId = state?.settings.display.monitorId ?? '';
  const unpaired = (state?.stream.clients.length ?? 0) === 0;
  const streaming = !!state?.stream.session;
  const lastOutput = useRef<string | null>(null);
  useEffect(() => {
    if (!state || onboarded === false) return;
    const changed = justOnboarded.current || (lastOutput.current !== null && outputId !== lastOutput.current);
    justOnboarded.current = false;
    if (changed && outputId.startsWith('tv:') && unpaired) setTvHelp(true);
    lastOutput.current = outputId;
  }, [outputId, unpaired, onboarded, state]);
  useEffect(() => {
    if (streaming) setTvHelp(false);
  }, [streaming]);

  const openSettings = useCallback((target: string) => {
    setSection(target);
    setPage('settings');
  }, []);

  // Remembered between starts, so a first run shows nothing until the setup instead of flashing the main view.
  useEffect(() => {
    if (onboarded === undefined) return;
    try {
      localStorage.setItem(ONBOARDED_KEY, onboarded ? '1' : '0');
    } catch {
      // Without storage the main view simply shows while connecting.
    }
  }, [onboarded]);

  if (!state && !wasOnboardedBefore()) return <div className="h-full" />;

  // Overlays sit outside both views, so the guide stays put (and does not fade in twice) when the setup hands over.
  const overlays = (
    <>
      <Toasts />
      {onboarded !== false && <ReleaseNotesDialog />}
      {tvHelp && <TvHelp address={state?.stream.address} onClose={closeTvHelp} />}
      <PinScreen />
    </>
  );

  if (state?.settings.ui.onboarded === false) {
    return (
      <>
        <div className="relative flex h-full flex-col overflow-hidden">
          <Onboarding />
        </div>
        {overlays}
      </>
    );
  }

  return (
    <>
      <div
        className={`relative flex h-full flex-col overflow-hidden ${entering ? 'view-enter' : ''}`}
        onAnimationEnd={(e) => e.target === e.currentTarget && setEntering(false)}
      >
        {/* The picture tints the whole window while live, like a poster behind a player. */}
        <div
          className="backdrop-image"
          style={{ backgroundImage: backdrop ? `url(data:image/jpeg;base64,${backdrop})` : undefined, opacity: backdrop ? undefined : 0 }}
          aria-hidden
        />
        <div className="backdrop-fade" aria-hidden />
        <TopBar
          page={page}
          onNavigate={(p) => {
            setSection(null);
            setPage(p);
          }}
        />
        <main className="relative z-10 min-h-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
          {!state ? (
            <div className="flex h-full items-center justify-center gap-3 text-dusk">
              <Spinner />
              <span>Connecting</span>
            </div>
          ) : page === 'play' ? (
            <PlayPage onOpenSettings={openSettings} />
          ) : page === 'settings' ? (
            <SettingsPage section={section} />
          ) : (
            <DiagnosticsPage />
          )}
        </main>
      </div>
      {overlays}
    </>
  );
}

const ONBOARDED_KEY = 'tretio.onboarded';

function wasOnboardedBefore() {
  try {
    return localStorage.getItem(ONBOARDED_KEY) === '1';
  } catch {
    return true;
  }
}

/** The blurred window backdrop follows the picture once a second; re-blurring it at the preview rate costs GPU for nothing. */
function useBackdrop(preview: string | null) {
  const [frame, setFrame] = useState<string | null>(null);
  const latest = useRef(preview);
  latest.current = preview;
  useEffect(() => {
    if (!preview) {
      setFrame(null);
      return;
    }
    setFrame((f) => f ?? preview);
    const id = setInterval(() => setFrame(latest.current), 1000);
    return () => clearInterval(id);
  }, [preview === null]); // eslint-disable-line react-hooks/exhaustive-deps
  return frame;
}
