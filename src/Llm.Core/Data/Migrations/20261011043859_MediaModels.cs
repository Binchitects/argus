using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Llm.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "media_models",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SpeechId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Steps = table.Column<int>(type: "integer", nullable: true),
                    CfgScale = table.Column<double>(type: "double precision", nullable: true),
                    Guidance = table.Column<double>(type: "double precision", nullable: true),
                    FlowShift = table.Column<double>(type: "double precision", nullable: true),
                    Sampler = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Scheduler = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    NegativePrompt = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    Fps = table.Column<int>(type: "integer", nullable: true),
                    MaxSeconds = table.Column<int>(type: "integer", nullable: true),
                    Voices = table.Column<List<string>>(type: "text[]", nullable: false),
                    DefaultVoice = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Speed = table.Column<double>(type: "double precision", nullable: true),
                    Language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Gpu = table.Column<int>(type: "integer", nullable: true),
                    MaxVramGb = table.Column<decimal>(type: "numeric", nullable: true),
                    Vae = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "gpu"),
                    VaeGpuGb = table.Column<decimal>(type: "numeric", nullable: true),
                    OffloadToCpu = table.Column<bool>(type: "boolean", nullable: false),
                    VaeTiling = table.Column<bool>(type: "boolean", nullable: false),
                    FlashAttention = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ExtraArgs = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Kept = table.Column<bool>(type: "boolean", nullable: false),
                    IdleMinutes = table.Column<int>(type: "integer", nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Files = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_models", x => x.Name);
                });

            migrationBuilder.CreateIndex(
                name: "IX_media_models_Mode",
                table: "media_models",
                column: "Mode");

            // The five models every stack had, as rows: those an admin turned off are left out (off is removed now), and
            // each keeps whether it was kept loaded (the picture and speech to text models were by default).
            migrationBuilder.Sql("""
                insert into media_models ("Name", "Mode", "Files", "SpeechId", "Steps", "CfgScale", "FlowShift", "Sampler", "NegativePrompt",
                    "Width", "Height", "Fps", "MaxSeconds", "Voices", "Language", "MaxVramGb", "Vae", "VaeGpuGb", "OffloadToCpu", "VaeTiling",
                    "FlashAttention", "Kept", "CreatedAt", "UpdatedAt")
                select d.name, d.mode, d.files::jsonb, d.speech, d.steps, d.cfg, d.shift, d.sampler, d.negative, d.width, d.height, d.fps, d.seconds,
                    '{}'::text[], d.language, d.vram, d.vae, d.vae_gb, false, d.tiling, d.fa,
                    coalesce((s.value::jsonb ->> 'Kept')::boolean, d.kept), now(), now()
                from (values
                    ('FLUX.2-klein-4B', 'image_generation',
                     '[{"Role":"diffusion","Path":"image/flux2-klein-4b/flux-2-klein-4b-Q4_0.gguf"},{"Role":"llm","Path":"image/flux2-klein-4b/Qwen3-4B-Q4_K_M.gguf"},{"Role":"vae","Path":"image/flux2-klein-4b/flux2-vae.safetensors"}]',
                     null, 4, 1.0, null::double precision, null, null, null::int, null::int, null::int, null::int, null, 2::numeric, 'gpu', null::numeric, false, true, true),
                    ('Wan2.2-TI2V-5B', 'video_generation',
                     '[{"Role":"diffusion","Path":"video/wan2.2-ti2v-5b/Wan2.2-TI2V-5B-Q4_K_M.gguf"},{"Role":"t5xxl","Path":"video/wan2.2-ti2v-5b/umt5-xxl-encoder-Q4_K_M.gguf"},{"Role":"vae","Path":"video/wan2.2-ti2v-5b/wan2.2_vae.safetensors"}]',
                     null, 20, 5.0, 5.0, 'euler', 'blurry, distorted, low quality, watermark, text', 640, 352, 16, 5, null, 3, 'auto', 12, true, true, false),
                    ('whisper-large-v3-turbo', 'audio_transcription', '[]', 'deepdml/faster-whisper-large-v3-turbo-ct2',
                     null, null, null, null, null, null, null, null, null, null, null, 'gpu', null, false, false, true),
                    ('kokoro', 'audio_speech', '[]', 'speaches-ai/Kokoro-82M-v1.0-ONNX',
                     null, null, null, null, null, null, null, null, null, null, null, 'gpu', null, false, false, false),
                    ('piper-fa', 'audio_speech', '[]', 'speaches-ai/piper-fa_IR-gyro-medium',
                     null, null, null, null, null, null, null, null, null, 'fa', null, 'gpu', null, false, false, false)
                ) as d(name, mode, files, speech, steps, cfg, shift, sampler, negative, width, height, fps, seconds, language, vram, vae, vae_gb, tiling, fa, kept)
                left join settings s on s.key = 'media.' || d.name
                where coalesce((s.value::jsonb ->> 'Enabled')::boolean, true);
                delete from settings where key like 'media.%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_models");
        }
    }
}
