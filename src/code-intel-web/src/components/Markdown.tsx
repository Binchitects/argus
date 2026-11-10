import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import AnswerTable from "./AnswerTable";

// react-markdown renders no raw HTML: model output cannot inject markup or script.
export default function Markdown({ text }: { text: string }) {
  return (
    <div className="md">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ href, children }) => (
            <a href={href} target="_blank" rel="noreferrer noopener">
              {children}
            </a>
          ),
          // Sorts by a heading's click, and a longer one filters.
          table: ({ children }) => <AnswerTable>{children}</AnswerTable>,
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}
