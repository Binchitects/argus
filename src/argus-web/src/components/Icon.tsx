// Stroke icons, inline so they render identically everywhere (no icon font, no emoji).
const paths: Record<string, string> = {
  chat: "M4 5h16v11H8l-4 4z",
  settings: "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8zm0-5v3m0 12v3M3 12h3m12 0h3M5.6 5.6l2.1 2.1m8.6 8.6 2.1 2.1m0-12.8-2.1 2.1m-8.6 8.6-2.1 2.1",
  overview: "M4 4h7v7H4zm9 0h7v7h-7zM4 13h7v7H4zm9 0h7v7h-7z",
  people: "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8zm-7 10a7 7 0 0 1 14 0m1-10a3 3 0 1 0 0-6m2 16a6 6 0 0 0-3-5",
  indexing: "M20 12a8 8 0 1 1-2.3-5.7M20 4v5h-5",
  explore: "M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zm5-2 5 5",
  packs: "M5 4h14v16H5zm4 0v16m-4-8h4",
  theme: "M12 3a9 9 0 1 0 0 18V3z M12 3a9 9 0 0 1 0 18",
  plus: "M12 5v14M5 12h14",
  trash: "M4 7h16M9 7V4h6v3m-8 0 1 13h8l1-13",
  edit: "M4 20h4L19 9l-4-4L4 16z",
  stop: "M7 7h10v10H7z",
  send: "M4 12 20 4l-6 16-3-7z",
  tool: "M14 6a4 4 0 0 0 5 5l-9 9-3-3 9-9a4 4 0 0 0-2-2z",
  logo: "M12 5a7 7 0 1 0 0 14 7 7 0 0 0 0-14zm0 4.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z",
};

export default function Icon({ name, size = 16 }: { name: keyof typeof paths | string; size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" className="icon">
      <path d={paths[name] ?? ""} />
    </svg>
  );
}
