import { useState } from "react";

/** A secret shown exactly once, with a copy button and a plain warning. */
export default function Secret({ label, value, onDone }: { label: string; value: string; onDone: () => void }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="msg ok secret" role="status">
      <div>
        <strong>{label}</strong> — copy it now; it will not be shown again.
      </div>
      <div className="secret-row">
        <code data-testid="secret-value">{value}</code>
        <button
          className="btn small"
          onClick={async () => {
            try {
              await navigator.clipboard.writeText(value);
              setCopied(true);
            } catch {
              setCopied(false);
            }
          }}
        >
          {copied ? "Copied" : "Copy"}
        </button>
        <button className="btn small ghost" onClick={onDone}>
          Done
        </button>
      </div>
    </div>
  );
}
