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
import type { ArgusRoute } from "./help";
import type { ReactNode } from "react";

function RequireUser({ children, admin = false }: { children: ReactNode; admin?: boolean }) {
  const { me, loading } = useAuth();
  if (loading) return <div className="center-screen dim">Loading…</div>;
  if (!me) return <Navigate to="/login" replace />;
  if (admin && me.user.role !== "admin") return <Navigate to="/" replace />;
  return <>{children}</>;
}

/**
 * Every page of the signed-in layout, by its whole path. The routes below are made from this
 * record and nothing else: a page needs its ArgusRoute, which needs its help in help.ts, or this
 * does not compile (test/routes.test.mjs fails for a <Route> written by hand).
 */
const pages: Record<Exclude<ArgusRoute, "/login">, { element: ReactNode; admin?: boolean }> = {
  "/": { element: <Chat /> },
  "/chat/:id": { element: <Chat /> },
  "/settings": { element: <Settings /> },
  "/help": { element: <HelpPage /> },
  "/manage": { element: <Overview />, admin: true },
  "/manage/people": { element: <People />, admin: true },
  "/manage/indexing": { element: <Indexing />, admin: true },
  "/manage/explore": { element: <Explore />, admin: true },
  "/manage/packs": { element: <Packs />, admin: true },
};

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
        {Object.entries(pages).map(([path, page]) => {
          const element = page.admin ? <RequireUser admin>{page.element}</RequireUser> : page.element;
          return path === "/" ? <Route key={path} index element={element} /> : <Route key={path} path={path.slice(1)} element={element} />;
        })}
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
