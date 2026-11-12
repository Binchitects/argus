using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <summary>8.0: the model gateway is the app's own, so a risk's asset "The gateway (LiteLLM)" is "The model gateway (in the app)".</summary>
    public partial class RenameGatewayAsset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE risks SET "Assets" = array_replace("Assets", 'The gateway (LiteLLM)', 'The model gateway (in the app)')""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE risks SET "Assets" = array_replace("Assets", 'The model gateway (in the app)', 'The gateway (LiteLLM)')""");
        }
    }
}
