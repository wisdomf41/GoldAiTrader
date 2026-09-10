using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoldAiTrader.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialOperationalSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExecutionJournal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCorrelationId = table.Column<string>(type: "text", nullable: false),
                    CanonicalSymbol = table.Column<string>(type: "text", nullable: false),
                    AccountIdentifier = table.Column<string>(type: "text", nullable: false),
                    StrategyVersion = table.Column<string>(type: "text", nullable: false),
                    Direction = table.Column<string>(type: "text", nullable: false),
                    RequestedVolume = table.Column<decimal>(type: "numeric", nullable: false),
                    RequestedEntry = table.Column<decimal>(type: "numeric", nullable: false),
                    RequestedStopLoss = table.Column<decimal>(type: "numeric", nullable: false),
                    RequestedTakeProfit = table.Column<decimal>(type: "numeric", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    BrokerOrderId = table.Column<string>(type: "text", nullable: true),
                    BrokerPositionId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmissionAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionJournal", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Features",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SchemaVersion = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Features", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    FeatureSchemaVersion = table.Column<string>(type: "text", nullable: false),
                    TrainedThrough = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DatasetVersion = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OperationalSafetyStates",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "text", nullable: false),
                    EmergencyShutdown = table.Column<bool>(type: "boolean", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalSafetyStates", x => x.Scope);
                });

            migrationBuilder.CreateTable(
                name: "Predictions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelVersion = table.Column<string>(type: "text", nullable: false),
                    Probability = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Predictions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResearchRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfigurationJson = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RiskStates",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "text", nullable: false),
                    ConsecutiveLosses = table.Column<int>(type: "integer", nullable: false),
                    SuspendedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskStates", x => x.Scope);
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    Direction = table.Column<string>(type: "text", nullable: false),
                    EntryTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExitTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EntryPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    ExitPrice = table.Column<decimal>(type: "numeric", nullable: true),
                    StopLoss = table.Column<decimal>(type: "numeric", nullable: false),
                    TakeProfit = table.Column<decimal>(type: "numeric", nullable: false),
                    Volume = table.Column<decimal>(type: "numeric", nullable: false),
                    ProfitLoss = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJournal_BrokerPositionId",
                table: "ExecutionJournal",
                column: "BrokerPositionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJournal_ClientCorrelationId",
                table: "ExecutionJournal",
                column: "ClientCorrelationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionJournal_SignalId",
                table: "ExecutionJournal",
                column: "SignalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_Version",
                table: "Models",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trades_SignalId",
                table: "Trades",
                column: "SignalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Decisions");

            migrationBuilder.DropTable(
                name: "ExecutionJournal");

            migrationBuilder.DropTable(
                name: "Features");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "OperationalSafetyStates");

            migrationBuilder.DropTable(
                name: "Predictions");

            migrationBuilder.DropTable(
                name: "ResearchRuns");

            migrationBuilder.DropTable(
                name: "RiskStates");

            migrationBuilder.DropTable(
                name: "Trades");
        }
    }
}
