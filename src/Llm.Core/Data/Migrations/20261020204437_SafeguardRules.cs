using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class SafeguardRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "safeguard_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Monitor = table.Column<bool>(type: "boolean", nullable: false),
                    Detector = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stages = table.Column<List<string>>(type: "text[]", nullable: false),
                    Groups = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    People = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    Models = table.Column<List<string>>(type: "text[]", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RerouteModel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Strike = table.Column<bool>(type: "boolean", nullable: false),
                    Kinds = table.Column<List<string>>(type: "text[]", nullable: false),
                    Patterns = table.Column<string>(type: "text", nullable: true),
                    Topics = table.Column<string>(type: "text", nullable: true),
                    Categories = table.Column<List<string>>(type: "text[]", nullable: false),
                    Domains = table.Column<List<string>>(type: "text[]", nullable: false),
                    DomainsAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    Extensions = table.Column<List<string>>(type: "text[]", nullable: false),
                    MaxFileMb = table.Column<int>(type: "integer", nullable: true),
                    ModelCheck = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_safeguard_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_safeguard_rules_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "safeguard_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Detector = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Monitor = table.Column<bool>(type: "boolean", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Excerpt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Review = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReviewReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AllowedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_safeguard_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_safeguard_events_AspNetUsers_ReviewedById",
                        column: x => x.ReviewedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_safeguard_events_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_safeguard_events_safeguard_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "safeguard_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_safeguard_events_At",
                table: "safeguard_events",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_safeguard_events_Review",
                table: "safeguard_events",
                column: "Review");

            migrationBuilder.CreateIndex(
                name: "IX_safeguard_events_ReviewedById",
                table: "safeguard_events",
                column: "ReviewedById");

            migrationBuilder.CreateIndex(
                name: "IX_safeguard_events_RuleId",
                table: "safeguard_events",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_safeguard_events_UserId_At",
                table: "safeguard_events",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_safeguard_rules_CreatedById",
                table: "safeguard_rules",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_safeguard_rules_Order",
                table: "safeguard_rules",
                column: "Order");

            // The switches become rules: the groups' own first (in their names' order), then the company's for everyone.
            migrationBuilder.Sql(Convert);

            migrationBuilder.DropColumn(
                name: "BlockedPatterns",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "Moderation",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "RedactPii",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "SecretScanning",
                table: "groups");
        }

        private const string Columns = """
            ("Id", "Name", "Description", "Order", "Enabled", "Monitor", "Detector", "Stages", "Groups", "People", "Models", "Action", "Strike",
             "Kinds", "Patterns", "Topics", "Categories", "Domains", "DomainsAllowed", "Extensions", "ModelCheck", "CreatedAt", "UpdatedAt")
            """;

        private const string Setting = "(SELECT value FROM settings WHERE key = 'config:Safeguards:{0}')";

        private static string S(string key) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Setting, key);

        private static readonly string Harms = $"string_to_array(coalesce(nullif(trim({S("ModerationCategories")}), ''), 'violence,self-harm,sexual-minors,weapons,malware,hate'), ',')";

        private static string Convert => $$"""
            INSERT INTO safeguard_rules {{Columns}}
            SELECT gen_random_uuid(), g."Name" || ': secrets', 'Converted from the group''s secret scanning.', (row_number() OVER (ORDER BY g."Name"))::int, true, false,
                   'secrets', ARRAY['question','file','api'], ARRAY[g."Id"], '{}', '{}',
                   CASE g."SecretScanning" WHEN 'off' THEN 'allow' WHEN 'mask' THEN 'mask' ELSE 'block' END, false,
                   '{}', NULL, NULL, '{}', '{}', false, '{}', false, now(), now()
            FROM groups g WHERE g."SecretScanning" IS NOT NULL;

            INSERT INTO safeguard_rules {{Columns}}
            SELECT gen_random_uuid(), g."Name" || ': personal data', 'Converted from the group''s personal data setting.', 20 + (row_number() OVER (ORDER BY g."Name"))::int, true, false,
                   'pii', ARRAY['question','api'], ARRAY[g."Id"], '{}', '{}',
                   CASE g."RedactPii" WHEN 'off' THEN 'allow' ELSE 'mask' END, false,
                   '{}', NULL, NULL, '{}', '{}', false, '{}', false, now(), now()
            FROM groups g WHERE g."RedactPii" IS NOT NULL;

            INSERT INTO safeguard_rules {{Columns}}
            SELECT gen_random_uuid(), g."Name" || ': harmful requests', 'Converted from the group''s model check.', 40 + (row_number() OVER (ORDER BY g."Name"))::int, true, false,
                   'topic', ARRAY['question','api'], ARRAY[g."Id"], '{}', '{}',
                   CASE g."Moderation" WHEN 'off' THEN 'allow' ELSE 'block' END, g."Moderation" <> 'off',
                   '{}', NULL, NULL, {{Harms}}, '{}', false, '{}', false, now(), now()
            FROM groups g WHERE g."Moderation" IS NOT NULL;

            INSERT INTO safeguard_rules {{Columns}}
            SELECT gen_random_uuid(), g."Name" || ': blocked words off', 'Converted from the group''s blocked words setting.', 60 + (row_number() OVER (ORDER BY g."Name"))::int, true, false,
                   'words', ARRAY['question','api'], ARRAY[g."Id"], '{}', '{}', 'allow', false,
                   '{}', NULL, NULL, '{}', '{}', false, '{}', false, now(), now()
            FROM groups g WHERE g."BlockedPatterns" = false;

            INSERT INTO safeguard_rules {{Columns}} VALUES
            (gen_random_uuid(), 'Secrets in what people send', 'Private keys, cloud and service tokens and passwords in messages, files and API requests: refused (the person is told what kind was found), never stored.',
             100, coalesce({{S("SecretScanning")}}, 'refuse') <> 'off', false, 'secrets', ARRAY['question','file','api'], '{}', '{}', '{}',
             CASE {{S("SecretScanning")}} WHEN 'mask' THEN 'mask' ELSE 'block' END, false, '{}', NULL, NULL, '{}', '{}', false, '{}', false, now(), now()),
            (gen_random_uuid(), 'Personal data to the model', 'E-mail addresses, phone and card numbers, IBANs, IP addresses and social security numbers reach the model masked; the chat keeps them as written.',
             110, coalesce({{S("RedactPii")}}, 'off') = 'mask', false, 'pii', ARRAY['question','api'], '{}', '{}', '{}',
             'mask', false, '{}', NULL, NULL, '{}', '{}', false, '{}', false, now(), now()),
            (gen_random_uuid(), 'Blocked words', 'Words, phrases and patterns a message may not contain.',
             120, coalesce(trim({{S("BlockedPatterns")}}), '') <> '', false, 'words', ARRAY['question','api'], '{}', '{}', '{}',
             'block', true, '{}', nullif(trim({{S("BlockedPatterns")}}), ''), NULL, '{}', '{}', false, '{}', false, now(), now()),
            (gen_random_uuid(), 'Harmful requests', 'The model reads each message first and refuses one asking for harm (learning, safety, news and fiction stay allowed).',
             130, coalesce({{S("Moderation")}}, 'off') = 'check', false, 'topic', ARRAY['question','api'], '{}', '{}', '{}',
             'block', true, '{}', NULL, NULL, {{Harms}}, '{}', false, '{}', false, now(), now()),
            (gen_random_uuid(), 'Jailbreaks', 'Messages that try to get round the assistant''s rules (ignore your instructions, reveal your system prompt, developer mode).',
             140, true, true, 'injection', ARRAY['question','api'], '{}', '{}', '{}',
             'block', false, '{}', NULL, NULL, '{}', '{}', false, '{}', false, now(), now()),
            (gen_random_uuid(), 'Prompt injection in what tools bring', 'Instructions hidden in web pages, documents and tool results, removed before the model reads them.',
             150, true, true, 'injection', ARRAY['tool_result'], '{}', '{}', '{}',
             'mask', false, '{}', NULL, NULL, '{}', '{}', false, '{}', false, now(), now());

            DELETE FROM settings WHERE key IN ('config:Safeguards:SecretScanning', 'config:Safeguards:RedactPii', 'config:Safeguards:Moderation',
                'config:Safeguards:ModerationCategories', 'config:Safeguards:BlockedPatterns');
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "safeguard_events");

            migrationBuilder.DropTable(
                name: "safeguard_rules");

            migrationBuilder.AddColumn<bool>(
                name: "BlockedPatterns",
                table: "groups",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Moderation",
                table: "groups",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedactPii",
                table: "groups",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretScanning",
                table: "groups",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);
        }
    }
}
