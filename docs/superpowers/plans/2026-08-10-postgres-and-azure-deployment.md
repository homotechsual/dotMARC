# PostgreSQL Migration & Azure Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace dotMARC's SQLite database with PostgreSQL (real EF Core Migrations, Testcontainers-backed tests), ship a `docker-compose.yml` so self-hosting stays a one-command affair, add CI/CD publishing to GHCR and Docker Hub, and ship a Bicep template that provisions everything needed to run dotMARC on Azure.

**Architecture:** Same application code, different database provider (Npgsql instead of SQLite) and a real migration history instead of `EnsureCreated()`. New: `docker-compose.yml` bundling the app with a `postgres:18` container for self-hosting; three GitHub Actions workflows mirroring `psatool-busybar-agent`'s existing CI/CD pattern; a Bicep template provisioning App Service, Postgres Flexible Server, and Key Vault for Azure deployment.

**Tech Stack additions:** Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, Testcontainers.PostgreSql 4.13.0, dotnet-ef 10.0.10 (local tool), PostgreSQL 18, Bicep.

## Global Constraints

- PostgreSQL fully replaces SQLite — no dual-provider support. Do not add a "which database" configuration switch.
- Real EF Core Migrations (`Database.Migrate()`) replace `Database.EnsureCreated()`. No data-preserving migration logic is needed for the `InitialCreate` migration — nothing has ever been deployed anywhere, so there's no existing data to carry forward.
- No VNet integration for Postgres — public access with an "Allow Azure services" firewall rule, per the design spec's explicit decision.
- Secrets (`Graph:ClientSecret`, `EntraId:ClientSecret`, the Postgres connection string) go into Key Vault, referenced from App Service settings — never as plaintext Application Settings in the Bicep template.
- Actually provisioning Azure resources is out of scope for every task in this plan — ship the template and its documentation; running it against a real subscription is the user's own action.
- The `InitialCreate` migration's exact content (Task 1) was generated and verified during planning — applied successfully against a real `postgres:18` container, confirmed to produce the correct 4 tables and all 4 indexes (including the two unique indexes and the composite `(DomainId, ReportingOrg, ReportId)` index from the project's earlier final-review fix wave). Use it verbatim; do not regenerate it from scratch unless the model has changed since this plan was written (it hasn't — no entity changes are part of this plan).

---

## Task 1: PostgreSQL provider swap and EF Core Migrations

**Files:**
- Modify: `src/DotMarc/DotMarc.csproj`
- Modify: `src/DotMarc/Program.cs`
- Modify: `src/DotMarc/appsettings.json`
- Modify: `src/DotMarc/Dockerfile`
- Create: `.config/dotnet-tools.json`
- Create: `src/DotMarc/Migrations/20260810175555_InitialCreate.cs`
- Create: `src/DotMarc/Migrations/20260810175555_InitialCreate.Designer.cs`
- Create: `src/DotMarc/Migrations/DotMarcDbContextModelSnapshot.cs`

**Interfaces:**
- Produces: `DotMarcDbContext` backed by PostgreSQL instead of SQLite, with a checked-in migration history. `Program.cs` calls `Database.MigrateAsync()` at startup instead of `Database.EnsureCreated()`.
- Consumed by: every later task in this plan, and by Task 2's test rewrite specifically (tests will call `Database.MigrateAsync()` against a Testcontainers-provisioned Postgres, not rely on `EnsureCreated()`'s schema-from-model behavior).

- [ ] **Step 1: Swap the EF Core provider in the project file**

`src/DotMarc/DotMarc.csproj` — replace the SQLite-related package references:

```xml
<PackageReference Include="MudBlazor" Version="9.8.0" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.10">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
</PackageReference>
<PackageReference Include="Microsoft.Identity.Client" Version="4.87.0" />
<PackageReference Include="Microsoft.Identity.Web" Version="4.14.2" />
<PackageReference Include="DmarcRua" Version="2.0.1" />
```

This removes `Microsoft.EntityFrameworkCore.Sqlite` and the `SQLitePCLRaw.bundle_e_sqlite3` version
override (added earlier specifically to patch a SQLite-only CVE — moot once SQLite is gone) and
adds `Npgsql.EntityFrameworkCore.PostgreSQL`.

- [ ] **Step 2: Add the dotnet-ef local tool**

```bash
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.10
```

This creates the manifest at the repo root by default — move it into `.config/` to match the
standard convention (some tooling and CI setups expect it there):

```bash
mkdir -p .config
mv dotnet-tools.json .config/dotnet-tools.json
```

Resulting `.config/dotnet-tools.json`:

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "dotnet-ef": {
      "version": "10.0.10",
      "commands": [
        "dotnet-ef"
      ],
      "rollForward": false
    }
  }
}
```

- [ ] **Step 3: Update Program.cs**

Change the default connection string and the provider call:

```csharp
var connectionString = builder.Configuration.GetConnectionString("DotMarc") ?? "Host=localhost;Database=dotmarc;Username=dotmarc;Password=dotmarc";
```

```csharp
builder.Services.AddDbContextFactory<DotMarcDbContext>(options => options.UseNpgsql(connectionString));
```

Replace `EnsureCreated()` with `MigrateAsync()` (note this requires `Program.cs`'s top-level
statements to already be in an `async` context via top-level `await` — confirm this compiles; if
the file isn't already using top-level `await` elsewhere, this `await` in the `using` block is
still valid since .NET 6+ implicitly wraps top-level statement files in an async `Main` when any
`await` is present):

```csharp
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DotMarcDbContext>().Database.MigrateAsync();
}
```

- [ ] **Step 4: Update the default connection string in appsettings.json**

`src/DotMarc/appsettings.json` — change the `ConnectionStrings:DotMarc` value from the SQLite file
path to a Postgres connection string matching the `docker-compose` service name that Task 3 will
add (so the default "just works" when running via compose):

```json
"ConnectionStrings": {
  "DotMarc": "Host=postgres;Database=dotmarc;Username=dotmarc;Password=dotmarc"
}
```

- [ ] **Step 5: Add the verified InitialCreate migration**

These three files were generated via `dotnet ef migrations add InitialCreate` against the current
model and verified during planning — applied successfully against a real `postgres:18` container,
producing exactly the 4 expected tables (`Domains`, `Reports`, `ReportRecords`, `ParseFailures`)
and all 4 expected indexes. Create them with this exact content (do not regenerate — the model
hasn't changed since this was verified):

`src/DotMarc/Migrations/20260810175555_InitialCreate.cs`:

```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Domains",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastReportReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Domains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParseFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GraphMessageId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParseFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Reports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DomainId = table.Column<int>(type: "integer", nullable: false),
                    ReportingOrg = table.Column<string>(type: "text", nullable: false),
                    ReportId = table.Column<string>(type: "text", nullable: false),
                    DateRangeBeginUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DateRangeEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RawXml = table.Column<string>(type: "text", nullable: false),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reports_Domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "Domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportId = table.Column<int>(type: "integer", nullable: false),
                    SourceIp = table.Column<string>(type: "text", nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    Disposition = table.Column<string>(type: "text", nullable: false),
                    SpfResult = table.Column<string>(type: "text", nullable: false),
                    DkimResult = table.Column<string>(type: "text", nullable: false),
                    HeaderFrom = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportRecords_Reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Domains_Name",
                table: "Domains",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParseFailures_GraphMessageId",
                table: "ParseFailures",
                column: "GraphMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReportRecords_ReportId",
                table: "ReportRecords",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_DomainId_ReportingOrg_ReportId",
                table: "Reports",
                columns: new[] { "DomainId", "ReportingOrg", "ReportId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ParseFailures");

            migrationBuilder.DropTable(
                name: "ReportRecords");

            migrationBuilder.DropTable(
                name: "Reports");

            migrationBuilder.DropTable(
                name: "Domains");
        }
    }
}
```

`src/DotMarc/Migrations/20260810175555_InitialCreate.Designer.cs`:

```csharp
// <auto-generated />
using System;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    [DbContext(typeof(DotMarcDbContext))]
    [Migration("20260810175555_InitialCreate")]
    partial class InitialCreate
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.10")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("DotMarc.Data.Domain", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("FirstSeenUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<bool>("IsPinned")
                        .HasColumnType("boolean");

                    b.Property<DateTimeOffset?>("LastReportReceivedUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("Name")
                        .IsUnique();

                    b.ToTable("Domains");
                });

            modelBuilder.Entity("DotMarc.Data.ParseFailure", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("AttemptCount")
                        .HasColumnType("integer");

                    b.Property<string>("GraphMessageId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("LastAttemptedUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Reason")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("GraphMessageId")
                        .IsUnique();

                    b.ToTable("ParseFailures");
                });

            modelBuilder.Entity("DotMarc.Data.Report", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("DateRangeBeginUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTimeOffset>("DateRangeEndUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int>("DomainId")
                        .HasColumnType("integer");

                    b.Property<string>("RawXml")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("ReceivedUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("ReportId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("ReportingOrg")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("DomainId", "ReportingOrg", "ReportId")
                        .IsUnique();

                    b.ToTable("Reports");
                });

            modelBuilder.Entity("DotMarc.Data.ReportRecord", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Disposition")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("DkimResult")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("HeaderFrom")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<int>("MessageCount")
                        .HasColumnType("integer");

                    b.Property<int>("ReportId")
                        .HasColumnType("integer");

                    b.Property<string>("SourceIp")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("SpfResult")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("ReportId");

                    b.ToTable("ReportRecords");
                });

            modelBuilder.Entity("DotMarc.Data.Report", b =>
                {
                    b.HasOne("DotMarc.Data.Domain", "Domain")
                        .WithMany("Reports")
                        .HasForeignKey("DomainId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Domain");
                });

            modelBuilder.Entity("DotMarc.Data.ReportRecord", b =>
                {
                    b.HasOne("DotMarc.Data.Report", "Report")
                        .WithMany("Records")
                        .HasForeignKey("ReportId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Report");
                });

            modelBuilder.Entity("DotMarc.Data.Domain", b =>
                {
                    b.Navigation("Reports");
                });

            modelBuilder.Entity("DotMarc.Data.Report", b =>
                {
                    b.Navigation("Records");
                });
#pragma warning restore 612, 618
        }
    }
}
```

`src/DotMarc/Migrations/DotMarcDbContextModelSnapshot.cs` — identical `BuildTargetModel` body to
the Designer file above, wrapped differently (this is the standing snapshot EF Core diffs future
migrations against, versus the Designer file which is this specific migration's own point-in-time
record — both are auto-generated and always kept in sync by the tooling):

```csharp
// <auto-generated />
using System;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    [DbContext(typeof(DotMarcDbContext))]
    partial class DotMarcDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "10.0.10")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("DotMarc.Data.Domain", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("FirstSeenUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<bool>("IsPinned")
                        .HasColumnType("boolean");

                    b.Property<DateTimeOffset?>("LastReportReceivedUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("Name")
                        .IsUnique();

                    b.ToTable("Domains");
                });

            modelBuilder.Entity("DotMarc.Data.ParseFailure", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<int>("AttemptCount")
                        .HasColumnType("integer");

                    b.Property<string>("GraphMessageId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("LastAttemptedUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Reason")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("GraphMessageId")
                        .IsUnique();

                    b.ToTable("ParseFailures");
                });

            modelBuilder.Entity("DotMarc.Data.Report", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<DateTimeOffset>("DateRangeBeginUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTimeOffset>("DateRangeEndUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<int>("DomainId")
                        .HasColumnType("integer");

                    b.Property<string>("RawXml")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<DateTimeOffset>("ReceivedUtc")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("ReportId")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("ReportingOrg")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("DomainId", "ReportingOrg", "ReportId")
                        .IsUnique();

                    b.ToTable("Reports");
                });

            modelBuilder.Entity("DotMarc.Data.ReportRecord", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("integer");

                    NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));

                    b.Property<string>("Disposition")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("DkimResult")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("HeaderFrom")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<int>("MessageCount")
                        .HasColumnType("integer");

                    b.Property<int>("ReportId")
                        .HasColumnType("integer");

                    b.Property<string>("SourceIp")
                        .IsRequired()
                        .HasColumnType("text");

                    b.Property<string>("SpfResult")
                        .IsRequired()
                        .HasColumnType("text");

                    b.HasKey("Id");

                    b.HasIndex("ReportId");

                    b.ToTable("ReportRecords");
                });

            modelBuilder.Entity("DotMarc.Data.Report", b =>
                {
                    b.HasOne("DotMarc.Data.Domain", "Domain")
                        .WithMany("Reports")
                        .HasForeignKey("DomainId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Domain");
                });

            modelBuilder.Entity("DotMarc.Data.ReportRecord", b =>
                {
                    b.HasOne("DotMarc.Data.Report", "Report")
                        .WithMany("Records")
                        .HasForeignKey("ReportId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Report");
                });

            modelBuilder.Entity("DotMarc.Data.Domain", b =>
                {
                    b.Navigation("Reports");
                });

            modelBuilder.Entity("DotMarc.Data.Report", b =>
                {
                    b.Navigation("Records");
                });
#pragma warning restore 612, 618
        }
    }
}
```

- [ ] **Step 6: Update the Dockerfile's connection string and drop the SQLite data volume**

`src/DotMarc/Dockerfile` currently declares a `VOLUME /app/data` and sets
`ConnectionStrings__DotMarc` to a SQLite file path under it — both existed only to persist the
SQLite database file across container restarts. With Postgres owning persistence instead (via its
own volume in Task 3's `docker-compose.yml`), remove the volume and change the default connection
string to match the Postgres service name `docker-compose.yml` will use:

Replace:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
VOLUME /app/data
ENV ConnectionStrings__DotMarc="Data Source=/app/data/dotmarc.db"
EXPOSE 8080
ENTRYPOINT ["dotnet", "DotMarc.dll"]
```

with:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ConnectionStrings__DotMarc="Host=postgres;Database=dotmarc;Username=dotmarc;Password=dotmarc"
EXPOSE 8080
ENTRYPOINT ["dotnet", "DotMarc.dll"]
```

The rest of the Dockerfile (the `build` stage) is unchanged.

- [ ] **Step 7: Verify the build succeeds**

```bash
dotnet build dotMARC.sln
```

Expected: builds with 0 errors. The existing test suite (33/33 as of the last final review) is
expected to now FAIL to build or run, since it still constructs `DotMarcDbContext` against
`UseSqlite` — this is expected and gets fixed in Task 2, not this task. Do not attempt to fix the
test project in this task.

- [ ] **Step 8: Manually verify the migration applies against a real Postgres**

```bash
docker run -d --name dotmarc-migration-verify -e POSTGRES_PASSWORD=test -p 55432:5432 postgres:18
```

Wait for readiness (`docker exec dotmarc-migration-verify pg_isready -U postgres`, retry until it
reports accepting connections), then:

```bash
dotnet tool restore
dotnet ef database update --project src/DotMarc/DotMarc.csproj --startup-project src/DotMarc/DotMarc.csproj --connection "Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=test"
```

Expected: "Applying migration '20260810175555_InitialCreate'." then "Done." Confirm the schema
with `docker exec dotmarc-migration-verify psql -U postgres -c "\dt"` (expect 5 tables: the 4
entity tables plus `__EFMigrationsHistory`) and `psql -U postgres -c "\di"` (expect the 4 named
indexes plus 4 primary key indexes plus the migrations-history primary key).

Then clean up:

```bash
docker stop dotmarc-migration-verify
docker rm dotmarc-migration-verify
```

- [ ] **Step 9: Commit**

```bash
git add src/DotMarc/DotMarc.csproj src/DotMarc/Program.cs src/DotMarc/appsettings.json src/DotMarc/Dockerfile .config/dotnet-tools.json src/DotMarc/Migrations/
git commit -m "Replace SQLite with PostgreSQL: Npgsql provider, real EF Core Migrations"
```

---

## Task 2: Rewrite tests against Testcontainers.PostgreSql

**Files:**
- Modify: `test/DotMarc.Tests/DotMarc.Tests.csproj`
- Create: `test/DotMarc.Tests/Internal/PostgresContainerFixture.cs`
- Modify: `test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`
- Modify: `test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`
- Modify: `test/DotMarc.Tests/ProgramDiValidationTests.cs`

**Interfaces:**
- Produces: `PostgresContainerFixture` (one real Postgres container, started once and shared across
  the whole test run via an xUnit collection fixture) and a `CreateDatabaseAsync()` method that
  creates a fresh, empty database on that shared container per test and returns a connection
  string plus an `IAsyncDisposable` that drops it afterward. This was verified during planning
  against a real Testcontainers.PostgreSql 4.13.0 + postgres:18 container: container start,
  connect, create a fresh database, connect to it, run a query, drop the database, and dispose the
  container all completed successfully.
- Consumes: the migration from Task 1 — each test's fresh database is brought to the current
  schema via `context.Database.MigrateAsync()`, not `EnsureCreated()`.

- [ ] **Step 1: Add the Testcontainers package**

`test/DotMarc.Tests/DotMarc.Tests.csproj` — add to the existing `<ItemGroup>` with the other
`PackageReference`s:

```xml
<PackageReference Include="Testcontainers.PostgreSql" Version="4.13.0" />
```

- [ ] **Step 2: Write the shared container fixture**

`test/DotMarc.Tests/Internal/PostgresContainerFixture.cs`:

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DotMarc.Tests.Internal;

/// <summary>One Postgres container shared across the whole test run (starting it is the expensive
/// part — several seconds), with each test getting its own freshly-created, freshly-migrated
/// database on that shared container (cheap — a CREATE DATABASE against an already-running server).
/// This matches the isolation the project's previous per-test temp-file SQLite database gave,
/// without paying container startup cost per test. Verified during planning: container start,
/// connect, create/connect-to/query a fresh database, drop it, and dispose the container all
/// complete successfully against a real postgres:18 image.</summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18").Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<(string ConnectionString, IAsyncDisposable Cleanup)> CreateDatabaseAsync()
    {
        var databaseName = $"test_{Guid.NewGuid():N}";
        var adminConnectionString = _container.GetConnectionString();

        await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
        {
            await adminConnection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", adminConnection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName };
        return (builder.ConnectionString, new DatabaseCleanup(adminConnectionString, databaseName));
    }

    private sealed class DatabaseCleanup(string adminConnectionString, string databaseName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();

            // Postgres refuses to drop a database with active connections — terminate any first.
            await using (var terminate = new NpgsqlCommand(
                $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{databaseName}' AND pid <> pg_backend_pid()", connection))
            {
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresContainerFixture>;
```

- [ ] **Step 3: Rewrite DotMarcDbContextTests against the fixture**

Replace the SQLite temp-file pattern (`_dbPath`, `CreateContext()` using `UseSqlite`,
`Database.EnsureCreated()`, file-based `Dispose()` with the `GC.Collect()`/`WaitForPendingFinalizers()`
workaround) with the shared fixture. All five `[Fact]` bodies are unchanged from the current file —
only setup/teardown changes, and `CreateContext()` no longer needs to call `EnsureCreated()` itself
since the fixture's `InitializeAsync()` migrates the database once per test:

`test/DotMarc.Tests/Data/DotMarcDbContextTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Data;

[Collection("Postgres")]
public sealed class DotMarcDbContextTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public DotMarcDbContextTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new DotMarcDbContext(options);
    }

    [Fact]
    public void CanInsertAndQuery_DomainWithReportAndRecords()
    {
        using (var context = CreateContext())
        {
            var domain = new Domain
            {
                Name = "contoso.io",
                IsPinned = true,
                FirstSeenUtc = DateTimeOffset.UtcNow
            };
            var report = new Report
            {
                Domain = domain,
                ReportingOrg = "google.com",
                ReportId = "123",
                DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
                DateRangeEndUtc = DateTimeOffset.UtcNow,
                RawXml = "<feedback/>",
                ReceivedUtc = DateTimeOffset.UtcNow
            };
            report.Records.Add(new ReportRecord
            {
                SourceIp = "198.51.100.7",
                MessageCount = 10,
                Disposition = DispositionResult.None,
                SpfResult = AuthResult.Pass,
                DkimResult = AuthResult.Pass,
                HeaderFrom = "contoso.io"
            });

            context.Domains.Add(domain);
            context.Reports.Add(report);
            context.SaveChanges();
        }

        using (var verifyContext = CreateContext())
        {
            var savedDomain = verifyContext.Domains
                .Include(d => d.Reports)
                .ThenInclude(r => r.Records)
                .Single();

            Assert.Equal("contoso.io", savedDomain.Name);
            Assert.True(savedDomain.IsPinned);
            Assert.Single(savedDomain.Reports);
            Assert.Single(savedDomain.Reports[0].Records);
            Assert.Equal(AuthResult.Pass, savedDomain.Reports[0].Records[0].SpfResult);
            Assert.Equal(DispositionResult.None, savedDomain.Reports[0].Records[0].Disposition);
        }
    }

    [Fact]
    public void DomainName_MustBeUnique()
    {
        using var context = CreateContext();
        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        context.Domains.Add(new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Report_DomainReportingOrgReportId_MustBeUnique()
    {
        // Backs PollingService's idempotency fix: without this constraint, reprocessing a message
        // whose report was already stored (e.g. because MarkAsReadAsync failed after the report
        // was saved) would silently double-count volume instead of being caught.
        using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        context.Reports.Add(new Report
        {
            Domain = domain,
            ReportingOrg = "google.com",
            ReportId = "dup-1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        });
        context.SaveChanges();

        context.Reports.Add(new Report
        {
            Domain = domain,
            ReportingOrg = "google.com",
            ReportId = "dup-1",
            DateRangeBeginUtc = DateTimeOffset.UtcNow.AddDays(-1),
            DateRangeEndUtc = DateTimeOffset.UtcNow,
            RawXml = "<feedback/>",
            ReceivedUtc = DateTimeOffset.UtcNow
        });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void ParseFailure_GraphMessageId_MustBeUnique()
    {
        using var context = CreateContext();
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "msg-1", Reason = "bad xml", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "msg-1", Reason = "bad xml again", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void ChangeTrackerClear_RemovesEntitiesLeftDanglingByAFailedSaveChanges()
    {
        // Regression coverage for the review finding: PollingService shares one DbContext across
        // a whole poll cycle. If a mid-cycle SaveChangesAsync throws (e.g. a constraint
        // violation), the half-built entities from that failed call stay tracked as Added unless
        // the tracker is explicitly cleared — otherwise the *next* SaveChanges call (recording a
        // ParseFailure) re-attempts them too and can throw again, uncaught. This confirms the
        // assumption PollingService's fix relies on: ChangeTracker.Clear() actually drops the
        // dangling entities, and a subsequent unrelated save then succeeds.
        using var context = CreateContext();
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "dangling", Reason = "first", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "dangling", Reason = "second", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });

        Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.NotEmpty(context.ChangeTracker.Entries());

        context.ChangeTracker.Clear();
        Assert.Empty(context.ChangeTracker.Entries());

        // A subsequent, unrelated save now succeeds instead of re-attempting the dangling inserts.
        context.ParseFailures.Add(new ParseFailure { GraphMessageId = "unrelated", Reason = "ok", AttemptCount = 1, LastAttemptedUtc = DateTimeOffset.UtcNow });
        context.SaveChanges();

        Assert.Equal(0, context.ParseFailures.Count(f => f.GraphMessageId == "dangling"));
        Assert.Equal(1, context.ParseFailures.Count(f => f.GraphMessageId == "unrelated"));
    }
}
```

- [ ] **Step 4: Rewrite PollingServiceTests against the fixture**

Replace the temp-file `_dbPath`/`CreateContext()`/`Dispose()` pattern with the shared
`PostgresContainerFixture`, migrating a fresh database in `InitializeAsync()`. All five test
bodies (`FakeGraphMailboxClient`, `ValidReportXml`, and the five `[Fact]`s covering the happy path,
parse failures, no-attachment skipping, the mark-as-read-failure idempotency regression, and the
repeated-failure `AttemptCount` growth) are unchanged from the current file — only setup/teardown
changes:

`test/DotMarc.Tests/Ingestion/PollingServiceTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Graph;
using DotMarc.Ingestion;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http;
using Xunit;

namespace DotMarc.Tests.Ingestion;

[Collection("Postgres")]
public class PollingServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public PollingServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DotMarcDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new DotMarcDbContext(options);
    }

    private static byte[] GzipOf(string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private const string ValidReportXml = """
        <?xml version="1.0" encoding="UTF-8" ?>
        <feedback>
          <report_metadata>
            <org_name>google.com</org_name>
            <email>noreply-dmarc-support@google.com</email>
            <report_id>1</report_id>
            <date_range><begin>1754438400</begin><end>1754524800</end></date_range>
          </report_metadata>
          <policy_published><domain>contoso.io</domain><adkim>r</adkim><aspf>r</aspf><p>quarantine</p><sp>quarantine</sp><pct>100</pct></policy_published>
          <record>
            <row><source_ip>198.51.100.7</source_ip><count>10</count><policy_evaluated><disposition>none</disposition><dkim>pass</dkim><spf>pass</spf></policy_evaluated></row>
            <identifiers><header_from>contoso.io</header_from></identifiers>
            <auth_results><spf><domain>contoso.io</domain><result>pass</result></spf></auth_results>
          </record>
        </feedback>
        """;

    [Fact]
    public async Task PollOnceAsync_ParsesAndStoresAValidReport_ThenMarksMessageRead()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            var domain = verify.Domains.Include(d => d.Reports).ThenInclude(r => r.Records).Single();
            Assert.Equal("contoso.io", domain.Name);
            Assert.Single(domain.Reports);
            Assert.Single(domain.Reports[0].Records);
        }

        Assert.Contains("msg-1", graphClient.MarkedAsRead);
        Assert.Empty(await CreateContext().ParseFailures.ToListAsync());
    }

    [Fact]
    public async Task PollOnceAsync_RecordsParseFailure_AndLeavesMessageUnread_ForUnparseableAttachment()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-2", "Not a report", true));
        graphClient.Attachments["msg-2"] = [new MailboxAttachment("garbage.xml", "text/xml", "not xml"u8.ToArray())];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            Assert.Empty(verify.Reports);
            var failure = verify.ParseFailures.Single();
            Assert.Equal("msg-2", failure.GraphMessageId);
        }

        Assert.DoesNotContain("msg-2", graphClient.MarkedAsRead);
    }

    [Fact]
    public async Task PollOnceAsync_SkipsMessagesWithNoAttachments()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-3", "Unrelated", false));

        using var context = CreateContext();
        var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(context.Reports);
        Assert.Empty(context.ParseFailures);
        Assert.DoesNotContain("msg-3", graphClient.MarkedAsRead);
    }

    [Fact]
    public async Task PollOnceAsync_DoesNotDuplicateReport_WhenMarkAsReadFailsAfterStoreSucceeds_AndMessageIsReprocessed()
    {
        // Regression coverage for the review finding: the report is stored, but MarkAsReadAsync
        // (a separate, transient Graph call) fails, so the message stays unread and gets
        // reprocessed on the next poll. That second attempt must not create a second Report row
        // for the same (domain, reporting org, report id).
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-1", "Report domain: contoso.io", true));
        graphClient.Attachments["msg-1"] = [new MailboxAttachment("report.xml.gz", "application/gzip", GzipOf(ValidReportXml))];
        graphClient.FailMarkAsReadFor.Add("msg-1");

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        // First attempt: report stored, but marking read failed — so it's NOT a ParseFailure, and
        // the message is still considered unread for the next poll.
        using (var verify = CreateContext())
        {
            Assert.Single(verify.Reports);
            Assert.Empty(verify.ParseFailures);
        }
        Assert.DoesNotContain("msg-1", graphClient.MarkedAsRead);

        // Second poll: Graph now succeeds at marking read. The same message (still unread, still
        // carrying the same report) is reprocessed.
        graphClient.FailMarkAsReadFor.Clear();
        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using (var verify = CreateContext())
        {
            Assert.Single(verify.Reports); // still exactly one — no duplicate.
            Assert.Empty(verify.ParseFailures);
        }
        Assert.Contains("msg-1", graphClient.MarkedAsRead);
    }

    [Fact]
    public async Task PollOnceAsync_UpdatesExistingParseFailureRow_InsteadOfGrowingUnboundedly_OnRepeatedFailure()
    {
        var graphClient = new FakeGraphMailboxClient();
        graphClient.UnreadMessages.Add(new MailboxMessage("msg-2", "Not a report", true));
        graphClient.Attachments["msg-2"] = [new MailboxAttachment("garbage.xml", "text/xml", "not xml"u8.ToArray())];

        using (var context = CreateContext())
        {
            var service = new PollingService(graphClient, context, NullLogger<PollingService>.Instance);
            await service.PollOnceAsync(CancellationToken.None);
            await service.PollOnceAsync(CancellationToken.None);
            await service.PollOnceAsync(CancellationToken.None);
        }

        using var verify = CreateContext();
        var failure = verify.ParseFailures.Single();
        Assert.Equal("msg-2", failure.GraphMessageId);
        Assert.Equal(3, failure.AttemptCount);
    }

    private sealed class FakeGraphMailboxClient : IGraphMailboxClient
    {
        public List<MailboxMessage> UnreadMessages { get; } = [];
        public Dictionary<string, List<MailboxAttachment>> Attachments { get; } = [];
        public List<string> MarkedAsRead { get; } = [];
        public HashSet<string> FailMarkAsReadFor { get; } = [];

        public Task<IReadOnlyList<MailboxMessage>> GetUnreadMessagesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailboxMessage>>(UnreadMessages);

        public Task<IReadOnlyList<MailboxAttachment>> GetAttachmentsAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailboxAttachment>>(Attachments.GetValueOrDefault(messageId, []));

        public Task MarkAsReadAsync(string messageId, CancellationToken cancellationToken)
        {
            if (FailMarkAsReadFor.Contains(messageId))
            {
                throw new HttpRequestException("Simulated transient Graph failure marking message read.");
            }

            MarkedAsRead.Add(messageId);
            return Task.CompletedTask;
        }
    }
}
```

- [ ] **Step 5: Rewrite ProgramDiValidationTests against the fixture**

Same pattern: registrations switch from `UseSqlite(...)` against a temp file to
`UseNpgsql(connectionString)` against a database from the shared fixture. The test's own logic (a
`ServiceProvider` built with `ValidateScopes`/`ValidateOnBuild` enabled, to catch the DI-ambiguity
class of bug from the project's earlier final review) is unchanged:

`test/DotMarc.Tests/ProgramDiValidationTests.cs`:

```csharp
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests;

/// <summary>Regression test for a bug the re-review of this fix wave's DbContextFactory change
/// caught: registering BOTH AddDbContext&lt;DotMarcDbContext&gt; and
/// AddDbContextFactory&lt;DotMarcDbContext&gt; together (the original shape of that fix) creates a
/// scoped/singleton DbContextOptions&lt;DotMarcDbContext&gt; conflict that only surfaces when the
/// container validates scopes. WebApplication.CreateBuilder enables ValidateScopes/ValidateOnBuild
/// by default in the Development environment, but not in Production — so a plain `dotnet build`
/// and even a Docker smoke test (Production by default, since the Dockerfile sets no explicit
/// ASPNETCORE_ENVIRONMENT) both missed it; only actually starting the host with
/// ASPNETCORE_ENVIRONMENT=Development throws.
///
/// This test builds a ServiceProvider with ValidateScopes/ValidateOnBuild explicitly enabled
/// (mirroring what CreateBuilder does in Development) using the exact registration shape
/// Program.cs uses today — AddDbContextFactory only, no separate AddDbContext call — confirming:
/// 1. BuildServiceProvider itself doesn't throw (this is where the bug, if reintroduced, throws:
///    "Cannot consume scoped service 'DbContextOptions&lt;DotMarcDbContext&gt;' from singleton
///    'IDbContextFactory&lt;DotMarcDbContext&gt;'").
/// 2. IDbContextFactory&lt;DotMarcDbContext&gt; resolves (used by Dashboard.razor/DomainDetail.razor).
/// 3. DotMarcDbContext also resolves from a scope (used by PollingService's existing
///    IServiceScopeFactory-based resolution) — this is exactly what the fix must not break, since
///    AddDbContextFactory registers DotMarcDbContext itself as scoped too, without needing a
///    separate AddDbContext call.</summary>
[Collection("Postgres")]
public sealed class ProgramDiValidationTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ProgramDiValidationTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options;
        await using var context = new DotMarcDbContext(options);
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    [Fact]
    public void ServiceProvider_BuildsCleanly_WithScopeValidationEnabled_UsingOnlyAddDbContextFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Exactly Program.cs's registration shape: AddDbContextFactory only, no AddDbContext
        // registered alongside it.
        services.AddDbContextFactory<DotMarcDbContext>(options => options.UseNpgsql(_connectionString));

        // ValidateScopes + ValidateOnBuild is what WebApplication.CreateBuilder turns on in the
        // Development environment. This call is the actual assertion: before the fix (AddDbContext
        // + AddDbContextFactory registered together), this line itself throws
        // InvalidOperationException.
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        // IDbContextFactory resolves directly (singleton) — used by the Blazor Server pages.
        var factory = provider.GetRequiredService<IDbContextFactory<DotMarcDbContext>>();
        Assert.NotNull(factory);

        // DotMarcDbContext also resolves from a scope — used by PollingService's existing
        // IServiceScopeFactory-based resolution, which must keep working unchanged.
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DotMarcDbContext>();
        Assert.NotNull(context);
    }
}
```

- [ ] **Step 6: Run the full test suite**

```bash
dotnet test dotMARC.sln
```

Expected: all tests pass. This will take noticeably longer than the previous SQLite-based run
(container startup is a one-time cost per test run, typically 10-20 seconds, not per test) — that's
expected, not a regression to chase down.

- [ ] **Step 7: Commit**

```bash
git add test/DotMarc.Tests/DotMarc.Tests.csproj test/DotMarc.Tests/Internal/PostgresContainerFixture.cs test/DotMarc.Tests/Data/DotMarcDbContextTests.cs test/DotMarc.Tests/Ingestion/PollingServiceTests.cs test/DotMarc.Tests/ProgramDiValidationTests.cs
git commit -m "Rewrite tests against Testcontainers.PostgreSql instead of temp-file SQLite"
```

---

## Task 3: docker-compose for self-hosted deployment

**Files:**
- Create: `docker-compose.yml`
- Modify: `README.md`

**Interfaces:**
- Produces: `docker compose up` as the self-hosted "one command to run it" path, replacing the
  current single `docker run` instruction.

- [ ] **Step 1: Write docker-compose.yml**

`docker-compose.yml` (repo root):

```yaml
services:
  app:
    build:
      context: .
      dockerfile: src/DotMarc/Dockerfile
    ports:
      - "8080:8080"
    environment:
      ConnectionStrings__DotMarc: "Host=postgres;Database=dotmarc;Username=dotmarc;Password=${POSTGRES_PASSWORD:-dotmarc}"
      Graph__ClientId: ${GRAPH_CLIENT_ID:?Set GRAPH_CLIENT_ID}
      Graph__TenantId: ${GRAPH_TENANT_ID:?Set GRAPH_TENANT_ID}
      Graph__ClientSecret: ${GRAPH_CLIENT_SECRET:?Set GRAPH_CLIENT_SECRET}
      Graph__MailboxAddress: ${GRAPH_MAILBOX_ADDRESS:?Set GRAPH_MAILBOX_ADDRESS}
      EntraId__TenantId: ${ENTRAID_TENANT_ID:?Set ENTRAID_TENANT_ID}
      EntraId__ClientId: ${ENTRAID_CLIENT_ID:?Set ENTRAID_CLIENT_ID}
      EntraId__ClientSecret: ${ENTRAID_CLIENT_SECRET:?Set ENTRAID_CLIENT_SECRET}
    depends_on:
      postgres:
        condition: service_healthy

  postgres:
    image: postgres:18
    environment:
      POSTGRES_DB: dotmarc
      POSTGRES_USER: dotmarc
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-dotmarc}
    volumes:
      - dotmarc-postgres-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U dotmarc -d dotmarc"]
      interval: 5s
      timeout: 5s
      retries: 10

volumes:
  dotmarc-postgres-data:
```

The `:?Set ...` syntax makes `docker compose up` fail fast with a clear message if a required
secret env var isn't set, rather than starting the app with empty/invalid credentials.
`POSTGRES_PASSWORD` has a default (`dotmarc`) since it's not a real external secret — it's only
ever used for the container-to-container connection on the compose network — but can still be
overridden.

- [ ] **Step 2: Update the README's self-hosted run instructions**

Replace the existing `## Run` section's `docker build`/`docker run` example with:

```markdown
## Run

```bash
GRAPH_CLIENT_ID=... GRAPH_TENANT_ID=... GRAPH_CLIENT_SECRET=... GRAPH_MAILBOX_ADDRESS=... \
ENTRAID_TENANT_ID=... ENTRAID_CLIENT_ID=... ENTRAID_CLIENT_SECRET=... \
docker compose up
```

This runs dotMARC and a PostgreSQL 18 database together, with Postgres data persisted in a named
Docker volume (`dotmarc-postgres-data`). Set the six required environment variables from the setup
steps above (or put them in a `.env` file next to `docker-compose.yml` — compose reads that
automatically).
```

Update any other README text that still references the old SQLite connection string format or the
single-container `docker run` command from before this change.

- [ ] **Step 3: Manually verify**

```bash
GRAPH_CLIENT_ID=placeholder GRAPH_TENANT_ID=placeholder GRAPH_CLIENT_SECRET=placeholder GRAPH_MAILBOX_ADDRESS=placeholder@example.com \
ENTRAID_TENANT_ID=placeholder ENTRAID_CLIENT_ID=placeholder ENTRAID_CLIENT_SECRET=placeholder \
docker compose up --build -d
```

Expected: both containers start, `postgres` reports healthy, `app` logs show a successful migration
run (`Applying migration '20260810175555_InitialCreate'.`) followed by normal startup — the same
graceful placeholder-credential failure mode already established in every prior Docker smoke test
in this project (Graph poll fails on the placeholder tenant, sign-in 500s on `/`) is expected and
fine here too. Then:

```bash
docker compose down -v
```

to clean up (the `-v` removes the named volume too, since this was just a smoke test).

- [ ] **Step 4: Commit**

```bash
git add docker-compose.yml README.md
git commit -m "Add docker-compose for self-hosted deployment with PostgreSQL"
```

---

## Task 4: CI/CD workflows

**Files:**
- Create: `.github/workflows/ci.yml`
- Create: `.github/workflows/publish.yml`
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Produces: automated build/test on every push and PR; automated multi-arch image publishing to
  GHCR (always) and Docker Hub (if credentials are configured) on push to `main`; automated
  versioned releases on `v*.*.*` tags. Mirrors `psatool-busybar-agent`'s existing workflows
  file-for-file, adapted only for dotMARC's solution name, Dockerfile path, and image name.

- [ ] **Step 1: Write the CI workflow**

`.github/workflows/ci.yml`:

```yaml
name: CI

on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:

jobs:
  test:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v7

      - name: Set up .NET
        uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

      - name: Restore dependencies
        run: dotnet restore dotMARC.sln

      - name: Build solution
        run: dotnet build dotMARC.sln -c Release --no-restore

      - name: Run tests
        run: dotnet test dotMARC.sln -c Release --no-build
```

Note: the test suite now requires Docker (Testcontainers.PostgreSql, per Task 2) — `ubuntu-latest`
GitHub-hosted runners have Docker available by default, so no extra setup step is needed here.

- [ ] **Step 2: Write the publish workflow**

`.github/workflows/publish.yml`:

```yaml
name: Publish

on:
  push:
    branches: [main]
  workflow_dispatch:

env:
  REGISTRY: ghcr.io
  DOCKERHUB_IMAGE: homotechsual/dotmarc

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write

    concurrency:
      group: ${{ github.workflow }}-${{ github.ref }}
      cancel-in-progress: true

    steps:
      - uses: actions/checkout@v7

      - name: Compute image name
        id: image
        run: echo "name=${REGISTRY}/$(echo '${{ github.repository }}' | tr '[:upper:]' '[:lower:]')" >> "$GITHUB_OUTPUT"

      - name: Compute Docker Hub image name
        id: dockerhub
        env:
          DOCKERHUB_USERNAME: ${{ secrets.DOCKERHUB_USERNAME }}
          DOCKERHUB_TOKEN: ${{ secrets.DOCKERHUB_TOKEN }}
        run: |
          if [ -n "$DOCKERHUB_USERNAME" ] && [ -n "$DOCKERHUB_TOKEN" ]; then
            echo "enabled=true" >> "$GITHUB_OUTPUT"
            echo "name=${DOCKERHUB_IMAGE}" >> "$GITHUB_OUTPUT"
          else
            echo "enabled=false" >> "$GITHUB_OUTPUT"
            echo "name=" >> "$GITHUB_OUTPUT"
          fi

      - name: Set up QEMU
        uses: docker/setup-qemu-action@v4

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v4

      - name: Log in to GHCR
        uses: docker/login-action@v4
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push image to GHCR
        uses: docker/build-push-action@v7
        with:
          context: .
          file: src/DotMarc/Dockerfile
          target: final
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ steps.image.outputs.name }}:edge
            ${{ steps.image.outputs.name }}:${{ github.sha }}
          cache-from: type=gha
          cache-to: type=gha,mode=max

      - name: Log in to Docker Hub
        if: ${{ steps.dockerhub.outputs.enabled == 'true' }}
        uses: docker/login-action@v4
        with:
          username: ${{ secrets.DOCKERHUB_USERNAME }}
          password: ${{ secrets.DOCKERHUB_TOKEN }}

      - name: Build and push image to Docker Hub
        if: ${{ steps.dockerhub.outputs.enabled == 'true' }}
        uses: docker/build-push-action@v7
        with:
          context: .
          file: src/DotMarc/Dockerfile
          target: final
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ steps.dockerhub.outputs.name }}:edge
            ${{ steps.dockerhub.outputs.name }}:${{ github.sha }}
          cache-from: type=gha
          cache-to: type=gha,mode=max
```

`src/DotMarc/Dockerfile`'s final stage is explicitly named `final` (`FROM
mcr.microsoft.com/dotnet/aspnet:10.0 AS final`) — `target: final` above pins the build to that
stage rather than relying on it being the last one declared.

- [ ] **Step 3: Write the release workflow**

`.github/workflows/release.yml`:

```yaml
name: Release

on:
  push:
    tags:
      - "v*.*.*"
  workflow_dispatch:
    inputs:
      version:
        description: "Release version (for example 1.0.0 or v1.0.0)"
        required: true
        type: string

env:
  REGISTRY: ghcr.io
  DOCKERHUB_IMAGE: homotechsual/dotmarc

jobs:
  test:
    runs-on: ubuntu-latest
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v7

      - name: Set up .NET
        uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

      - name: Restore dependencies
        run: dotnet restore dotMARC.sln

      - name: Build solution
        run: dotnet build dotMARC.sln -c Release --no-restore

      - name: Run tests
        run: dotnet test dotMARC.sln -c Release --no-build

  publish-images:
    needs: test
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    outputs:
      version: ${{ steps.version.outputs.version }}
      ghcr_image: ${{ steps.images.outputs.ghcr }}
      dockerhub_image: ${{ steps.images.outputs.dockerhub }}
      dockerhub_enabled: ${{ steps.images.outputs.dockerhub_enabled }}
    steps:
      - uses: actions/checkout@v7

      - name: Resolve release version
        id: version
        env:
          INPUT_VERSION: ${{ github.event.inputs.version }}
        run: |
          if [ -n "$INPUT_VERSION" ]; then
            VERSION="$INPUT_VERSION"
          else
            VERSION="${GITHUB_REF_NAME}"
          fi

          VERSION="${VERSION#v}"

          if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
            echo "Invalid version: $VERSION"
            exit 1
          fi

          MAJOR="${VERSION%%.*}"
          REST="${VERSION#*.}"
          MINOR="${REST%%.*}"

          echo "version=$VERSION" >> "$GITHUB_OUTPUT"
          echo "major=$MAJOR" >> "$GITHUB_OUTPUT"
          echo "minor=$MINOR" >> "$GITHUB_OUTPUT"

      - name: Resolve image names
        id: images
        env:
          DOCKERHUB_USERNAME: ${{ secrets.DOCKERHUB_USERNAME }}
          DOCKERHUB_TOKEN: ${{ secrets.DOCKERHUB_TOKEN }}
        run: |
          GHCR_IMAGE="${REGISTRY}/$(echo '${{ github.repository }}' | tr '[:upper:]' '[:lower:]')"
          echo "ghcr=$GHCR_IMAGE" >> "$GITHUB_OUTPUT"

          if [ -n "$DOCKERHUB_USERNAME" ] && [ -n "$DOCKERHUB_TOKEN" ]; then
            echo "dockerhub_enabled=true" >> "$GITHUB_OUTPUT"
            echo "dockerhub=${DOCKERHUB_IMAGE}" >> "$GITHUB_OUTPUT"
          else
            echo "dockerhub_enabled=false" >> "$GITHUB_OUTPUT"
            echo "dockerhub=" >> "$GITHUB_OUTPUT"
          fi

      - name: Require Docker Hub credentials for tagged releases
        if: ${{ github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v') && steps.images.outputs.dockerhub_enabled != 'true' }}
        run: |
          echo "DOCKERHUB_USERNAME and DOCKERHUB_TOKEN must be set for tagged releases."
          exit 1

      - name: Set up QEMU
        uses: docker/setup-qemu-action@v4

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v4

      - name: Log in to GHCR
        uses: docker/login-action@v4
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push release image to GHCR
        uses: docker/build-push-action@v7
        with:
          context: .
          file: src/DotMarc/Dockerfile
          target: final
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ steps.images.outputs.ghcr }}:${{ steps.version.outputs.version }}
            ${{ steps.images.outputs.ghcr }}:${{ steps.version.outputs.major }}.${{ steps.version.outputs.minor }}
            ${{ steps.images.outputs.ghcr }}:${{ steps.version.outputs.major }}
            ${{ steps.images.outputs.ghcr }}:latest
            ${{ steps.images.outputs.ghcr }}:${{ github.sha }}
          cache-from: type=gha
          cache-to: type=gha,mode=max

      - name: Log in to Docker Hub
        if: ${{ steps.images.outputs.dockerhub_enabled == 'true' }}
        uses: docker/login-action@v4
        with:
          username: ${{ secrets.DOCKERHUB_USERNAME }}
          password: ${{ secrets.DOCKERHUB_TOKEN }}

      - name: Build and push release image to Docker Hub
        if: ${{ steps.images.outputs.dockerhub_enabled == 'true' }}
        uses: docker/build-push-action@v7
        with:
          context: .
          file: src/DotMarc/Dockerfile
          target: final
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ steps.images.outputs.dockerhub }}:${{ steps.version.outputs.version }}
          cache-from: type=gha
          cache-to: type=gha,mode=max

      - name: Create Docker Hub release alias tags
        if: ${{ steps.images.outputs.dockerhub_enabled == 'true' }}
        env:
          REPO: ${{ steps.images.outputs.dockerhub }}
          VERSION: ${{ steps.version.outputs.version }}
          MAJOR: ${{ steps.version.outputs.major }}
          MINOR: ${{ steps.version.outputs.minor }}
        run: |
          set -euo pipefail

          SOURCE="${REPO}:${VERSION}"
          docker buildx imagetools create -t "${REPO}:${MAJOR}.${MINOR}" "$SOURCE"
          docker buildx imagetools create -t "${REPO}:${MAJOR}" "$SOURCE"
          docker buildx imagetools create -t "${REPO}:latest" "$SOURCE"

      - name: Verify Docker Hub semver tags
        if: ${{ steps.images.outputs.dockerhub_enabled == 'true' }}
        env:
          REPO: ${{ steps.images.outputs.dockerhub }}
          VERSION: ${{ steps.version.outputs.version }}
          MAJOR: ${{ steps.version.outputs.major }}
          MINOR: ${{ steps.version.outputs.minor }}
        run: |
          set -euo pipefail

          REPO_PATH="${REPO%/*}/${REPO#*/}"
          REQUIRED_TAGS=("$VERSION" "$MAJOR.$MINOR" "$MAJOR" "latest")

          for attempt in 1 2 3 4 5 6; do
            TAGS_JSON="$(curl -fsSL "https://hub.docker.com/v2/repositories/${REPO_PATH}/tags?page_size=100")"
            missing=0

            for tag in "${REQUIRED_TAGS[@]}"; do
              if ! echo "$TAGS_JSON" | grep -q "\"name\":\"${tag}\""; then
                echo "Attempt ${attempt}: missing Docker Hub tag: ${tag}"
                missing=1
              fi
            done

            if [ "$missing" -eq 0 ]; then
              echo "Verified Docker Hub tags: $VERSION, $MAJOR.$MINOR, $MAJOR, latest"
              exit 0
            fi

            sleep 10
          done

          echo "Docker Hub semver tag verification failed after retries."
          exit 1

  release:
    needs: publish-images
    if: ${{ github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v') }}
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      - name: Create GitHub release
        uses: softprops/action-gh-release@v3
        with:
          tag_name: ${{ github.ref_name }}
          generate_release_notes: true
          body: |
            Published images:

            - GHCR: `${{ needs.publish-images.outputs.ghcr_image }}:${{ needs.publish-images.outputs.version }}`
            - Docker Hub: `${{ needs.publish-images.outputs.dockerhub_image }}:${{ needs.publish-images.outputs.version }}`
```

- [ ] **Step 4: Verify locally what can be verified**

GitHub Actions workflows can't be fully executed locally. Verify what's checkable without a real
push: YAML is well-formed (`docker compose config` won't validate workflow YAML, but a basic parse
check via any YAML linter available, or careful visual review against the file-for-file
`psatool-busybar-agent` comparison, is the available substitute here), and that the referenced
Dockerfile path (`src/DotMarc/Dockerfile`) and solution name (`dotMARC.sln`) are correct for this
repo. Full verification happens on the first real push to `main` once this is merged — note in
your report that workflow-level verification is necessarily incomplete until then, consistent with
how this project has handled other things that can't be verified without a live external system
(the Entra sign-in flow, for example).

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/ci.yml .github/workflows/publish.yml .github/workflows/release.yml
git commit -m "Add CI/CD: build+test, GHCR/Docker Hub publish, tagged releases"
```

---

## Task 5: Bicep template for Azure deployment

**Files:**
- Create: `infra/main.bicep`
- Create: `infra/main.parameters.json`
- Modify: `README.md`

**Interfaces:**
- Produces: a deployable Bicep template provisioning an App Service Plan, a Linux Web App for
  Containers, an Azure Database for PostgreSQL Flexible Server, and a Key Vault with the Web App's
  managed identity granted access — everything needed to run dotMARC on Azure. Actually running
  `az deployment group create` against a real subscription is the user's own action.

- [ ] **Step 1: Write the Bicep template**

`infra/main.bicep`:

```bicep
@description('Base name used to derive resource names (e.g. "dotmarc").')
param baseName string = 'dotmarc'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Container image to deploy, e.g. ghcr.io/homotechsual/dotmarc:latest')
param containerImage string = 'ghcr.io/homotechsual/dotmarc:latest'

@description('PostgreSQL administrator username.')
param postgresAdminUsername string = 'dotmarc'

@secure()
@description('PostgreSQL administrator password.')
param postgresAdminPassword string

@description('Non-secret Graph app-only config.')
param graphClientId string
param graphTenantId string
param graphMailboxAddress string

@description('Non-secret dashboard sign-in config.')
param entraIdTenantId string
param entraIdClientId string

var postgresServerName = '${baseName}-pg-${uniqueString(resourceGroup().id)}'
var keyVaultName = '${baseName}-kv-${uniqueString(resourceGroup().id)}'
var appServicePlanName = '${baseName}-plan'
var webAppName = '${baseName}-${uniqueString(resourceGroup().id)}'
var postgresDatabaseName = 'dotmarc'

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: postgresServerName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '18'
    administratorLogin: postgresAdminUsername
    administratorLoginPassword: postgresAdminPassword
    storage: {
      storageSizeGB: 32
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource postgresDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgresServer
  name: postgresDatabaseName
}

resource postgresFirewallAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgresServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  kind: 'app,linux,container'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      linuxFxVersion: 'DOCKER|${containerImage}'
      alwaysOn: true
      webSocketsEnabled: true
      appSettings: [
        { name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE', value: 'false' }
        { name: 'Graph__ClientId', value: graphClientId }
        { name: 'Graph__TenantId', value: graphTenantId }
        { name: 'Graph__MailboxAddress', value: graphMailboxAddress }
        { name: 'EntraId__TenantId', value: entraIdTenantId }
        { name: 'EntraId__ClientId', value: entraIdClientId }
        { name: 'Graph__ClientSecret', value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=Graph-ClientSecret)' }
        { name: 'EntraId__ClientSecret', value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=EntraId-ClientSecret)' }
        { name: 'ConnectionStrings__DotMarc', value: '@Microsoft.KeyVault(VaultName=${keyVaultName};SecretName=ConnectionStrings-DotMarc)' }
      ]
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
  }
}

resource keyVaultSecretsUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webApp.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// These three secrets are provisioned empty. The app cannot function until they're set — see the
// README's "Deploy to Azure" section for the az keyvault secret set commands run after deployment.
// Web App settings above reference them by name (not by version), so setting a new value takes
// effect without redeploying the template.
resource graphClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'Graph-ClientSecret'
  properties: {
    value: ''
  }
}

resource entraIdClientSecretRef 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'EntraId-ClientSecret'
  properties: {
    value: ''
  }
}

resource connectionStringSecret 'Microsoft.KeyVault/vaults/secrets@2024-04-01-preview' = {
  parent: keyVault
  name: 'ConnectionStrings-DotMarc'
  properties: {
    value: ''
  }
}

output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppName string = webApp.name
output postgresServerFqdn string = postgresServer.properties.fullyQualifiedDomainName
output keyVaultName string = keyVault.name
```

The three Key Vault secrets are provisioned empty deliberately — per the design spec, secret
material never passes through the deployment command line or a parameters file. Web App settings
reference them by name via `@Microsoft.KeyVault(VaultName=...;SecretName=...)` (not by version), so
populating them after deployment with `az keyvault secret set` (Step 3 below) takes effect without
needing to redeploy the Bicep template. `postgresAdminPassword` remains a deployment parameter
because it's needed to provision the PostgreSQL server resource itself, not because it's meant to
flow into a Key Vault secret automatically — the `ConnectionStrings-DotMarc` secret's value is
assembled and set manually in Step 3, using the `postgresServerFqdn` output.

- [ ] **Step 2: Write the parameters file**

`infra/main.parameters.json` — a template for the user to fill in (not committed with real values):

```json
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "baseName": { "value": "dotmarc" },
    "containerImage": { "value": "ghcr.io/homotechsual/dotmarc:latest" },
    "postgresAdminUsername": { "value": "dotmarc" },
    "postgresAdminPassword": { "value": "REPLACE_ME" },
    "graphClientId": { "value": "REPLACE_ME" },
    "graphTenantId": { "value": "REPLACE_ME" },
    "graphMailboxAddress": { "value": "REPLACE_ME" },
    "entraIdTenantId": { "value": "REPLACE_ME" },
    "entraIdClientId": { "value": "REPLACE_ME" }
  }
}
```

Note this file holds only non-secret configuration plus the Postgres admin password (required to
provision the database server resource itself). `Graph:ClientSecret`, `EntraId:ClientSecret`, and
the Postgres connection string are never passed as deployment parameters — they're set directly
into Key Vault after deployment, per Step 3 below.

- [ ] **Step 3: Add the README Azure deployment section**

Add a new `## Deploy to Azure` section documenting: what the template provisions (App Service,
Postgres Flexible Server, Key Vault), the prerequisite steps (both Entra app registrations, same as
the existing Docker setup instructions — link back to that section rather than duplicating it), and
the deployment command:

```bash
az group create --name dotmarc-rg --location uksouth
az deployment group create \
  --resource-group dotmarc-rg \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json
```

Note clearly that `main.parameters.json` as checked in has placeholder values and must be filled in
(or the equivalent `--parameters key=value` inline overrides used) before running this — it is not
meant to be deployed as-is. Cross-reference the CI/CD-published GHCR image
(`ghcr.io/homotechsual/dotmarc:latest`, or a specific version tag from a release) as the
recommended `containerImage` value.

After the deployment above succeeds, the app won't yet be able to sign in or reach Postgres — the
template deliberately leaves three Key Vault secrets empty (see Task 5 Step 1) rather than accept
secret material as deployment parameters. Populate them directly:

```bash
RG=dotmarc-rg
KV=$(az deployment group show --resource-group $RG --name main --query properties.outputs.keyVaultName.value -o tsv)
PG_FQDN=$(az deployment group show --resource-group $RG --name main --query properties.outputs.postgresServerFqdn.value -o tsv)

az keyvault secret set --vault-name $KV --name Graph-ClientSecret --value "<graph app client secret>"
az keyvault secret set --vault-name $KV --name EntraId-ClientSecret --value "<entra id app client secret>"
az keyvault secret set --vault-name $KV --name ConnectionStrings-DotMarc \
  --value "Host=$PG_FQDN;Database=dotmarc;Username=<postgresAdminUsername>;Password=<postgresAdminPassword>;Ssl Mode=Require"

az webapp restart --resource-group $RG --name $(az deployment group show --resource-group $RG --name main --query properties.outputs.webAppName.value -o tsv)
```

Substitute the two Entra app registration secrets (created following the same manual steps already
documented for the Docker setup), and the `postgresAdminUsername`/`postgresAdminPassword` values
used in the deployment parameters. The `az webapp restart` forces the Web App to re-fetch the Key
Vault references immediately rather than waiting for their normal refresh cycle.

- [ ] **Step 4: Verify what can be verified**

A full deployment can't be verified without a real Azure subscription (out of scope for this
task). What can be checked: the Bicep file is syntactically valid —

```bash
az bicep build --file infra/main.bicep
```

if the Azure CLI is available in your environment; if not, note in your report that Bicep syntax
validation wasn't possible and rely on careful manual review against the Bicep resource schemas
referenced (`Microsoft.Web/sites@2024-04-01`, `Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01`,
`Microsoft.KeyVault/vaults@2024-04-01-preview`) instead.

- [ ] **Step 5: Commit**

```bash
git add infra/main.bicep infra/main.parameters.json README.md
git commit -m "Add Bicep template for Azure deployment: App Service, Postgres, Key Vault"
```
