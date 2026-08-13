/** Wires the Blazor reconnect modal to the framework reconnect/resume APIs. */
function handleReconnectStateChanged(event: Event): void {
  const modal = document.getElementById('components-reconnect-modal') as HTMLDialogElement | null;
  const detail = (event as CustomEvent<{ state: string }>).detail;
  if (!modal) return;

  if (detail.state === 'show') modal.showModal();
  else if (detail.state === 'failed') document.addEventListener('visibilitychange', retryWhenDocumentBecomesVisible);
  else if (detail.state === 'rejected') location.reload();
  else if (detail.state === 'hide') modal.close();
}

async function retry(): Promise<void> {
  document.removeEventListener('visibilitychange', retryWhenDocumentBecomesVisible);
  const modal = document.getElementById('components-reconnect-modal') as HTMLDialogElement | null;
  try {
    const successful = await Blazor.reconnect();
    if (!successful) {
      const resumeSuccessful = await Blazor.resumeCircuit();
      if (!resumeSuccessful) location.reload();
      else modal?.close();
    }
  } catch {
    document.addEventListener('visibilitychange', retryWhenDocumentBecomesVisible);
  }
}

async function resume(): Promise<void> {
  const modal = document.getElementById('components-reconnect-modal');
  try {
    const successful = await Blazor.resumeCircuit();
    if (!successful) location.reload();
  } catch {
    modal?.classList.replace('components-reconnect-paused', 'components-reconnect-resume-failed');
  }
}

async function retryWhenDocumentBecomesVisible(): Promise<void> {
  if (document.visibilityState === 'visible') await retry();
}

declare global {
  interface Window {
    Blazor: {
      reconnect(): Promise<boolean>;
      resumeCircuit(): Promise<boolean>;
    };
  }
}

const reconnectModal = document.getElementById('components-reconnect-modal');
reconnectModal?.addEventListener('components-reconnect-state-changed', handleReconnectStateChanged);
document.getElementById('components-reconnect-button')?.addEventListener('click', () => void retry());
document.getElementById('components-resume-button')?.addEventListener('click', () => void resume());
