import { useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import { Link, useLocation } from "react-router";
import { useAuth } from "../auth";
import { help, helpFor, type ArgusRoute, type Topic } from "../help";

/** A page's help: what it is for, its parts, and the common tasks. */
export function TopicBody({ topic }: { topic: Topic }) {
  return (
    <>
      <p className="dim">{topic.about}</p>
      {topic.parts.length > 0 && (
        <dl className="help-parts">
          {topic.parts.map(([name, text]) => (
            <div key={name}>
              <dt>{name}</dt>
              <dd className="dim">{text}</dd>
            </div>
          ))}
        </dl>
      )}
      {topic.tasks.map((t) => (
        <div key={t.title}>
          <h3>{t.title}</h3>
          <ol>
            {t.steps.map((s) => (
              <li key={s}>{s}</li>
            ))}
          </ol>
        </div>
      ))}
    </>
  );
}

/** Wide enough for the help to sit beside the page, which makes room for it (styles.css says the same). */
const besideQuery = "(min-width: 1280px)";

function useBeside() {
  const [beside, setBeside] = useState(() => window.matchMedia(besideQuery).matches);
  useEffect(() => {
    const query = window.matchMedia(besideQuery);
    const changed = () => setBeside(query.matches);
    query.addEventListener("change", changed);
    return () => query.removeEventListener("change", changed);
  }, []);
  return beside;
}

/**
 * The page's help. On a wide screen it sits beside the page, which makes room for it: it follows
 * you from page to page until closed, and Esc closes it from inside it (an Esc in the page is the
 * page's). On a narrower screen it opens over the page as a dialog: the page waits behind it, and
 * Esc or a click beside it closes it.
 */
export function HelpPanel({ onClose, button }: { onClose: () => void; button: RefObject<HTMLButtonElement | null> }) {
  const { pathname } = useLocation();
  const { me } = useAuth();
  const topic = helpFor(pathname, me?.user.role === "admin");
  const beside = useBeside();
  const panel = useRef<HTMLElement>(null);
  const close = (focusBack: boolean) => {
    // Over the page, the dialog keeps the page inert until it is closed: closed first, so the focus can go back.
    if (panel.current instanceof HTMLDialogElement) panel.current.close();
    onClose();
    if (focusBack) button.current?.focus();
  };
  const body = (
    <>
      <div className="row between">
        <h2 id="help-title">Help: {topic.title}</h2>
        <button className="btn small ghost" onClick={() => close(true)} aria-label="Close the help">
          Close
        </button>
      </div>
      <TopicBody topic={topic} />
      <p>
        {/* Over the page, the page it leads to is the one to see. */}
        <Link to="/help" onClick={() => !beside && close(false)}>
          Every page's help
        </Link>
      </p>
    </>
  );
  return beside ? (
    <Beside panel={panel} close={close}>
      {body}
    </Beside>
  ) : (
    <Over panel={panel} close={close}>
      {body}
    </Over>
  );
}

interface PanelProps {
  panel: RefObject<HTMLElement | null>;
  close: (focusBack: boolean) => void;
  children: ReactNode;
}

function Beside({ panel, close, children }: PanelProps) {
  // Focus goes into the panel as it opens.
  useEffect(() => panel.current?.focus(), [panel]);
  return (
    <aside
      ref={panel}
      id="help-panel"
      className="help-panel"
      aria-labelledby="help-title"
      data-testid="help-panel"
      tabIndex={-1}
      onKeyDown={(e) => {
        if (e.key !== "Escape" || e.defaultPrevented) return;
        e.preventDefault();
        close(true);
      }}
    >
      {children}
    </aside>
  );
}

function Over({ panel, close, children }: PanelProps) {
  const dialog = panel as RefObject<HTMLDialogElement | null>;
  // A modal dialog: the page behind it is inert until it closes.
  useEffect(() => {
    const d = dialog.current!;
    if (!d.open) d.showModal();
    d.focus();
    return () => d.close();
  }, [dialog]);
  return (
    <dialog
      ref={dialog}
      id="help-panel"
      className="help-panel help-over"
      aria-labelledby="help-title"
      data-testid="help-panel"
      tabIndex={-1}
      onCancel={(e) => {
        e.preventDefault();
        close(true);
      }}
      // The backdrop is the dialog's own: a click on it lands on the dialog, not on what it holds.
      onClick={(e) => e.target === e.currentTarget && close(true)}
    >
      <div className="help-over-body">{children}</div>
    </dialog>
  );
}

/** /help: every page's help, the admins' pages for admins. */
export default function HelpPage() {
  const { me } = useAuth();
  const isAdmin = me?.user.role === "admin";
  const shown = (Object.entries(help) as [ArgusRoute, Topic][]).filter(([route, t]) => route !== "/help" && (!t.admin || isAdmin));
  return (
    <div className="page help-page">
      <h1>Help</h1>
      <p className="lede">What each page is for, what its parts do, and the common tasks. Help in the sidebar shows the page you are on.</p>
      {shown.map(([route, t]) => (
        <section key={route} className="card" id={route.slice(1).replace(/[/:]/g, "-") || "chat"}>
          <h2>{t.title}</h2>
          <TopicBody topic={t} />
        </section>
      ))}
    </div>
  );
}
