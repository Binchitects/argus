import { useRef, useState, type FormEvent } from "react";
import { Navigate, useNavigate } from "react-router";
import { useAuth } from "../auth";
import Eyes from "../components/Eyes";
import Icon from "../components/Icon";

export default function Login() {
  const { me, login } = useAuth();
  const navigate = useNavigate();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const logo = useRef<HTMLSpanElement>(null);
  if (me) return <Navigate to="/" replace />;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(username, password);
      navigate("/");
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  // The logo, then its eyes, play behind the form; they keep off it (data-keep-clear).
  return (
    <div className="login-screen">
      <Eyes logo={logo} />
      <span ref={logo} className="logo hero eyes-logo"><Icon name="logo" size={50} /></span>
      <form className="card login" onSubmit={submit} aria-label="Sign in" data-keep-clear>
        <div className="brand big">Argus</div>
        <p className="dim">Your team's models, code and documentation, in one place.</p>
        {error && <div className="msg bad" role="alert">{error}</div>}
        <label>
          Username or email
          <input name="username" autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required autoFocus />
        </label>
        <label>
          Password
          <input name="password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        <button className="btn primary wide" type="submit" disabled={busy}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </div>
  );
}
