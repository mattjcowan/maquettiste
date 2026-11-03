using System.Globalization;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine;

/// <summary>What <c>migration ledger</c> and <c>get_ledger_ddl</c> answer (next-phase-1-design.md section 3.3).</summary>
/// <param name="ContractVersion">The ledger contract version the DDL creates (<see cref="LedgerContract.Version"/>).</param>
/// <param name="Database">The name of the PostgreSQL database the DDL is for.</param>
/// <param name="Dialect">Its dialect's JSON name (<c>postgresql</c>).</param>
/// <param name="Schema">The ledger schema (<c>migrations.ledgerSchema</c>), as the setting holds it; the DDL quotes it when it is a
/// reserved word.</param>
/// <param name="Ddl">The ledger DDL: idempotent, without transaction control, times or ids, ending with a line feed.</param>
/// <param name="Grant">The read grant for the role asked for (usage on the schema, <c>SELECT</c> on the three tables), or
/// <see langword="null"/> when none was asked; written as <c>null</c> in JSON either way.</param>
public sealed record LedgerDdlAnswer(int ContractVersion, string Database, string Dialect, string Schema, string Ddl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Grant);

/// <summary>The migration ledger's DDL for the CLI and the tools (next-phase-1-design.md section 3.3, task A21).</summary>
public sealed partial class ModelStore
{
    /// <summary>
    /// The ledger DDL of one PostgreSQL database: the named one (name or id), else the only PostgreSQL database of the model. The DDL
    /// fills the project's <c>migrations.ledgerSchema</c>; the engine prints it and the read grant, and never reads a ledger.
    /// </summary>
    /// <param name="database">The database, by id or name; <see langword="null"/> or empty for the only PostgreSQL database.</param>
    /// <param name="grantRead">A role to print the read grant for; <see langword="null"/> for none.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The contract version, the database, its dialect, the schema, the DDL and the grant.</returns>
    /// <exception cref="ArgumentException">Naming the cause: the model has no PostgreSQL database, the named one is missing or not
    /// PostgreSQL, several PostgreSQL databases exist and none is named, several databases carry the name, or the role is
    /// empty.</exception>
    public async Task<LedgerDdlAnswer> GetLedgerDdlAsync(string? database, string? grantRead, CancellationToken ct)
    {
        if (grantRead is { Length: 0 })
            throw new ArgumentException("grantRead names no role; leave it out for no grant.", nameof(grantRead));
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var chosen = LedgerDatabase(snapshot, database);
        var schema = snapshot.Settings.Migrations.LedgerSchema;
        return new LedgerDdlAnswer(LedgerContract.Version, chosen.Name, DialectInfo.Name(chosen.Dialect), schema, LedgerContract.Ddl(chosen.Dialect, schema),
            grantRead is null ? null : LedgerContract.ReadGrant(chosen.Dialect, schema, grantRead));
    }

    /// <summary>The database whose ledger DDL is asked for.</summary>
    /// <exception cref="ArgumentException">None fits; the message names why.</exception>
    private static Database LedgerDatabase(ModelSnapshot snapshot, string? database)
    {
        var version = LedgerContract.Version.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(database))
        {
            var named = ChosenDatabases(snapshot, database); // by id, else by name; an unknown one throws
            if (named.Count > 1)
                throw new ArgumentException($"Several databases are named '{database}'; name one by its id.", nameof(database));
            var only = named[0];
            return LedgerContract.Supports(only.Dialect)
                ? only
                : throw new ArgumentException(
                    $"The database '{only.Name}' is {DialectInfo.Name(only.Dialect)}; the migration ledger exists on PostgreSQL only (contract version {version}).",
                    nameof(database));
        }

        var postgres = ChosenDatabases(snapshot, null).Where(d => LedgerContract.Supports(d.Dialect)).ToList();
        return postgres.Count switch
        {
            0 => throw new ArgumentException(
                $"The model has no PostgreSQL database; the migration ledger exists on PostgreSQL only (contract version {version}).", nameof(database)),
            1 => postgres[0],
            _ => throw new ArgumentException(
                $"The model has {postgres.Count.ToString(CultureInfo.InvariantCulture)} PostgreSQL databases ({string.Join(", ", postgres.Select(d => d.Name))}); name one.",
                nameof(database)),
        };
    }
}
