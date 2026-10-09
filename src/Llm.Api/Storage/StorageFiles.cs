using System.Data.Common;
using System.Text;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Llm.Api.Storage;

/// <summary>A file of the chat as the storage page lists it: whose, from where, in which chat, and what it takes.</summary>
/// <param name="Origin">upload, picture, video, speech (made by those tools) or tool (Python's files, long tool results).</param>
/// <param name="State">chat (in a chat), assistant (an assistant's file only), deleted (in chats deleted under legal hold only) or none (in no chat).</param>
/// <param name="Bytes">What it takes: the file, its text, a video's sound track and the pages drawn of it.</param>
/// <param name="Held">Its owner is on legal hold: it is never deleted.</param>
public sealed record FileRow(Guid Id, Guid UserId, string Person, string? Email, string Name, string Kind, string ContentType, string Origin, string State,
    long Bytes, DateTimeOffset CreatedAt, Guid? ChatId, string? ChatTitle, bool Held);

/// <summary>Which files: each part narrows; null is any.</summary>
/// <param name="Before">Made before then (older than).</param>
/// <param name="Sort">size (largest first), new or old.</param>
/// <param name="Origins">Any of these origins (the clean-ups'); <paramref name="States"/> the same for states.</param>
public sealed record FileFilter(Guid? Person = null, string? Kind = null, string? Origin = null, string? State = null, string? Search = null,
    DateTimeOffset? From = null, DateTimeOffset? Before = null, long? MinBytes = null, string Sort = "size", int Take = 1000, IReadOnlyCollection<Guid>? Ids = null,
    IReadOnlyCollection<string>? Origins = null, IReadOnlyCollection<string>? States = null);

/// <summary>What a group of files takes, by where they came from, their kind and whether a chat still has them.</summary>
public sealed record FileGroup(string Origin, string Kind, string State, long Count, long Bytes);

/// <summary>A person's files: how many, what they take, and their room.</summary>
public sealed record PersonFiles(Guid Id, string UserName, string DisplayName, string? Email, long Count, long Bytes, int? OwnMegabytes, bool Held);

/// <summary>
/// The chat's files, which are kept in the app's database (chat_attachments, with the pages drawn of them):
/// listed with where each came from (a message of the person, or a tool's) and whether any chat still has
/// it, summed by kind and by person, and deleted, never one of a person on legal hold.
/// </summary>
public sealed class StorageFiles(AppDbContext db)
{
    /// <summary>What one file takes, in bytes: a file's bytes, its text, a video's sound and its drawn pages.</summary>
    private const string BytesOf = """
        coalesce(octet_length(a."Data"), 0) + coalesce(octet_length(a."Sound"), 0) + octet_length(a."Text")
          + coalesce((SELECT sum(octet_length(p."Data")) FROM attachment_pages p WHERE p."AttachmentId" = a."Id"), 0)
        """;

    /// <summary>
    /// Every file with where it came from and whether a chat has it. Messages (and those waiting their turn) name
    /// their files in a JSON list; the first message naming a file (a chat not deleted first) says where it came
    /// from: a tool's (by which tool) or the person's.
    /// </summary>
    private const string Files = $$"""
        WITH refs AS (
            SELECT r.id, m."Role" AS role, m."ToolName" AS tool, m."ConversationId" AS chat, m."CreatedAt" AS at, c."DeletedAt" IS NOT NULL AS hidden
            FROM chat_messages m
            JOIN conversations c ON c."Id" = m."ConversationId"
            CROSS JOIN LATERAL jsonb_array_elements_text(CASE WHEN jsonb_typeof(m."AttachmentsJson"::jsonb) = 'array' THEN m."AttachmentsJson"::jsonb ELSE '[]'::jsonb END) AS r(id)
            WHERE m."AttachmentsJson" IS NOT NULL
            UNION ALL
            -- A message waiting its turn (sent while the chat answered): its files are the person's, in that chat.
            SELECT r.id, 'user', NULL, q."ConversationId", q."CreatedAt", c."DeletedAt" IS NOT NULL
            FROM queued_messages q
            JOIN conversations c ON c."Id" = q."ConversationId"
            CROSS JOIN LATERAL jsonb_array_elements_text(CASE WHEN jsonb_typeof(q."AttachmentsJson"::jsonb) = 'array' THEN q."AttachmentsJson"::jsonb ELSE '[]'::jsonb END) AS r(id)
            WHERE q."AttachmentsJson" IS NOT NULL
        ),
        firsts AS (SELECT DISTINCT ON (id) id, role, tool, chat FROM refs ORDER BY id, hidden, at),
        lives AS (SELECT DISTINCT id FROM refs WHERE NOT hidden),
        files AS (
            SELECT a."Id" AS id, a."UserId" AS user_id, a."FileName" AS name, a."Kind" AS kind, a."ContentType" AS type, a."CreatedAt" AS created_at,
                {{BytesOf}} AS bytes, f.chat,
                CASE WHEN l.id IS NOT NULL THEN 'chat'
                     WHEN EXISTS (SELECT 1 FROM project_files pf WHERE pf."AttachmentId" = a."Id") THEN 'assistant'
                     WHEN f.id IS NOT NULL THEN 'deleted'
                     ELSE 'none' END AS state,
                CASE WHEN f.role IS DISTINCT FROM 'tool' THEN 'upload'
                     WHEN f.tool = 'generate_image' THEN 'picture'
                     WHEN f.tool = 'generate_video' THEN 'video'
                     WHEN f.tool = 'speak' THEN 'speech'
                     ELSE 'tool' END AS origin
            FROM chat_attachments a
            LEFT JOIN firsts f ON f.id = a."Id"::text
            LEFT JOIN lives l ON l.id = a."Id"::text
        )
        """;

    public static readonly IReadOnlySet<string> Origins = new HashSet<string>(StringComparer.Ordinal) { "upload", "picture", "video", "speech", "tool" };
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal) { "chat", "assistant", "deleted", "none" };

    /// <summary>The files the filter finds (at most its Take, in its order), and how many it finds in all and what they take.</summary>
    public async Task<(IReadOnlyList<FileRow> Rows, Amount Total)> ListAsync(FileFilter f, CancellationToken ct)
    {
        var where = new List<string>();
        var args = new List<NpgsqlParameter>();
        void Add(string condition, string name, object value, NpgsqlDbType type)
        {
            where.Add(condition);
            args.Add(new NpgsqlParameter(name, type) { Value = value });
        }
        if (f.Person is { } person)
        {
            Add("x.user_id = @person", "person", person, NpgsqlDbType.Uuid);
        }
        if (f.Kind is { Length: > 0 } kind)
        {
            Add("x.kind = @kind", "kind", kind, NpgsqlDbType.Text);
        }
        if (f.Origin is { Length: > 0 } origin)
        {
            Add("x.origin = @origin", "origin", origin, NpgsqlDbType.Text);
        }
        if (f.State is { Length: > 0 } state)
        {
            Add("x.state = @state", "state", state, NpgsqlDbType.Text);
        }
        if (f.Search?.Trim() is { Length: > 0 } search)
        {
            // Its name, its owner, or its chat's title.
            Add("(x.name ILIKE @q OR u.\"UserName\" ILIKE @q OR u.\"DisplayName\" ILIKE @q OR u.\"Email\" ILIKE @q OR c.\"Title\" ILIKE @q)", "q",
                "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%",
                NpgsqlDbType.Text);
        }
        if (f.From is { } from)
        {
            Add("x.created_at >= @from", "from", from.UtcDateTime, NpgsqlDbType.TimestampTz);
        }
        if (f.Before is { } before)
        {
            Add("x.created_at < @before", "before", before.UtcDateTime, NpgsqlDbType.TimestampTz);
        }
        if (f.MinBytes is > 0 and var min)
        {
            Add("x.bytes >= @min", "min", min, NpgsqlDbType.Bigint);
        }
        if (f.Origins is { } origins)
        {
            Add("x.origin = ANY(@origins)", "origins", origins.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
        }
        if (f.States is { } states)
        {
            Add("x.state = ANY(@states)", "states", states.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text);
        }
        if (f.Ids is { } ids)
        {
            Add("x.id = ANY(@ids)", "ids", ids.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid);
        }
        var order = f.Sort switch
        {
            "new" => "x.created_at DESC, x.id",
            "old" => "x.created_at, x.id",
            _ => "x.bytes DESC, x.id",
        };
        var sql = new StringBuilder(Files).Append("""
            SELECT x.id, x.user_id, u."UserName", u."DisplayName", u."Email", x.name, x.kind, x.type, x.origin, x.state, x.bytes, x.created_at,
                   x.chat, c."Title", u."LegalHoldSince" IS NOT NULL, count(*) OVER (), coalesce(sum(x.bytes) OVER (), 0)
            FROM files x
            JOIN "AspNetUsers" u ON u."Id" = x.user_id
            LEFT JOIN conversations c ON c."Id" = x.chat
            """);
        if (where.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", where);
        }
        sql.Append(" ORDER BY ").Append(order).Append(" LIMIT @take");
        args.Add(new NpgsqlParameter("take", NpgsqlDbType.Integer) { Value = Math.Clamp(f.Take, 1, 100_000) });
        var rows = new List<FileRow>();
        var total = Amount.None;
        await ReadAsync(sql.ToString(), args, r =>
        {
            var userName = r.GetString(2);
            var display = r.IsDBNull(3) ? "" : r.GetString(3);
            rows.Add(new FileRow(r.GetGuid(0), r.GetGuid(1), display.Length > 0 ? display : userName, r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetString(7), r.GetString(8), r.GetString(9), r.GetInt64(10), r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetGuid(12),
                r.IsDBNull(13) ? null : r.GetString(13), r.GetBoolean(14)));
            total = new Amount(r.GetInt64(15), Convert.ToInt64(r.GetValue(16), System.Globalization.CultureInfo.InvariantCulture));
        }, ct);
        return (rows, total);
    }

    /// <summary>Every file summed by where it came from, its kind and whether a chat has it.</summary>
    public async Task<IReadOnlyList<FileGroup>> GroupsAsync(CancellationToken ct)
    {
        var groups = new List<FileGroup>();
        await ReadAsync(Files + " SELECT origin, kind, state, count(*), coalesce(sum(bytes), 0) FROM files GROUP BY 1, 2, 3 ORDER BY 5 DESC", [], r =>
            groups.Add(new FileGroup(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), Convert.ToInt64(r.GetValue(4), System.Globalization.CultureInfo.InvariantCulture))), ct);
        return groups;
    }

    /// <summary>Everyone with files, or a room of their own: what they take, largest first.</summary>
    public async Task<IReadOnlyList<PersonFiles>> PeopleAsync(CancellationToken ct)
    {
        var people = new List<PersonFiles>();
        await ReadAsync($"""
            WITH mine AS (SELECT a."UserId" AS user_id, count(*) AS n, coalesce(sum({BytesOf}), 0) AS bytes FROM chat_attachments a GROUP BY a."UserId")
            SELECT u."Id", u."UserName", u."DisplayName", u."Email", coalesce(m.n, 0), coalesce(m.bytes, 0), q."Megabytes", u."LegalHoldSince" IS NOT NULL
            FROM "AspNetUsers" u
            LEFT JOIN mine m ON m.user_id = u."Id"
            LEFT JOIN storage_quotas q ON q."UserId" = u."Id"
            WHERE m.user_id IS NOT NULL OR q."UserId" IS NOT NULL
            ORDER BY 6 DESC, 2
            """, [], r => people.Add(new PersonFiles(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt64(4), Convert.ToInt64(r.GetValue(5), System.Globalization.CultureInfo.InvariantCulture), r.IsDBNull(6) ? null : r.GetInt32(6), r.GetBoolean(7))), ct);
        return people;
    }

    /// <summary>What one person's files take.</summary>
    public async Task<long> UsedByAsync(Guid userId, CancellationToken ct)
    {
        long used = 0;
        await ReadAsync($"""SELECT coalesce(sum({BytesOf}), 0) FROM chat_attachments a WHERE a."UserId" = @user""",
            [new NpgsqlParameter("user", NpgsqlDbType.Uuid) { Value = userId }], r => used = Convert.ToInt64(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture), ct);
        return used;
    }

    /// <summary>
    /// Deletes files for good (their pages and passages go with them; a message that named one shows it no more),
    /// never one whose owner is on legal hold. What went, by person, and what the hold kept.
    /// </summary>
    public async Task<(IReadOnlyDictionary<Guid, Amount> Deleted, Amount Held)> DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return (new Dictionary<Guid, Amount>(), Amount.None);
        }
        var (rows, _) = await ListAsync(new FileFilter(Ids: ids, Take: ids.Count), ct);
        var held = rows.Where(r => r.Held).Aggregate(Amount.None, (a, r) => a.Plus(new Amount(1, r.Bytes)));
        var go = rows.Where(r => !r.Held).ToList();
        var deleted = new Dictionary<Guid, Amount>();
        foreach (var batch in go.Chunk(500))
        {
            var batchIds = batch.Select(r => r.Id).ToList();
            // Checked again as it goes: a hold placed meanwhile wins.
            await db.ChatAttachments.Where(a => batchIds.Contains(a.Id) && db.Users.Any(u => u.Id == a.UserId && u.LegalHoldSince == null)).ExecuteDeleteAsync(ct);
        }
        var goIds = go.Select(r => r.Id).ToList();
        var left = (await db.ChatAttachments.AsNoTracking().Where(a => goIds.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        foreach (var r in go.Where(r => !left.Contains(r.Id)))
        {
            deleted[r.UserId] = deleted.GetValueOrDefault(r.UserId, Amount.None).Plus(new Amount(1, r.Bytes));
        }
        foreach (var r in go.Where(r => left.Contains(r.Id)))
        {
            held = held.Plus(new Amount(1, r.Bytes));
        }
        return (deleted, held);
    }

    private async Task ReadAsync(string sql, IReadOnlyList<NpgsqlParameter> args, Action<DbDataReader> row, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await db.Database.OpenConnectionAsync(ct);
        }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 120;
            foreach (var a in args)
            {
                cmd.Parameters.Add(a);
            }
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                row(reader);
            }
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
