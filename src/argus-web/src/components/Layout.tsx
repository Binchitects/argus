import { useCallback, useEffect, useState } from "react";
import { NavLink, Outlet, useNavigate } from "react-router";
import { useAuth } from "../auth";
import { applyTheme, currentTheme, type Theme } from "../theme";
import { HelpPanel } from "./Help";
import Icon from "./Icon";

const userLinks = [
  { to: "/", label: "Chat", icon: "chat", end: true },
  { to: "/settings", label: "Settings & keys", icon: "settings" },
];
const adminLinks = [
  { to: "/manage", label: "Overview", icon: "overview", end: true },
  { to: "/manage/people", label: "People", icon: "people" },
  { to: "/manage/indexing", label: "Indexing", icon: "indexing" },
  { to: "/manage/explore", label: "Explore", icon: "explore" },
  { to: "/manage/packs", label: "Knowledge packs", icon: "packs" },
];

export default function Layout() {
  const { me, logout } = useAuth();
  const navigate = useNavigate();
  const [theme, setTheme] = useState<Theme>(currentTheme());
  const [helpOpen, setHelpOpen] = useState(false);
  const closeHelp = useCallback(() => setHelpOpen(false), []);
  useEffect(() => applyTheme(theme), [theme]);
  const next: Record<Theme, Theme> = { auto: "light", light: "dark", dark: "auto" };
  const user = me!.user;
  const about = me!.about;

  return (
    <div className="shell">
      <nav className="side" aria-label="Main">
        <div className="brand">
          <span className="logo"><Icon name="logo" size={18} /></span> Argus
        </div>
        <div className="group">Workspace</div>
        {userLinks.map((l) => (
          <NavLink key={l.to} to={l.to} end={l.end} className="item">
            <Icon name={l.icon} /> {l.label}
          </NavLink>
        ))}
        {user.role === "admin" && (
          <>
            <div className="group">Administration</div>
            {adminLinks.map((l) => (
              <NavLink key={l.to} to={l.to} end={l.end} className="item">
                <Icon name={l.icon} /> {l.label}
              </NavLink>
            ))}
          </>
        )}
        <div className="spacer" />
        <button className="item ghost" onClick={() => setHelpOpen((o) => !o)} aria-expanded={helpOpen} title="Help for this page">
          <Icon name="help" /> Help
        </button>
        <button className="item ghost" onClick={() => setTheme(next[theme])} title="Switch theme">
          <Icon name="theme" /> Theme: {theme}
        </button>
        <div className="who">
          <span className="avatar" aria-hidden>{(user.display_name || user.username).slice(0, 2).toUpperCase()}</span>
          <span className="who-name" title={user.email}>{user.display_name || user.username}</span>
          <button
            className="btn small"
            onClick={async () => {
              await logout();
              navigate("/login");
            }}
          >
            Sign out
          </button>
        </div>
        {/* LICENSING.md, additional term 7(b): the licence and the source, beside the version. */}
        <p className="about">
          <span aria-label="Version">v{about.version}</span> ·{" "}
          <a href={about.source} target="_blank" rel="noreferrer" title={`Licensed under the ${about.license}: its source is offered to everyone who uses it`}>
            Source
          </a>
        </p>
      </nav>
      <main className="main">
        <Outlet />
      </main>
      {helpOpen && <HelpPanel onClose={closeHelp} />}
    </div>
  );
}
