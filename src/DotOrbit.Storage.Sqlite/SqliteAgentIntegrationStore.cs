using System.Globalization;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class SqliteAgentIntegrationStore(
    WorkspaceTransactionCoordinator transactions) : IAgentIntegrationStore
{
    internal const string SchemaSixteen = """
        CREATE TABLE agent_client_authorizations (
            id TEXT NOT NULL PRIMARY KEY,
            credential_verifier TEXT NOT NULL,
            os_account TEXT NOT NULL,
            client_name TEXT NOT NULL,
            client_version TEXT NOT NULL,
            scopes INTEGER NOT NULL CHECK(scopes BETWEEN 1 AND 3),
            authorized_at TEXT NOT NULL,
            UNIQUE(credential_verifier, os_account)
        );
        CREATE TABLE pending_agent_proposal (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            receipt TEXT NOT NULL UNIQUE,
            idempotency_key TEXT NOT NULL,
            authorization_id TEXT NOT NULL,
            client_identity TEXT NOT NULL,
            client_name TEXT NOT NULL,
            received_at TEXT NOT NULL,
            payload_digest TEXT NOT NULL,
            strict_utf8 BLOB NOT NULL CHECK(length(strict_utf8) BETWEEN 1 AND 4194304),
            UNIQUE(client_identity, idempotency_key)
        );
        CREATE TABLE agent_proposal_receipts (
            client_identity TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            payload_digest TEXT NOT NULL,
            receipt TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            PRIMARY KEY(client_identity, idempotency_key)
        );
        """;

    internal const string PendingProposalSchemaSeventeen = """
        CREATE TABLE pending_agent_proposal (
            queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
            receipt TEXT NOT NULL UNIQUE,
            idempotency_key TEXT NOT NULL,
            authorization_id TEXT NOT NULL,
            client_identity TEXT NOT NULL,
            client_name TEXT NOT NULL,
            received_at TEXT NOT NULL,
            payload_digest TEXT NOT NULL,
            proposal_kind INTEGER CHECK(proposal_kind BETWEEN 0 AND 1),
            operation_count INTEGER NOT NULL CHECK(operation_count BETWEEN 0 AND 512),
            effect_count INTEGER NOT NULL CHECK(effect_count BETWEEN 0 AND 2000),
            strict_utf8 BLOB NOT NULL CHECK(length(strict_utf8) BETWEEN 1 AND 4194304),
            UNIQUE(client_identity, idempotency_key)
        );
        """;

    internal const string PendingProposalSchema = """
        CREATE TABLE pending_agent_proposal (
            queue_id INTEGER PRIMARY KEY AUTOINCREMENT,
            receipt TEXT NOT NULL UNIQUE,
            idempotency_key TEXT NOT NULL,
            authorization_id TEXT NOT NULL,
            client_identity TEXT NOT NULL,
            client_name TEXT NOT NULL,
            received_at TEXT NOT NULL,
            payload_digest TEXT NOT NULL,
            proposal_kind INTEGER CHECK(proposal_kind BETWEEN 0 AND 1),
            operation_count INTEGER NOT NULL CHECK(operation_count BETWEEN 0 AND 512),
            effect_count INTEGER NOT NULL CHECK(effect_count BETWEEN 0 AND 2000),
            review_basis BLOB CHECK(review_basis IS NULL OR length(review_basis) BETWEEN 1 AND 4194304),
            strict_utf8 BLOB NOT NULL CHECK(length(strict_utf8) BETWEEN 1 AND 4194304),
            UNIQUE(client_identity, idempotency_key)
        );
        """;

    internal const string Schema = """
        CREATE TABLE agent_client_authorizations (
            id TEXT NOT NULL PRIMARY KEY,
            credential_verifier TEXT NOT NULL,
            os_account TEXT NOT NULL,
            client_name TEXT NOT NULL,
            client_version TEXT NOT NULL,
            scopes INTEGER NOT NULL CHECK(scopes BETWEEN 1 AND 3),
            authorized_at TEXT NOT NULL,
            UNIQUE(credential_verifier, os_account)
        );
        """ + PendingProposalSchema + """
        CREATE TABLE agent_proposal_receipts (
            client_identity TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            payload_digest TEXT NOT NULL,
            receipt TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            PRIMARY KEY(client_identity, idempotency_key)
        );
        """;

    internal const string SchemaSeventeen = """
        CREATE TABLE agent_client_authorizations (
            id TEXT NOT NULL PRIMARY KEY,
            credential_verifier TEXT NOT NULL,
            os_account TEXT NOT NULL,
            client_name TEXT NOT NULL,
            client_version TEXT NOT NULL,
            scopes INTEGER NOT NULL CHECK(scopes BETWEEN 1 AND 3),
            authorized_at TEXT NOT NULL,
            UNIQUE(credential_verifier, os_account)
        );
        """ + PendingProposalSchemaSeventeen + """
        CREATE TABLE agent_proposal_receipts (
            client_identity TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            payload_digest TEXT NOT NULL,
            receipt TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            PRIMARY KEY(client_identity, idempotency_key)
        );
        """;

    public IReadOnlyList<AgentClientAuthorization> ReadAuthorizations() =>
        Guard(() => transactions.Read((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id,credential_verifier,os_account,client_name,client_version,scopes,authorized_at
                FROM agent_client_authorizations
                ORDER BY authorized_at,id;
                """;
            using var reader = command.ExecuteReader();
            var authorizations = new List<AgentClientAuthorization>();
            while (reader.Read())
            {
                authorizations.Add(ReadAuthorization(reader));
            }

            return authorizations.AsReadOnly();
        }));

    public AgentClientAuthorization? FindAuthorization(
        string credentialVerifier,
        string operatingSystemAccount,
        AgentClientScopes exactScopes)
    {
        ValidateCredentialVerifier(credentialVerifier);
        ValidateMetadata(operatingSystemAccount, 256, nameof(operatingSystemAccount));
        ValidateScopes(exactScopes);
        return Guard(() => transactions.Read((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id,credential_verifier,os_account,client_name,client_version,scopes,authorized_at
                FROM agent_client_authorizations
                WHERE credential_verifier=$verifier AND os_account=$account AND scopes=$scopes;
                """;
            command.Parameters.AddWithValue("$verifier", credentialVerifier);
            command.Parameters.AddWithValue("$account", operatingSystemAccount);
            command.Parameters.AddWithValue("$scopes", (int)exactScopes);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAuthorization(reader) : null;
        }));
    }

    public void RememberAuthorization(AgentClientAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ValidateMetadata(authorization.Id, 128, nameof(authorization));
        ValidateCredentialVerifier(authorization.CredentialVerifier);
        ValidateMetadata(authorization.OperatingSystemAccount, 256, nameof(authorization));
        ValidateMetadata(authorization.ClientName, 80, nameof(authorization));
        ValidateMetadata(authorization.ClientVersion, 80, nameof(authorization));
        ValidateScopes(authorization.Scopes);
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO agent_client_authorizations
                    (id,credential_verifier,os_account,client_name,client_version,scopes,authorized_at)
                VALUES ($id,$verifier,$account,$name,$version,$scopes,$authorized)
                ON CONFLICT(credential_verifier,os_account) DO UPDATE SET
                    id=excluded.id,
                    client_name=excluded.client_name,
                    client_version=excluded.client_version,
                    scopes=excluded.scopes,
                    authorized_at=excluded.authorized_at;
                DELETE FROM agent_client_authorizations
                WHERE id NOT IN (
                    SELECT id FROM agent_client_authorizations
                    ORDER BY authorized_at DESC,id DESC
                    LIMIT 64
                );
                """;
            command.Parameters.AddWithValue("$id", authorization.Id);
            command.Parameters.AddWithValue("$verifier", authorization.CredentialVerifier);
            command.Parameters.AddWithValue("$account", authorization.OperatingSystemAccount);
            command.Parameters.AddWithValue("$name", authorization.ClientName);
            command.Parameters.AddWithValue("$version", authorization.ClientVersion);
            command.Parameters.AddWithValue("$scopes", (int)authorization.Scopes);
            command.Parameters.AddWithValue("$authorized", authorization.AuthorizedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            return ArchiveSearchIndexEffects.None;
        }));
    }

    public bool RevokeAuthorization(string authorizationId)
    {
        ValidateMetadata(authorizationId, 128, nameof(authorizationId));
        var removed = false;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM agent_client_authorizations WHERE id=$id;";
            command.Parameters.AddWithValue("$id", authorizationId);
            removed = command.ExecuteNonQuery() == 1;
            return ArchiveSearchIndexEffects.None;
        }));
        return removed;
    }

    public IReadOnlyList<PendingAgentProposal> ReadPendingProposals() =>
        Guard(() => transactions.Read((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT receipt,idempotency_key,authorization_id,client_identity,client_name,received_at,
                       proposal_kind,operation_count,effect_count,review_basis,strict_utf8
                FROM pending_agent_proposal
                ORDER BY queue_id;
                """;
            using var reader = command.ExecuteReader();
            var proposals = new List<PendingAgentProposal>();
            while (reader.Read())
            {
                proposals.Add(ReadPending(reader));
            }

            return proposals.AsReadOnly();
        }));

    public PendingAgentProposal? FindPendingProposal(string receipt)
    {
        ValidateMetadata(receipt, 128, nameof(receipt));
        return Guard(() => transactions.Read((connection, transaction) =>
            ReadPending(connection, transaction, receipt)));
    }

    public AgentProposalReceipt? FindProposalReceipt(string clientIdentity, string idempotencyKey)
    {
        ValidateCredentialVerifier(clientIdentity);
        ValidateMetadata(idempotencyKey, 128, nameof(idempotencyKey));
        return Guard(() => transactions.Read<AgentProposalReceipt?>((connection, transaction) =>
        {
            var receipt = ReadReceipt(connection, transaction, clientIdentity, idempotencyKey);
            return receipt is null ? null : new(receipt.PayloadDigest, receipt.Receipt);
        }));
    }

    public PendingAgentProposalStoreResult TryStorePendingProposal(PendingAgentProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ValidateMetadata(proposal.Receipt, 128, nameof(proposal));
        ValidateMetadata(proposal.IdempotencyKey, 128, nameof(proposal));
        ValidateMetadata(proposal.AuthorizationId, 128, nameof(proposal));
        ValidateCredentialVerifier(proposal.ClientIdentity);
        ValidateMetadata(proposal.ClientName, 80, nameof(proposal));
        if (proposal.ProposalKind is not { } proposalKind)
        {
            throw new ArgumentException("New pending proposals require a known proposal kind.", nameof(proposal));
        }
        PendingAgentProposalStoreResult? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            var priorReceipt = ReadReceipt(
                connection,
                transaction,
                proposal.ClientIdentity,
                proposal.IdempotencyKey);
            if (priorReceipt is not null)
            {
                result = priorReceipt.PayloadDigest == proposal.PayloadDigest
                    ? new(PendingAgentProposalStoreStatus.Duplicate, priorReceipt.Receipt)
                    : new(PendingAgentProposalStoreStatus.IdempotencyConflict, null);
                return ArchiveSearchIndexEffects.None;
            }

            if (ReadPendingCount(connection, transaction) >= AgentInterchangeV1.MaximumPendingProposals)
            {
                result = new(PendingAgentProposalStoreStatus.Occupied, null);
                return ArchiveSearchIndexEffects.None;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO pending_agent_proposal
                    (receipt,idempotency_key,authorization_id,client_identity,client_name,
                     received_at,payload_digest,proposal_kind,operation_count,effect_count,review_basis,strict_utf8)
                VALUES ($receipt,$key,$authorization,$identity,$client,$received,$digest,$kind,$operations,$effects,$basis,$payload);
                """;
            command.Parameters.AddWithValue("$receipt", proposal.Receipt);
            command.Parameters.AddWithValue("$key", proposal.IdempotencyKey);
            command.Parameters.AddWithValue("$authorization", proposal.AuthorizationId);
            command.Parameters.AddWithValue("$identity", proposal.ClientIdentity);
            command.Parameters.AddWithValue("$client", proposal.ClientName);
            command.Parameters.AddWithValue("$received", proposal.ReceivedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$digest", proposal.PayloadDigest);
            command.Parameters.AddWithValue("$kind", (int)proposalKind);
            command.Parameters.AddWithValue("$operations", proposal.OperationCount);
            command.Parameters.AddWithValue("$effects", proposal.EffectCount);
            command.Parameters.Add("$basis", SqliteType.Blob).Value = proposal.ReviewBasis.IsEmpty
                ? DBNull.Value
                : proposal.ReviewBasis.ToArray();
            command.Parameters.Add("$payload", SqliteType.Blob).Value = proposal.StrictUtf8.ToArray();
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO agent_proposal_receipts
                    (client_identity,idempotency_key,payload_digest,receipt,recorded_at)
                VALUES ($identity,$key,$digest,$receipt,$received);
                DELETE FROM agent_proposal_receipts AS candidate
                WHERE NOT EXISTS (
                    SELECT 1 FROM pending_agent_proposal AS pending
                    WHERE pending.receipt=candidate.receipt
                ) AND candidate.rowid NOT IN (
                    SELECT retained.rowid FROM agent_proposal_receipts AS retained
                    WHERE NOT EXISTS (
                        SELECT 1 FROM pending_agent_proposal AS pending
                        WHERE pending.receipt=retained.receipt
                    )
                    ORDER BY retained.rowid DESC
                    LIMIT max(0, 128 - (SELECT count(*) FROM pending_agent_proposal))
                );
                """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$identity", proposal.ClientIdentity);
            command.Parameters.AddWithValue("$key", proposal.IdempotencyKey);
            command.Parameters.AddWithValue("$digest", proposal.PayloadDigest);
            command.Parameters.AddWithValue("$receipt", proposal.Receipt);
            command.Parameters.AddWithValue("$received", proposal.ReceivedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            result = new(PendingAgentProposalStoreStatus.Stored, proposal.Receipt);
            return ArchiveSearchIndexEffects.None;
        }));
        return result ?? throw new InvalidOperationException();
    }

    public bool RemovePendingProposal(string receipt)
    {
        ValidateMetadata(receipt, 128, nameof(receipt));
        var removed = false;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM pending_agent_proposal WHERE receipt=$receipt;";
            command.Parameters.AddWithValue("$receipt", receipt);
            removed = command.ExecuteNonQuery() == 1;
            if (removed)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    DELETE FROM agent_proposal_receipts
                    WHERE rowid NOT IN (
                        SELECT rowid FROM agent_proposal_receipts
                        ORDER BY rowid DESC
                        LIMIT 128
                    );
                    """;
                command.ExecuteNonQuery();
            }
            return ArchiveSearchIndexEffects.None;
        }));
        return removed;
    }

    internal static void ValidateShape(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var definitions = Schema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        SqliteWorkspaceWork.ValidateTableDefinition(command, "agent_client_authorizations", definitions[0]);
        SqliteWorkspaceWork.ValidateTableDefinition(command, "pending_agent_proposal", definitions[1]);
        SqliteWorkspaceWork.ValidateTableDefinition(command, "agent_proposal_receipts", definitions[2]);
    }

    internal static void ValidateShapeSixteen(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var definitions = SchemaSixteen.Split(
            ';',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        SqliteWorkspaceWork.ValidateTableDefinition(command, "agent_client_authorizations", definitions[0]);
        SqliteWorkspaceWork.ValidateTableDefinition(command, "pending_agent_proposal", definitions[1]);
        SqliteWorkspaceWork.ValidateTableDefinition(command, "agent_proposal_receipts", definitions[2]);
    }

    internal static void ValidateShapeSeventeen(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var definitions = SchemaSeventeen.Split(
            ';',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        SqliteWorkspaceWork.ValidateTableDefinition(command, "agent_client_authorizations", definitions[0]);
        SqliteWorkspaceWork.ValidateTableDefinition(command, "pending_agent_proposal", definitions[1]);
        SqliteWorkspaceWork.ValidateTableDefinition(command, "agent_proposal_receipts", definitions[2]);
    }

    private static PendingAgentProposal? ReadPending(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string receipt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT receipt,idempotency_key,authorization_id,client_identity,client_name,received_at,
                   proposal_kind,operation_count,effect_count,review_basis,strict_utf8
            FROM pending_agent_proposal WHERE receipt=$receipt;
            """;
        command.Parameters.AddWithValue("$receipt", receipt);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPending(reader) : null;
    }

    private static AgentClientAuthorization ReadAuthorization(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        (AgentClientScopes)reader.GetInt32(5),
        ParseInstant(reader.GetString(6)));

    private static PendingAgentProposal ReadPending(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        ParseInstant(reader.GetString(5)),
        ReadProposalKind(reader, 6),
        reader.GetInt32(7),
        reader.GetInt32(8),
        reader.IsDBNull(9) ? ReadOnlySpan<byte>.Empty : (byte[])reader.GetValue(9),
        (byte[])reader.GetValue(10));

    private static ProposalKind? ReadProposalKind(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var kind = (ProposalKind)reader.GetInt32(ordinal);
        return Enum.IsDefined(kind) ? kind : throw new InvalidDataException();
    }

    private static int ReadPendingCount(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM pending_agent_proposal;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static StoredReceipt? ReadReceipt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string clientIdentity,
        string idempotencyKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payload_digest,receipt
            FROM agent_proposal_receipts
            WHERE client_identity=$identity AND idempotency_key=$key;
            """;
        command.Parameters.AddWithValue("$identity", clientIdentity);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private sealed record StoredReceipt(string PayloadDigest, string Receipt);

    private static T Guard<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (exception is SqliteException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or FormatException
                                          or InvalidDataException
                                          or OverflowException)
        {
            throw new WorkspaceWorkException();
        }
    }

    private static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            ? instant
            : throw new InvalidDataException();

    private static void ValidateCredentialVerifier(string value)
    {
        ValidateMetadata(value, 128, nameof(value));
        if (value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Credential verifiers must be lowercase SHA-256 hex.", nameof(value));
        }
    }

    private static void ValidateScopes(AgentClientScopes scopes)
    {
        const AgentClientScopes all = AgentClientScopes.ReadInterchangeContext
                                      | AgentClientScopes.SubmitProposals;
        if (scopes == AgentClientScopes.None || (scopes & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scopes));
        }
    }

    private static void ValidateMetadata(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Value is outside the bounded metadata contract.", parameterName);
        }
    }
}
