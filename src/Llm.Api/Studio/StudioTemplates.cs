namespace Llm.Api.Studio;

/// <summary>A workflow to start from.</summary>
public sealed record StudioTemplate(string Key, string Name, string Description, Flow Flow);

/// <summary>Workflows to start from, made of what people already ask the tasks and reviews for.</summary>
public static class StudioTemplates
{
    public static readonly IReadOnlyList<StudioTemplate> All =
    [
        new("digest", "Morning digest", "Every weekday at 8: the news on a subject from the web, short, by email.", new Flow
        {
            Trigger = new FlowTrigger { Kind = "schedule", Cron = "0 8 * * 1-5", TimeZone = "UTC" },
            Steps =
            [
                new FlowStep { Id = "news", Kind = "agent", Name = "Find the news", Tools = ["web"], Prompt = "Find what is new today about our field (change this: name the subjects). Five points at most, each with its link." },
                new FlowStep { Id = "mail", Kind = "email", Name = "Email it", Subject = "Your digest, {{now}}", Text = "{{news.text}}" },
            ],
        }),
        new("review", "Review each merge request", "When a merge request opens or changes in GitLab: a review of its changes, as a comment on it.", new Flow
        {
            Trigger = new FlowTrigger { Kind = "gitlab", Events = ["merge_request"] },
            Steps =
            [
                new FlowStep { Id = "review", Kind = "ask", Name = "Review it", Prompt = "Review this merge request: bugs first, then what is unclear. Short, with the lines.\n\n{{input.text}}" },
                new FlowStep { Id = "comment", Kind = "comment", Name = "Comment in GitLab", Text = "{{review.text}}" },
            ],
        }),
        new("triage", "Triage a room's questions", "A message in a room that starts with #triage: sorted, and answered there, or raised to you when it is urgent.", new Flow
        {
            Trigger = new FlowTrigger { Kind = "room", Keyword = "#triage" },
            Steps =
            [
                new FlowStep
                {
                    Id = "sort", Kind = "ask", Name = "Sort it", Json = true, Model = "small",
                    Prompt = "Sort this request: {\"urgent\": true or false, \"topic\": \"one or two words\", \"answer\": \"a short first answer\"}\n\n{{input.text}}",
                },
                new FlowStep
                {
                    Id = "urgent", Kind = "condition", Name = "Urgent?", Left = "{{sort.json.urgent}}", Op = "equals", Right = "true",
                    Then = [new FlowStep { Id = "raise", Kind = "approval", Name = "You decide", Question = "Urgent, from {{input.by}}: {{input.text}}. Answer in the room?" }],
                },
                new FlowStep { Id = "reply", Kind = "text", Name = "The answer", Text = "{{input.by}}, about {{sort.json.topic}}: {{sort.json.answer}}" },
            ],
        }),
        new("form", "A request form", "A form your team fills: the model drafts the answer, you approve it, and it is saved as a file.", new Flow
        {
            Trigger = new FlowTrigger
            {
                Kind = "form",
                Fields =
                [
                    new FlowField { Name = "subject", Label = "Subject", Kind = "text", Required = true },
                    new FlowField { Name = "kind", Label = "Kind", Kind = "choice", Choices = ["Question", "Request", "Idea"], Required = true },
                    new FlowField { Name = "details", Label = "Details", Kind = "long" },
                ],
            },
            Steps =
            [
                new FlowStep { Id = "draft", Kind = "ask", Name = "Draft the answer", Prompt = "Draft a helpful answer to this {{input.kind}} from {{input.by}}: {{input.subject}}\n\n{{input.details}}" },
                new FlowStep { Id = "check", Kind = "approval", Name = "Approve it", Question = "Send this answer to {{input.by}}?\n\n{{draft.text}}" },
                new FlowStep { Id = "save", Kind = "file", Name = "Save it", FileName = "answer-{{input.subject}}.md", Text = "# {{input.subject}}\n\n{{draft.text}}" },
            ],
        }),
        new("each", "One by one", "A list, each item handled the same way: here, a line of advice for each, brought together.", new Flow
        {
            Trigger = new FlowTrigger { Kind = "manual" },
            Steps =
            [
                new FlowStep { Id = "list", Kind = "ask", Name = "Make the list", Json = true, Prompt = "List three things to check before a release, as a JSON list of short strings. {{input.text}}" },
                new FlowStep
                {
                    Id = "each", Kind = "loop", Name = "For each", Over = "{{list.json}}", Most = 10,
                    Steps = [new FlowStep { Id = "advice", Kind = "ask", Name = "Advise", Model = "small", Prompt = "One sentence of advice on: {{item}}" }],
                },
                new FlowStep { Id = "all", Kind = "text", Name = "Together", Text = "{{each.text}}" },
            ],
        }),
        new("mention", "Answer a mention in GitLab", "When someone writes @arena in a comment on a merge request or issue: an answer from the code and the company's documents, as a comment.", new Flow
        {
            Trigger = new FlowTrigger { Kind = "gitlab", Events = ["comment"], Keyword = "@arena" },
            Steps =
            [
                new FlowStep { Id = "docs", Kind = "knowledge", Name = "Look it up", Prompt = "{{input.gitlab.title}} {{input.text}}" },
                new FlowStep
                {
                    Id = "answer", Kind = "agent", Name = "Answer it", Tools = ["argus"],
                    Prompt = "Answer the question in this comment, short and with links, from the code and these documents.\n\n{{input.text}}\n\nDocuments:\n{{docs.text}}",
                },
                new FlowStep { Id = "reply", Kind = "gitlab", Name = "Answer in GitLab", Action = "comment", Text = "{{answer.text}}" },
            ],
        }),
        new("shipped", "Label and announce merges", "When a merge request is merged into main or a release branch: labelled shipped, release notes drafted, and a room told.", new Flow
        {
            Trigger = new FlowTrigger { Kind = "gitlab", Events = ["merge_request_merged"], Branches = "main, release/*" },
            Steps =
            [
                new FlowStep { Id = "label", Kind = "gitlab", Name = "Label it", Action = "label", Labels = "shipped" },
                new FlowStep { Id = "notes", Kind = "ask", Name = "Release notes", Model = "small", Prompt = "Write one line of release notes, for users, about this change:\n\n{{input.text}}" },
                new FlowStep { Id = "tell", Kind = "notify", Name = "Tell me", Subject = "Shipped: {{input.gitlab.title}}", Text = "{{notes.text}}\n{{input.gitlab.url}}" },
            ],
        }),
        new("pipeline", "Explain a failed pipeline", "When a pipeline fails on main: why, from its jobs' logs, as a comment; and an issue when it fails again an hour later.", new Flow
        {
            Trigger = new FlowTrigger { Kind = "gitlab", Events = ["pipeline_failed"], Branches = "main" },
            Steps =
            [
                new FlowStep { Id = "why", Kind = "ask", Name = "Why it failed", Prompt = "Why did this pipeline fail? The likely cause and the fix, short.\n\n{{input.text}}" },
                new FlowStep { Id = "comment", Kind = "comment", Name = "Comment in GitLab", Text = "{{why.text}}" },
                new FlowStep
                {
                    Id = "issue", Kind = "gitlab", Name = "Open an issue", Action = "issue", Subject = "Pipeline failed on {{input.gitlab.branch}}", Labels = "ci",
                    Text = "{{why.text}}\n\n{{input.gitlab.url}}",
                },
            ],
        }),
        new("report", "Weekly data report", "Every Monday at 9: a question to a data source, a chart's numbers in words, by email (choose the data source).", new Flow
        {
            Trigger = new FlowTrigger { Kind = "schedule", Cron = "0 9 * * 1", TimeZone = "UTC" },
            Steps =
            [
                new FlowStep { Id = "numbers", Kind = "data", Name = "Ask the data", Prompt = "Totals by week for the last four weeks" },
                new FlowStep { Id = "words", Kind = "ask", Name = "In words", Model = "small", Prompt = "Say in three sentences what these numbers show:\n\n{{numbers.text}}" },
                new FlowStep { Id = "mail", Kind = "email", Name = "Email it", Subject = "Weekly report, {{now}}", Text = "{{words.text}}\n\n{{numbers.text}}" },
            ],
        }),
    ];
}
