using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class NeutralNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(""""
-- The code-intelligence service was named after a brand until 8.0: its chat tool id argus, its permissions argus.*
-- and settings.argus, its settings ArgusIndex:* and Argus:*. Stored under their neutral names from here on.
UPDATE conversations SET "Tools" = array_replace("Tools", 'argus', 'code-intel') WHERE 'argus' = ANY("Tools");
UPDATE projects SET "Tools" = array_replace("Tools", 'argus', 'code-intel') WHERE 'argus' = ANY("Tools");
UPDATE scheduled_tasks SET "Tools" = array_replace("Tools", 'argus', 'code-intel') WHERE 'argus' = ANY("Tools");
UPDATE task_templates SET "Tools" = array_replace("Tools", 'argus', 'code-intel') WHERE 'argus' = ANY("Tools");
UPDATE toolsets SET "Tools" = array_replace("Tools", 'argus', 'code-intel') WHERE 'argus' = ANY("Tools");
UPDATE tool_settings SET "ToolId" = 'code-intel' WHERE "ToolId" = 'argus';
UPDATE tool_group_settings SET "ToolId" = 'code-intel' WHERE "ToolId" = 'argus';
UPDATE person_credentials SET "ToolId" = 'code-intel' WHERE "ToolId" = 'argus';
UPDATE tool_calls SET "ToolId" = 'code-intel' WHERE "ToolId" = 'argus';
UPDATE access_rules SET "Resource" = 'code-intel' WHERE "ResourceKind" = 'tool' AND "Resource" = 'argus';
UPDATE access_requests SET "Resource" = 'code-intel' WHERE "ResourceKind" = 'tool' AND "Resource" = 'argus';
UPDATE storage_samples SET "Key" = 'code-intel' WHERE "Key" = 'argus';
UPDATE workflows SET "Flow" = replace("Flow", '"argus"', '"code-intel"') WHERE "Flow" LIKE '%"argus"%';
UPDATE workflow_runs SET "Flow" = replace("Flow", '"argus"', '"code-intel"') WHERE "Flow" LIKE '%"argus"%';
UPDATE access_roles SET "Permissions" = ARRAY(
    SELECT CASE WHEN p LIKE 'argus.%' THEN 'code-intel.' || substr(p, 7) WHEN p = 'settings.argus' THEN 'settings.code-intel' ELSE p END
    FROM unnest("Permissions") WITH ORDINALITY AS u(p, n) ORDER BY n)
  WHERE EXISTS (SELECT 1 FROM unnest("Permissions") p WHERE p LIKE 'argus.%' OR p = 'settings.argus');
UPDATE settings SET "key" = replace("key", 'config:ArgusIndex:', 'config:CodeIntelIndex:') WHERE "key" LIKE 'config:ArgusIndex:%';
UPDATE settings SET "key" = replace("key", 'config:Argus:', 'config:CodeIntel:') WHERE "key" LIKE 'config:Argus:%';
"""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(""""
-- The code-intelligence service was back under the names before 8.0: its chat tool id argus, its permissions argus.*
-- and settings.argus, its settings ArgusIndex:* and Argus:*. Stored under their neutral names from here on.
UPDATE conversations SET "Tools" = array_replace("Tools", 'code-intel', 'argus') WHERE 'code-intel' = ANY("Tools");
UPDATE projects SET "Tools" = array_replace("Tools", 'code-intel', 'argus') WHERE 'code-intel' = ANY("Tools");
UPDATE scheduled_tasks SET "Tools" = array_replace("Tools", 'code-intel', 'argus') WHERE 'code-intel' = ANY("Tools");
UPDATE task_templates SET "Tools" = array_replace("Tools", 'code-intel', 'argus') WHERE 'code-intel' = ANY("Tools");
UPDATE toolsets SET "Tools" = array_replace("Tools", 'code-intel', 'argus') WHERE 'code-intel' = ANY("Tools");
UPDATE tool_settings SET "ToolId" = 'argus' WHERE "ToolId" = 'code-intel';
UPDATE tool_group_settings SET "ToolId" = 'argus' WHERE "ToolId" = 'code-intel';
UPDATE person_credentials SET "ToolId" = 'argus' WHERE "ToolId" = 'code-intel';
UPDATE tool_calls SET "ToolId" = 'argus' WHERE "ToolId" = 'code-intel';
UPDATE access_rules SET "Resource" = 'argus' WHERE "ResourceKind" = 'tool' AND "Resource" = 'code-intel';
UPDATE access_requests SET "Resource" = 'argus' WHERE "ResourceKind" = 'tool' AND "Resource" = 'code-intel';
UPDATE storage_samples SET "Key" = 'argus' WHERE "Key" = 'code-intel';
UPDATE workflows SET "Flow" = replace("Flow", '"code-intel"', '"argus"') WHERE "Flow" LIKE '%"code-intel"%';
UPDATE workflow_runs SET "Flow" = replace("Flow", '"code-intel"', '"argus"') WHERE "Flow" LIKE '%"code-intel"%';
UPDATE access_roles SET "Permissions" = ARRAY(
    SELECT CASE WHEN p LIKE 'code-intel.%' THEN 'argus.' || substr(p, 12) WHEN p = 'settings.code-intel' THEN 'settings.argus' ELSE p END
    FROM unnest("Permissions") WITH ORDINALITY AS u(p, n) ORDER BY n)
  WHERE EXISTS (SELECT 1 FROM unnest("Permissions") p WHERE p LIKE 'code-intel.%' OR p = 'settings.code-intel');
UPDATE settings SET "key" = replace("key", 'config:CodeIntelIndex:', 'config:ArgusIndex:') WHERE "key" LIKE 'config:CodeIntelIndex:%';
UPDATE settings SET "key" = replace("key", 'config:CodeIntel:', 'config:Argus:') WHERE "key" LIKE 'config:CodeIntel:%';
"""");
        }
    }
}
