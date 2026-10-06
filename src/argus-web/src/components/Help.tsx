import { useEffect, useRef, type RefObject } from "react";
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

/**
 * The page's help, beside it (on a wide screen the page makes room for it): it follows you from
 * page to page until closed. Esc closes it from inside it; an Esc in the page is the page's.
 */
export function HelpPanel({ onClose, button }: { onClose: () => void; button: RefObject<HTMLButtonElement | null> }) {
  const { pathname } = useLocation();
  const { me } = useAuth();
  const topic = helpFor(pathname, me?.user.role === "admin");
  const ref = useRef<HTMLElement>(null);
  // Focus goes into the panel as it opens.
  useEffect(() => ref.current?.focus(), []);
  const close = () => {
    const inside = ref.current?.contains(document.activeElement);
    onClose();
    if (inside) button.current?.focus();
  };
  return (
    <aside
      ref={ref}
      id="help-panel"
      className="help-panel"
      aria-labelledby="help-title"
      data-testid="help-panel"
      tabIndex={-1}
      onKeyDown={(e) => {
        if (e.key !== "Escape" || e.defaultPrevented) return;
        e.preventDefault();
        close();
      }}
    >
      <div className="row between">
        <h2 id="help-title">Help: {topic.title}</h2>
        <button className="btn small ghost" onClick={close} aria-label="Close the help">
          Close
        </button>
      </div>
      <TopicBody topic={topic} />
      <p>
        <Link to="/help">Every page's help</Link>
      </p>
    </aside>
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
