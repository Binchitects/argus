import { Navigate, Route, Routes } from "react-router";
import { AuthProvider, useAuth } from "./auth";
import Layout from "./components/Layout";
import Login from "./pages/Login";
import Chat from "./pages/Chat";
import Settings from "./pages/Settings";
import Overview from "./pages/admin/Overview";
import People from "./pages/admin/People";
import Indexing from "./pages/admin/Indexing";
import Explore from "./pages/admin/Explore";
import Packs from "./pages/admin/Packs";
import HelpPage from "./components/Help";
import { inLayout, type ArgusRoute } from "./help";
import type { ReactNode } from "react";

function RequireUser({ children, admin = false }: { children: ReactNode; admin?: boolean }) {
  const { me, loading } = useAuth();
  if (loading) return <div className="center-screen dim">Loading…</div>;
  if (!me) return <Navigate to="/login" replace />;
  if (admin && me.user.role !== "admin") return <Navigate to="/" replace />;
  return <>{children}</>;
}

// Each path is an ArgusRoute: help.ts has the help for it, or this does not compile.
function Routed() {
  return (
    <Routes>
      <Route path={"/login" satisfies ArgusRoute} element={<Login />} />
      <Route
        element={
          <RequireUser>
            <Layout />
          </RequireUser>
        }
      >
        <Route index element={<Chat />} />
        <Route path={inLayout("/chat/:id")} element={<Chat />} />
        <Route path={inLayout("/settings")} element={<Settings />} />
        <Route path={inLayout("/help")} element={<HelpPage />} />
        <Route path={inLayout("/manage")} element={<RequireUser admin><Overview /></RequireUser>} />
        <Route path={inLayout("/manage/people")} element={<RequireUser admin><People /></RequireUser>} />
        <Route path={inLayout("/manage/indexing")} element={<RequireUser admin><Indexing /></RequireUser>} />
        <Route path={inLayout("/manage/explore")} element={<RequireUser admin><Explore /></RequireUser>} />
        <Route path={inLayout("/manage/packs")} element={<RequireUser admin><Packs /></RequireUser>} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

export default function App() {
  return (
    <AuthProvider>
      <Routed />
    </AuthProvider>
  );
}
