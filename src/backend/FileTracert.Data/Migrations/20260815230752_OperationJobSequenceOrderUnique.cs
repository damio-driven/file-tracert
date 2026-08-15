using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class OperationJobSequenceOrderUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationJobs_SequenceOrder",
                table: "OperationJobs");

            // An existing database can already hold duplicate positions — that is exactly the
            // defect this index closes (C26), and CREATE UNIQUE INDEX would simply fail on them.
            // Renumber every job 1..N by its current position, ties broken by Id: relative FIFO
            // order is preserved, uniqueness is guaranteed, and the walk that reads these numbers
            // (the ledger feasibility) keeps the same verdicts. The temp table is what keeps the
            // rank stable — a correlated subquery over the table being updated would read rows it
            // had already rewritten.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE _seq_renumber AS
                    SELECT Id, ROW_NUMBER() OVER (ORDER BY SequenceOrder, Id) AS NewOrder
                    FROM OperationJobs;
                """);
            migrationBuilder.Sql("""
                UPDATE OperationJobs
                   SET SequenceOrder = (SELECT NewOrder FROM _seq_renumber
                                        WHERE _seq_renumber.Id = OperationJobs.Id);
                """);
            migrationBuilder.Sql("DROP TABLE _seq_renumber;");

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_SequenceOrder",
                table: "OperationJobs",
                column: "SequenceOrder",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationJobs_SequenceOrder",
                table: "OperationJobs");

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_SequenceOrder",
                table: "OperationJobs",
                column: "SequenceOrder");
        }
    }
}
