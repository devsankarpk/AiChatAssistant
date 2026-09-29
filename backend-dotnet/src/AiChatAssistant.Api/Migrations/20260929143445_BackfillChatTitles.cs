using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiChatAssistant.Api.Migrations
{
    /// <inheritdoc />
    public partial class BackfillChatTitles : Migration
    {
        /// <inheritdoc />
        // Data-only: chats created before auto-titling kept the default "New chat" forever. Title
        // them from their first user message, approximating ChatController.TitleFromMessage
        // (collapse whitespace, 60 chars max) - it truncates at a character, not a word.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE ChatSessions s
                JOIN (
                    SELECT m.SessionId, TRIM(REGEXP_REPLACE(m.Content, '[[:space:]]+', ' ')) AS Text
                    FROM Messages m
                    JOIN (
                        SELECT SessionId, MIN(Id) AS FirstId
                        FROM Messages
                        WHERE Role = 'user'
                        GROUP BY SessionId
                    ) f ON f.FirstId = m.Id
                ) fm ON fm.SessionId = s.Id
                SET s.Title = CASE
                    WHEN CHAR_LENGTH(fm.Text) <= 60 THEN fm.Text
                    ELSE CONCAT(TRIM(LEFT(fm.Text, 59)), '…')
                END
                WHERE s.Title = 'New chat' AND fm.Text <> '';
                """);
        }

        // Nothing to undo: the old titles were all the same placeholder.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
