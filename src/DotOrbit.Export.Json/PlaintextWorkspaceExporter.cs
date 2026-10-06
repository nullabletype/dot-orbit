using System.Buffers;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Export.Json;

public interface IPlaintextWorkspaceExporter
{
    Task ExportAsync(
        WorkspaceWorkSnapshot snapshot,
        string destinationPath,
        CancellationToken cancellationToken = default);
}

public sealed class PlaintextWorkspaceExporter : IPlaintextWorkspaceExporter
{
    private readonly IIdentifierGenerator _identifiers;

    public PlaintextWorkspaceExporter()
        : this(new SystemIdentifierGenerator())
    {
    }

    public PlaintextWorkspaceExporter(IIdentifierGenerator identifiers) =>
        _identifiers = identifiers ?? throw new ArgumentNullException(nameof(identifiers));

    public const string Format = "dot-orbit-plaintext-export";
    public const int SchemaVersion = 1;

    public static byte[] CreateDocument(WorkspaceWorkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteNumber("schemaVersion", SchemaVersion);
            WriteCategories(writer, snapshot.Categories);
            WriteParticipants(writer, snapshot.Participants);
            WriteProjects(writer, snapshot.Projects);
            WriteTasks(writer, snapshot.Tasks);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public async Task ExportAsync(
        WorkspaceWorkSnapshot snapshot,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var directoryPath = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("The export destination must have a parent directory.", nameof(destinationPath));
        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(fullDestinationPath)}.{_identifiers.NewIdentifier()}.tmp");
        var temporaryFileCreated = false;

        try
        {
            var content = CreateDocument(snapshot);
            await using (var stream = CreateTemporaryStream(temporaryPath, fullDestinationPath))
            {
                temporaryFileCreated = true;
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            if (temporaryFileCreated && File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static FileStream CreateTemporaryStream(string temporaryPath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = File.Exists(destinationPath)
                ? new FileInfo(destinationPath).GetAccessControl()
                : CreateOwnerOnlySecurity();
            return new FileInfo(temporaryPath).Create(
                FileMode.CreateNew,
                FileSystemRights.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous,
                security);
        }

        var mode = File.Exists(destinationPath)
            ? File.GetUnixFileMode(destinationPath)
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous,
            UnixCreateMode = mode,
        };
        var stream = new FileStream(temporaryPath, options);
        File.SetUnixFileMode(stream.SafeFileHandle, mode);
        return stream;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateOwnerOnlySecurity()
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user identity is unavailable.");
        var security = new FileSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private static void WriteCategories(Utf8JsonWriter writer, IReadOnlyList<WorkspaceCategory> categories)
    {
        writer.WriteStartArray("categories");
        foreach (var category in categories.OrderBy(item => item.Position).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", category.Id);
            writer.WriteString("name", category.Name);
            writer.WriteNumber("position", category.Position);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteParticipants(Utf8JsonWriter writer, IReadOnlyList<ParticipantRecord> participants)
    {
        writer.WriteStartArray("participants");
        foreach (var participant in participants.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", participant.Id);
            writer.WriteString("label", participant.Label);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteProjects(Utf8JsonWriter writer, IReadOnlyList<ProjectRecord> projects)
    {
        writer.WriteStartObject("projects");
        WriteProjectCollection(writer, "active", projects.Where(item => !item.IsArchived));
        WriteProjectCollection(writer, "archived", projects.Where(item => item.IsArchived));
        writer.WriteEndObject();
    }

    private static void WriteProjectCollection(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<ProjectRecord> projects)
    {
        writer.WriteStartArray(propertyName);
        foreach (var project in projects.OrderBy(item => item.Position).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", project.Id);
            writer.WriteString("title", project.Title);
            writer.WriteString("description", project.Description);
            writer.WriteString("categoryId", project.CategoryId);
            WriteDate(writer, "targetDate", project.TargetDate);
            writer.WriteNumber("position", project.Position);
            writer.WriteBoolean("isArchived", project.IsArchived);
            WriteInstant(writer, "archiveInstant", project.ArchivedAt);
            WriteDate(writer, "archiveDate", project.ArchiveDate);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteTasks(Utf8JsonWriter writer, IReadOnlyList<TaskRecord> tasks)
    {
        writer.WriteStartObject("tasks");
        WriteTaskCollection(writer, "active", tasks.Where(item => !item.IsArchived));
        WriteTaskCollection(writer, "archived", tasks.Where(item => item.IsArchived));
        writer.WriteEndObject();
    }

    private static void WriteTaskCollection(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<TaskRecord> tasks)
    {
        writer.WriteStartArray(propertyName);
        foreach (var task in tasks.OrderBy(item => item.SharedPosition).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", task.Id);
            WriteString(writer, "projectId", task.ProjectId);
            writer.WriteString("title", task.Title);
            writer.WriteString("description", task.Description);
            WriteString(writer, "explicitCategoryId", task.ExplicitCategoryId);
            WriteDate(writer, "dueDate", task.DueDate);
            writer.WriteNumber("sharedPosition", task.SharedPosition);
            if (task.ProjectPosition is { } projectPosition)
                writer.WriteNumber("projectPosition", projectPosition);
            else
                writer.WriteNull("projectPosition");
            writer.WriteBoolean("isComplete", task.IsComplete);
            WriteInstant(writer, "completionInstant", task.CompletedAt);
            WriteDate(writer, "completionDate", task.CompletionDate);
            writer.WriteStartArray("participantIds");
            foreach (var participantId in task.Participants) writer.WriteStringValue(participantId);
            writer.WriteEndArray();
            WriteString(writer, "todayLane", TodayLaneValue(task.TodayLane));
            writer.WriteBoolean("isArchived", task.IsArchived);
            WriteInstant(writer, "archiveInstant", task.ArchivedAt);
            WriteDate(writer, "archiveDate", task.ArchiveDate);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string? TodayLaneValue(TodayLane? lane) => lane switch
    {
        null => null,
        TodayLane.Planned => "planned",
        TodayLane.InProgress => "inProgress",
        _ => throw new ArgumentOutOfRangeException(nameof(lane)),
    };

    private static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null) writer.WriteNull(propertyName);
        else writer.WriteString(propertyName, value);
    }

    private static void WriteDate(Utf8JsonWriter writer, string propertyName, DateOnly? value)
    {
        if (value is null) writer.WriteNull(propertyName);
        else writer.WriteString(propertyName, value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    private static void WriteInstant(Utf8JsonWriter writer, string propertyName, DateTimeOffset? value)
    {
        if (value is null) writer.WriteNull(propertyName);
        else writer.WriteString(propertyName, value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }
}
