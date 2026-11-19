import { useEffect, useId, useRef, useState, type ReactNode } from 'react';

interface ModalProps {
  open: boolean;
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}

/**
 * A modal dialog on the platform's own `<dialog>` element: it traps focus, closes on Escape, puts the rest of the page out of reach of
 * assistive technology and returns focus to whatever opened it. Content is only mounted while open.
 */
export function Modal({ open, title, onClose, children, footer, wide }: ModalProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      className={`modal${wide ? ' wide' : ''}`}
      aria-labelledby={titleId}
      onCancel={(e) => {
        e.preventDefault();
        onClose();
      }}
      onClick={(e) => {
        if (e.target === ref.current) onClose(); // a click on the backdrop
      }}
    >
      {open && (
        <div className="card modal-card">
          <div className="hd">
            <h2 id={titleId}>{title}</h2>
            <button type="button" className="btn sm gh" onClick={onClose} aria-label="Close">✕</button>
          </div>
          <div className="bd">{children}</div>
          {footer && <div className="ft">{footer}</div>}
        </div>
      )}
    </dialog>
  );
}

interface ConfirmTypedProps {
  open: boolean;
  title: string;
  /** What will happen, in plain words, including the blast radius ("3 held events will be discarded"). */
  consequence: ReactNode;
  /** The words the person must type to proceed (usually the object's name). */
  phrase: string;
  confirmLabel: string;
  busy?: boolean;
  error?: string | null;
  onConfirm: () => void;
  onClose: () => void;
}

/** Destructive actions ask the person to type the name of the thing they are about to affect (UX-SPEC section 9). */
export function ConfirmTyped({ open, title, consequence, phrase, confirmLabel, busy, error, onConfirm, onClose }: ConfirmTypedProps) {
  const [typed, setTyped] = useState('');
  const inputId = useId();
  useEffect(() => {
    if (!open) setTyped('');
  }, [open]);

  const matches = typed.trim() === phrase;
  return (
    <Modal
      open={open}
      title={title}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="button" className="btn dng" disabled={!matches || busy} onClick={onConfirm}>{busy ? 'Working…' : confirmLabel}</button>
        </>
      }
    >
      <div className="note warn">{consequence}</div>
      <div className="field" style={{ marginTop: 14 }}>
        <label className="f" htmlFor={inputId}>Type <b className="mono">{phrase}</b> to confirm</label>
        <input id={inputId} className="input mono" value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" spellCheck={false} />
      </div>
      {error && <div className="banner red" role="alert"><div className="grow">{error}</div></div>}
    </Modal>
  );
}

interface DrawerProps {
  open: boolean;
  title: ReactNode;
  onClose: () => void;
  children: ReactNode;
  footer?: ReactNode;
}

/**
 * A panel that slides in from the right (the delivery drawer). It is a modal dialog underneath, so the keyboard behaves: focus stays inside, Escape closes,
 * and focus goes back to the row that opened it. Content is only mounted while open.
 */
export function Drawer({ open, title, onClose, children, footer }: DrawerProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      className="drawer-dlg"
      aria-labelledby={titleId}
      onCancel={(e) => {
        e.preventDefault();
        onClose();
      }}
      onClick={(e) => {
        if (e.target === ref.current) onClose();
      }}
    >
      {open && (
        <div className="drawer-card">
          <div className="dh">
            <div className="grow" id={titleId}>{title}</div>
            <button type="button" className="btn sm gh" onClick={onClose} aria-label="Close">✕</button>
          </div>
          <div className="db">{children}</div>
          {footer && <div className="df">{footer}</div>}
        </div>
      )}
    </dialog>
  );
}
