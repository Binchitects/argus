export type Theme = "auto" | "light" | "dark";

export function currentTheme(): Theme {
  try {
    return (localStorage.getItem("argus.theme") as Theme) ?? "auto";
  } catch {
    return "auto";
  }
}

export function applyTheme(theme: Theme) {
  if (theme === "auto") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);
  try {
    localStorage.setItem("argus.theme", theme);
  } catch {
    /* private window */
  }
}
