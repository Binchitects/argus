import { useId, useState } from "react";
import { useSearchParams } from "react-router";
import { days, label, presets, problem, type TimeWindow } from "./time-window";

/** The page's window, kept in its address (?from=&to=) so a link opens the same view and Back goes to the one before. */
export function useTimeWindow() {
  const [params, setParams] = useSearchParams();
  const current: TimeWindow = { from: params.get("from") ?? "", to: params.get("to") ?? "" };
  const set = (w: TimeWindow) =>
    setParams((p) => {
      const next = new URLSearchParams(p);
      if (w.from) next.set("from", w.from);
      else next.delete("from");
      if (w.to) next.set("to", w.to);
      else next.delete("to");
      return next;
    });
  const asked = days(current);
  return {
    window: current,
    set,
    /** The API's query for it: "" for the budget period. */
    query: asked ? `?from=${asked.from}&to=${asked.to}` : "",
    /** The window's days, said under the figures. */
    said: asked ? (asked.from === asked.to ? asked.from : `${asked.from} to ${asked.to}`) : "this budget period",
  };
}

const CUSTOM = "custom";

/** The window's control: its presets and a custom range of two days (both in). */
export function TimeWindowPicker({ value, onChange }: { value: TimeWindow; onChange: (w: TimeWindow) => void }) {
  const id = useId();
  const key = `${value.from}|${value.to}`;
  const known = presets.some((p) => `${p.from}|${p.to}` === key);
  const shown = days(value);
  const [custom, setCustom] = useState<{ from: string; to: string } | null>(null);
  const wrong = custom ? problem(custom.from, custom.to) : null;
  return (
    <div className="row" role="group" aria-label="Time window">
      <label className="inline">
        Time window
        <select
          value={custom ? CUSTOM : key}
          onChange={(e) => {
            if (e.target.value === CUSTOM) {
              setCustom({ from: shown?.from ?? "", to: shown?.to ?? "" });
              return;
            }
            setCustom(null);
            const [from = "", to = ""] = e.target.value.split("|");
            onChange({ from, to });
          }}
        >
          {presets.map((p) => (
            <option key={p.label} value={`${p.from}|${p.to}`}>{p.label}</option>
          ))}
          {!known && <option value={key}>{label(value)}</option>}
          <option value={CUSTOM}>Custom range…</option>
        </select>
      </label>
      {custom && (
        <form
          className="row"
          aria-label="Custom range"
          onSubmit={(e) => {
            e.preventDefault();
            if (wrong) return;
            onChange(custom);
            setCustom(null);
          }}
        >
          <label className="inline" htmlFor={`${id}-from`}>First day<input id={`${id}-from`} type="date" value={custom.from} onChange={(e) => setCustom({ ...custom, from: e.target.value })} /></label>
          <label className="inline" htmlFor={`${id}-to`}>Last day<input id={`${id}-to`} type="date" value={custom.to} onChange={(e) => setCustom({ ...custom, to: e.target.value })} /></label>
          <button className="btn primary small" type="submit" disabled={!!wrong}>Apply</button>
          <button className="btn ghost small" type="button" onClick={() => setCustom(null)}>Cancel</button>
          {wrong && <span className="bad small" role="alert">{wrong}</span>}
        </form>
      )}
    </div>
  );
}
