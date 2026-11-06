using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// What to do with one skill of an import: bring it in (import), put it in place of the person's own of its name (replace),
/// bring it in under another name (rename, with <see cref="Name"/>), or leave it (skip).
/// </summary>
public sealed record SkillImportChoice(string Folder, string? Action = null, string? Name = null);

/// <summary>
/// Skills exported and imported as files (Workspace → Skills → Export, Import; Code Arena's IDE and /skills import): any
/// skill the person may use goes out as its SKILL.md folder; one brought in is theirs, made as one made on the page is
/// (the same checks, the same permission), its text through the safeguards' file rules first. A name the person has
/// already is replaced, renamed or skipped, as they chose in the preview; nothing they did not choose is overwritten.
/// </summary>
public static partial class SkillEndpoints
{
    /// <summary>One skill as an import's preview shows it, and what the import would do with it.</summary>
    internal sealed record Previewed(SkillImport Skill, string? Problem, string? NameProblem, Skill? Clash, string? Shadows, string Suggested, bool Masked);

    /// <summary>
    /// The skills asked for, as a .zip of their folders; one alone as its SKILL.md with <paramref name="format"/> md (when
    /// it has no other files). Only skills the person may use.
    /// </summary>
    private static async Task<IResult> ExportAsync(string? ids, string? format, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db,
        CancellationToken ct)
    {
        var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).ToHashSet();
        if (wanted.Count == 0 || wanted.Contains(Guid.Empty))
        {
            return AuthEndpoints.Problem(400, "ids", "Say which skills to export (ids=…).");
        }
        var who = await ViewerAsync(p, users, access, db, ct);
        var skills = (await db.Skills.AsNoTracking().Where(x => wanted.Contains(x.Id)).ToListAsync(ct)).Where(who.Sees).OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        if (skills.Count != wanted.Count)
        {
            return Results.NotFound();
        }
        if (format == "md")
        {
            if (skills is not [var one] || SkillTransfer.FilesOf(one).Count > 0)
            {
                return AuthEndpoints.Problem(400, "format", "Only one skill with no other files goes out as its SKILL.md: export it as a .zip.");
            }
            return Results.File(Encoding.UTF8.GetBytes(SkillLibrary.ToSkillMd(one)), "text/markdown; charset=utf-8", SkillTransfer.SkillMd);
        }
        // Two skills of one name (a person's and the company's): the person's goes, as they use it.
        var named = skills.GroupBy(s => s.Name, StringComparer.Ordinal).Select(g => g.OrderBy(s => Rank(s, who.Me.Id)).First()).ToList();
        return Results.File(SkillTransfer.Zip(named), "application/zip", named is [var only] ? $"{only.Name}.zip" : "skills.zip");
    }

    /// <summary>A form's files as an import reads them: each by its path in the folder chosen (the browser sends it as the file's name).</summary>
    private static async Task<(List<SkillTransfer.Upload>? Uploads, string? Choices, IResult? Problem)> UploadsAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = SkillTransfer.MaxUpload + 1024 * 1024;
        }
        if (!request.HasFormContentType)
        {
            return (null, null, AuthEndpoints.Problem(400, "file", "Send the skills as files: a .zip, a SKILL.md, or a folder's files."));
        }
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Sum(f => f.Length) > SkillTransfer.MaxUpload)
        {
            return (null, null, AuthEndpoints.Problem(413, "file", $"An import is at most {SkillTransfer.MaxUpload / 1024 / 1024} MB."));
        }
        var uploads = new List<SkillTransfer.Upload>();
        foreach (var file in form.Files)
        {
            using var m = new MemoryStream();
            await file.CopyToAsync(m, ct);
            uploads.Add(new SkillTransfer.Upload(file.FileName, m.ToArray()));
        }
        return (uploads, form["choices"].ToString(), null);
    }

    private static async Task<IResult> ImportPreviewAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db,
        Safeguards.Safeguards safeguards, CancellationToken ct)
    {
        var (uploads, _, problem) = await UploadsAsync(request, ct);
        if (problem is not null)
        {
            return problem;
        }
        try
        {
            var (folders, leftOut) = SkillTransfer.Read(uploads!);
            return Results.Ok(await PreviewAsync((await users.GetUserAsync(p))!, folders, leftOut, access, db, safeguards, ct));
        }
        catch (SkillTransfer.TransferException e)
        {
            return AuthEndpoints.Problem(400, "file", e.Message);
        }
    }

    private static async Task<IResult> ImportFilesAsync(HttpRequest request, ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, AppDbContext db,
        Safeguards.Safeguards safeguards, Audit audit, CancellationToken ct)
    {
        var (uploads, choices, problem) = await UploadsAsync(request, ct);
        if (problem is not null)
        {
            return problem;
        }
        List<SkillImportChoice> chosen;
        try
        {
            chosen = choices is { Length: > 0 } c ? JsonSerializer.Deserialize<List<SkillImportChoice>>(c, JsonSerializerOptions.Web) ?? [] : [];
        }
        catch (JsonException)
        {
            return AuthEndpoints.Problem(400, "choices", "The choices are a list of { folder, action, name }.");
        }
        try
        {
            var (folders, _) = SkillTransfer.Read(uploads!);
            return Results.Ok(await ImportAsync((await users.GetUserAsync(p))!, folders, chosen, access, db, safeguards, audit, ct));
        }
        catch (SkillTransfer.TransferException e)
        {
            return AuthEndpoints.Problem(400, "file", e.Message);
        }
    }

    /// <summary>
    /// Each folder read as a skill and looked at as the import would: its problem, if any (the safeguards' too), a name that
    /// will not do, the person's own skill of its name (to replace, rename or skip), another's it would come before.
    /// </summary>
    internal static async Task<List<Previewed>> LookAsync(AppUser me, IReadOnlyList<SkillFolder> folders, AccessService access, AppDbContext db, Safeguards.Safeguards safeguards,
        CancellationToken ct)
    {
        var mine = await db.Skills.AsNoTracking().Where(x => x.UserId == me.Id).ToListAsync(ct);
        var others = (await UsableAsync(me, access, db, ct)).Where(x => x.UserId != me.Id).ToList();
        var taken = mine.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var list = new List<Previewed>();
        foreach (var folder in folders)
        {
            var skill = SkillTransfer.Parse(folder);
            var problem = skill.Problem;
            var masked = false;
            if (problem is null)
            {
                // Every file through the file rules: a secret or an injection in a skill is refused as in an attachment.
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                var checkedMd = await safeguards.CheckFileAsync(me, $"{folder.Folder}/{SkillTransfer.SkillMd}", skill.Instructions, ct, skill.Bytes);
                if (!checkedMd.Allowed)
                {
                    problem = $"The safeguards refused its SKILL.md: {checkedMd.Reason}";
                }
                foreach (var (path, text) in skill.Files)
                {
                    if (problem is not null)
                    {
                        break;
                    }
                    var verdict = await safeguards.CheckFileAsync(me, $"{folder.Folder}/{path}", text, ct, Encoding.UTF8.GetByteCount(text));
                    if (!verdict.Allowed)
                    {
                        problem = $"The safeguards refused {path}: {verdict.Reason}";
                    }
                    files[path] = verdict.Text ?? text;
                    masked |= verdict.Text is not null;
                }
                masked |= checkedMd.Text is not null;
                skill = skill with { Instructions = checkedMd.Text ?? skill.Instructions, Files = files };
            }
            var nameProblem = SkillLibrary.NameProblem(skill.Name);
            var clash = nameProblem is null ? mine.FirstOrDefault(x => x.Name == skill.Name) : null;
            var shadows = others.FirstOrDefault(x => x.Name == skill.Name) is { } other ? Source(other, me.Id) : null;
            // A name to rename it to: its own with a number, free among the person's and this import's.
            var stem = nameProblem is null ? skill.Name : Slug(skill.Name);
            var suggested = stem;
            for (var n = 2; taken.Contains(suggested) || list.Any(x => x.Suggested == suggested || x.Skill.Name == suggested && x.Skill != skill); n++)
            {
                var tail = "-" + n;
                suggested = (stem.Length + tail.Length > SkillLibrary.MaxName ? stem[..(SkillLibrary.MaxName - tail.Length)] : stem) + tail;
            }
            list.Add(new Previewed(skill, problem, nameProblem, clash, shadows, suggested, masked));
        }
        return list;
    }

    /// <summary>A name made a slash name: lowercase, other characters as -, at most <see cref="SkillLibrary.MaxName"/>.</summary>
    private static string Slug(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' ? ch : '-')]).Trim('-', '_');
        slug = slug.Length > SkillLibrary.MaxName ? slug[..SkillLibrary.MaxName].TrimEnd('-', '_') : slug;
        return slug.Length == 0 ? "skill" : slug;
    }

    /// <summary>What an import's preview says: each skill, what it brings, and what is in the way.</summary>
    internal static async Task<object> PreviewAsync(AppUser me, IReadOnlyList<SkillFolder> folders, IReadOnlyList<string> leftOut, AccessService access, AppDbContext db,
        Safeguards.Safeguards safeguards, CancellationToken ct)
    {
        var looked = await LookAsync(me, folders, access, db, safeguards, ct);
        return new
        {
            skills = looked.Select(x => new
            {
                folder = x.Skill.Folder, x.Skill.Name, x.Skill.Title, x.Skill.Description, parameters = x.Skill.Parameters.Count,
                files = x.Skill.Files.Select(f => new { path = f.Key, bytes = Encoding.UTF8.GetByteCount(f.Value) }), x.Skill.Bytes, leftOut = x.Skill.LeftOut,
                x.Problem, x.NameProblem, clash = x.Clash is { } c ? new { c.Id, c.Title, c.Version } : null, x.Shadows, x.Suggested, x.Masked,
            }),
            leftOut,
            room = Math.Max(0, PerPerson - await db.Skills.CountAsync(x => x.UserId == me.Id, ct)),
        };
    }

    /// <summary>
    /// The import as chosen: each skill made the person's own, put in place of theirs of its name (replace), made under
    /// another name (rename), or left (skip, and any it cannot be). A clash not chosen for is left, never overwritten.
    /// </summary>
    internal static async Task<object> ImportAsync(AppUser me, IReadOnlyList<SkillFolder> folders, IReadOnlyList<SkillImportChoice> choices, AccessService access, AppDbContext db,
        Safeguards.Safeguards safeguards, Audit audit, CancellationToken ct)
    {
        var looked = await LookAsync(me, folders, access, db, safeguards, ct);
        var imported = new List<object>();
        var names = new List<string>();
        var skipped = new List<object>();
        foreach (var x in looked)
        {
            var choice = choices.FirstOrDefault(c => c.Folder == x.Skill.Folder);
            var action = choice?.Action ?? (x.Clash is null && x.NameProblem is null ? "import" : "skip");
            void Skip(string why) => skipped.Add(new { folder = x.Skill.Folder, x.Skill.Name, reason = why });
            if (action == "skip")
            {
                Skip(x.Clash is not null ? $"You have /{x.Skill.Name} already." : x.NameProblem ?? "Skipped.");
                continue;
            }
            if (x.Problem is { } problem)
            {
                Skip(problem);
                continue;
            }
            var name = action == "rename" ? choice?.Name?.Trim().TrimStart('/').ToLowerInvariant() ?? x.Suggested : x.Skill.Name;
            var request = new SkillRequest(name, x.Skill.Title, x.Skill.Description, x.Skill.Instructions, [.. x.Skill.Parameters], Files: new(x.Skill.Files));
            IResult? refused;
            Skill? done;
            if (action == "replace")
            {
                if (x.Clash is null)
                {
                    Skip($"You have no /{x.Skill.Name} to replace.");
                    continue;
                }
                (done, refused) = await ChangeAsync(me, x.Clash.Id, request, access, db, audit, ct);
            }
            else if (action is "import" or "rename")
            {
                if (action == "import" && x.Clash is not null)
                {
                    Skip($"You have /{x.Skill.Name} already: replace it, import it under another name, or skip it.");
                    continue;
                }
                (done, refused) = await MakeAsync(me, request, access, db, audit, ct);
            }
            else
            {
                Skip($"\"{action}\" is not a choice: import, replace, rename or skip.");
                continue;
            }
            if (refused is not null)
            {
                Skip(Said(refused));
                continue;
            }
            imported.Add(new { folder = x.Skill.Folder, done!.Id, done.Name, replaced = action == "replace" });
            names.Add("/" + done.Name);
        }
        if (names.Count > 0)
        {
            await audit.WriteAsync("skill.import", $"{names.Count} skill{(names.Count == 1 ? "" : "s")}", detail: string.Join(", ", names));
        }
        return new { imported, skipped };
    }

    /// <summary>What a refusal says (its error, as AuthEndpoints.Problem words it), for the import's list of what was left.</summary>
    private static string Said(IResult refused) =>
        refused is IValueHttpResult { Value: { } value } && JsonSerializer.SerializeToElement(value).TryGetProperty("error", out var error) ? error.GetString() ?? "Refused." : "Refused.";
}
