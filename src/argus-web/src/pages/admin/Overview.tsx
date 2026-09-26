import { Link } from "react-router";
import { api, money, type IndexStatus, type Overview as OverviewData, type PacksStatus } from "../../api";
import { useAsync } from "../../components/useAsync";

export default function Overview() {
  const overview = useAsync(() => api.get<OverviewData>("/api/admin/overview"), []);
  const index = useAsync(() => api.get<IndexStatus>("/admin/index/status"), []);
  const packs = useAsync(() => api.get<PacksStatus>("/admin/packs"), []);
  const o = overview.value;
  const ix = index.value?.index;
  const up = o ? o.services.filter((s) => s.ok).length : 0;

  let indexTile = { value: "—", sub: "loading" };
  if (index.error) indexTile = { value: "unreachable", sub: index.error };
  else if (ix && !ix.repos) indexTile = { value: "empty", sub: "no repository indexed yet" };
  else if (ix) {
    const bad = (ix.stale ?? 0) || (ix.errored ?? 0);
    indexTile = { value: `${(ix.repos ?? 0) - bad}/${ix.repos}`, sub: ix.stale ? `${ix.stale} out of date` : ix.errored ? `${ix.errored} failing` : "repositories current" };
  }

  return (
    <div className="page">
      <h1>Overview</h1>
      <p className="lede">Everything the service is made of, at a glance.</p>
      {overview.error && <div className="msg bad">{overview.error}</div>}
      <div className="tiles">
        <div className="tile" data-testid="tile-services"><div className="tile-label">Services up</div><div className="tile-value">{o ? `${up}/${o.services.length}` : "—"}</div><div className="dim">model, gateway, embeddings</div></div>
        <div className="tile"><div className="tile-label">People</div><div className="tile-value">{o?.users.total ?? "—"}</div><div className="dim">{o ? `${o.users.admins} admin${o.users.admins === 1 ? "" : "s"}${o.users.disabled ? `, ${o.users.disabled} disabled` : ""}` : ""}</div></div>
        <div className="tile"><div className="tile-label">Spend</div><div className="tile-value">{o?.spend !== undefined ? money(o.spend) : "—"}</div><div className="dim">{o?.spend_error ?? "this budget period"}</div></div>
        <div className="tile" data-testid="tile-index"><div className="tile-label">Code index</div><div className="tile-value">{indexTile.value}</div><div className="dim">{indexTile.sub}</div></div>
        <div className="tile"><div className="tile-label">Knowledge packs</div><div className="tile-value">{packs.value?.packs.length ?? "—"}</div><div className="dim">installed</div></div>
      </div>
      {ix && !ix.repos && !index.error && (
        <div className="msg bad">
          <strong>No repository is indexed.</strong> The code tools have nothing to search yet. Start a pass on the <Link to="/manage/indexing">Indexing</Link> page.
        </div>
      )}
      {ix?.stale ? (
        <div className="msg bad">
          <strong>{ix.stale} repository(ies) have a stale index</strong>: {ix.stale_names?.join(", ")}. Answers about them come from old data. <Link to="/manage/indexing">Index now</Link>.
        </div>
      ) : null}
      <div className="card">
        <h2>Services</h2>
        <ul className="services">
          {(o?.services ?? []).map((s) => (
            <li key={s.name}>
              <span className={`dot ${s.ok ? "ok" : "bad"}`} aria-hidden /> <strong>{s.name}</strong>
              <span className="dim right">{s.ok ? `${s.detail} · ${s.ms} ms` : s.detail}</span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
