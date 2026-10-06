import { useEffect } from "react";
import { Link, useLocation } from "react-router";
import { useAuth } from "../auth";
import { help, helpFor, type ArgusRoute, type Topic } from "../help";

function TopicBody({ topic }: { topic: Topic }) {
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

/** The page's help, beside it: it follows you from page to page until closed (Esc). */
export function HelpPanel({ onClose }: { onClose: () => void }) {
  const { pathname } = useLocation();
  const { me } = useAuth();
  const topic = helpFor(pathname, me?.user.role === "admin");
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <aside className="help-panel" aria-labelledby="help-title" data-testid="help-panel">
      <div className="row between">
        <h2 id="help-title">Help: {topic.title}</h2>
        <button className="btn small ghost" onClick={onClose} aria-label="Close the help">
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
