using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gom.Web.Migrations
{
    /// <inheritdoc />
    public partial class SearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");

            // unaccent() cannot be indexed (it is only stable: its dictionary is looked up by name each time), so this one names
            // its dictionary with the schema the extension is in, is declared immutable, and has a SQL-standard body, which is bound
            // when it is made. A search that finds little then uses the index instead of reading the whole conversation.
            migrationBuilder.Sql("""
                DO $$
                DECLARE s text;
                BEGIN
                  SELECT n.nspname INTO STRICT s FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'unaccent';
                  EXECUTE format('CREATE FUNCTION gom_unaccent(text) RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT RETURN %I.unaccent(%L::regdictionary, $1)', s, s || '.unaccent');
                END $$;
                """);
            // Building it reads every message: about 30 s for 10 million on a laptop, past the 30 s a command may take by default. A
            // database that large needs "Command Timeout=600" added to the migration bundle's connection string for this one run.
            migrationBuilder.Sql("CREATE INDEX ix_messages_body_trgm ON messages USING gin (gom_unaccent(body) gin_trgm_ops);");
            // The planner knows what the index holds only after the table is next analysed; until then it guessed that a search
            // matches nearly 1% of a conversation and read the conversation newest first instead (10 s on 10 million, 2026-11-28).
            // ANALYZE reads a sample, so it is quick at any size.
            migrationBuilder.Sql("ANALYZE messages;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX ix_messages_body_trgm;");
            migrationBuilder.Sql("DROP FUNCTION gom_unaccent(text);");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:unaccent", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:unaccent", ",,");
        }
    }
}
