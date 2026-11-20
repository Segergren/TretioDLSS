import { useCallback, useEffect, useRef, useState } from 'react';
import { TopBar, Page } from './Components/TopBar';
import { Toasts } from './Components/Toasts';
import { ReleaseNotesDialog } from './Components/ReleaseNotesDialog';
import { PlayPage } from './Pages/PlayPage';
import { SettingsPage } from './Pages/SettingsPage';
import { DiagnosticsPage } from './Pages/DiagnosticsPage';
import { useStore } from './lib/store';
import { Spinner } from './Components/StartButton';

export function App() {
  const { state, preview } = useStore();
  const [page, setPage] = useState<Page>('play');
  const [section, setSection] = useState<string | null>(null);
  const live = state?.pipeline.status === 'running';
  const backdrop = useBackdrop(live ? preview : null);

  const openSettings = useCallback((target: string) => {
    setSection(target);
    setPage('settings');
  }, []);

  return (
    <div className="relative flex h-full flex-col overflow-hidden">
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
      <Toasts />
      <ReleaseNotesDialog />
    </div>
  );
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
