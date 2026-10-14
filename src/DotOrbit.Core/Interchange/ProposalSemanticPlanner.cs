using System.Globalization;
using System.Text;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Core.Interchange;

public sealed class ProposalSemanticPlanner(TimeProvider timeProvider, IProposalMarkdownValidator markdownValidator)
{
    public ProposalPlanResult Validate(ChangeProposal proposal, ProposalWorkspaceState source,
        string currentSnapshotId, string proposalDigest)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(source);
        if (proposal.SourceWorkspaceRevision != source.Revision || proposal.SourceSnapshotId != currentSnapshotId)
            return Failed("stale-source");
        if (proposal.Format != AgentInterchangeV1.ChangeProposalFormat || proposal.SchemaVersion != 1)
            return Failed("unsupported-version");
        if (proposal.Operations.Count is < 1 or > 512) return Failed("operation-limit");
        var simulation = new Simulation(source, timeProvider, markdownValidator);
        for (var index = 0; index < proposal.Operations.Count; index++)
        {
            try { simulation.Execute(proposal.Operations[index]); }
            catch (InvalidProposalException error) { return Failed(error.Code, index); }
            catch (EncoderFallbackException) { return Failed("invalid-unicode", index); }
            catch (ArgumentException) { return Failed("invalid-domain-value", index); }
        }
        try
        {
            simulation.Finish();
            var operations = proposal.Operations.Select(Freeze).ToArray();
            return new(new(proposal.SourceSnapshotId, source.Revision, proposalDigest,
                Array.AsReadOnly(operations), Array.AsReadOnly(simulation.Effects.ToArray())), []);
        }
        catch (InvalidProposalException error) { return Failed(error.Code); }
    }

    private static ChangeOperation Freeze(ChangeOperation operation) => operation switch
    {
        CreateTaskOperation item => item with { Participants = Array.AsReadOnly(item.Participants.ToArray()) },
        ReplaceTaskParticipantsOperation item => item with { Participants = Array.AsReadOnly(item.Participants.ToArray()) },
        _ => operation,
    };
    private static ProposalPlanResult Failed(string code, int? index = null) =>
        new(null, Array.AsReadOnly(new[] { new ProposalValidationFailure(code, index) }));
    private sealed class InvalidProposalException(string code) : Exception("The proposal is invalid.")
    { public string Code { get; } = code; }
    private static void Check(bool condition, string code) { if (!condition) throw new InvalidProposalException(code); }

    private sealed class Simulation
    {
        private readonly Dictionary<string, WorkspaceCategory> categories;
        private readonly Dictionary<string, ParticipantRecord> participants;
        private readonly Dictionary<string, ProjectRecord> projects;
        private readonly Dictionary<string, TaskRecord> tasks;
        private readonly Dictionary<string, (string Key, string Kind)> locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProposalReference> references = new(StringComparer.Ordinal);
        private readonly HashSet<string> created = new(StringComparer.Ordinal);
        private readonly HashSet<string> operationIds = new(StringComparer.Ordinal);
        private readonly HashSet<(string Key, string Facet)> facets = [];
        private readonly HashSet<string> terminal = new(StringComparer.Ordinal);
        private readonly HashSet<string> targetedTasks = new(StringComparer.Ordinal);
        private readonly HashSet<string> touchedParents = new(StringComparer.Ordinal);
        private readonly HashSet<string> terminalProjects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> originalParents;
        private readonly HashSet<string> binnedTasks = new(StringComparer.Ordinal);
        private readonly HashSet<string> binnedProjects = new(StringComparer.Ordinal);
        private readonly ProposalWorkspaceState source;
        private readonly IProposalMarkdownValidator markdown;
        private readonly DateTimeOffset instant;
        private readonly DateOnly date;
        private int nodes;
        private long bytes;
        public List<ProposalPreviewEffect> Effects { get; } = [];

        public Simulation(ProposalWorkspaceState source, TimeProvider time, IProposalMarkdownValidator markdown)
        {
            this.source = source;
            this.markdown = markdown;
            instant = time.GetUtcNow();
            date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, time.LocalTimeZone).DateTime);
            categories = source.Workspace.Categories.ToDictionary(x => x.Id, StringComparer.Ordinal);
            participants = source.Workspace.Participants.ToDictionary(x => x.Id, StringComparer.Ordinal);
            projects = source.Workspace.Projects.ToDictionary(x => x.Id, StringComparer.Ordinal);
            tasks = source.Workspace.Tasks.ToDictionary(x => x.Id,
                x => x with { ParticipantIds = Array.AsReadOnly(x.Participants.ToArray()) }, StringComparer.Ordinal);
            originalParents = tasks.ToDictionary(x => x.Key, x => x.Value.ProjectId, StringComparer.Ordinal);
            foreach (var key in categories.Keys.Concat(participants.Keys).Concat(projects.Keys).Concat(tasks.Keys))
                references[key] = ProposalReference.Existing(key);
        }
        private string Resolve(ProposalReference reference, string kind)
        {
            string key;
            if (reference.LocalId is { } local)
            {
                Check(locals.TryGetValue(local, out var value) && value.Kind == kind, "invalid-local-reference");
                key = value.Key;
            }
            else
            {
                Check(reference.ExistingId is not null, "invalid-reference"); key = reference.ExistingId!;
                Check(kind switch
                {
                    "category" => source.Workspace.Categories.Any(item => item.Id == key),
                    "participant" => source.Workspace.Participants.Any(item => item.Id == key),
                    "project" => source.Workspace.Projects.Any(item => item.Id == key),
                    "task" => source.Workspace.Tasks.Any(item => item.Id == key),
                    _ => false,
                }, "invalid-reference");
            }
            Check(kind switch { "category" => categories.ContainsKey(key), "participant" => participants.ContainsKey(key),
                "project" => projects.ContainsKey(key) && !projects[key].IsArchived && !binnedProjects.Contains(key),
                "task" => tasks.ContainsKey(key) && !tasks[key].IsArchived && !binnedTasks.Contains(key)
                    && (tasks[key].ProjectId is null || !projects[tasks[key].ProjectId!].IsArchived), _ => false }, "invalid-reference");
            Check(!terminal.Contains(key), "terminal-target");
            return key;
        }
        private string Create(string local, string kind)
        {
            Check(!locals.ContainsKey(local), "duplicate-local-id");
            var key = "\u0000local:" + local;
            while (references.ContainsKey(key)) key = "\u0000" + key;
            locals.Add(local, (key, kind)); created.Add(key); references.Add(key, ProposalReference.Local(local));
            return key;
        }
        private void Write(string key, params string[] names)
        {
            Check(!terminal.Contains(key), "terminal-target");
            foreach (var name in names) Check(facets.Add((key, name)), "facet-conflict");
            if (tasks.ContainsKey(key)) { targetedTasks.Add(key); if (tasks[key].ProjectId is { } parent) touchedParents.Add(parent); }
        }
        private void Effect(string kind, string key, string before, string after, params string[] markdownFields)
        {
            Check(Effects.Count < 2000, "preview-effect-limit");
            var label = tasks.TryGetValue(key, out var task) ? task.Title : projects.TryGetValue(key, out var project) ? project.Title
                : categories.TryGetValue(key, out var category) ? category.Name : participants.TryGetValue(key, out var participant) ? participant.Label
                : source.TaskBin.First(item => item.Task.Id == key).Task.Title;
            bytes += new UTF8Encoding(false, true).GetByteCount(kind) + new UTF8Encoding(false, true).GetByteCount(before) + new UTF8Encoding(false, true).GetByteCount(after);
            bytes += new UTF8Encoding(false, true).GetByteCount(label) + new UTF8Encoding(false, true).GetByteCount(references[key].ExistingId ?? references[key].LocalId!)
                + markdownFields.Sum(field => (long)new UTF8Encoding(false, true).GetByteCount(field));
            Check(bytes <= 4 * 1024 * 1024, "preview-text-limit");
            Effects.Add(new(kind, references[key], label, before, after, Array.AsReadOnly(markdownFields)));
        }
        private void Markdown(string value, bool proposed = true)
        {
            var result = markdown.Validate(value);
            Check(result.IsValid && result.NodeCount <= (proposed ? 4096 : 65536) && (!proposed || result.ContainerDepth <= 32), "markdown-limit");
            nodes += result.NodeCount; Check(nodes <= 65536, "markdown-preview-limit");
        }
        private string Category(ProposedTaskCategory category, string? parent)
        {
            Check(Enum.IsDefined(category.Mode), "invalid-category-mode");
            if (category.Mode == ProposedTaskCategoryMode.Inherit)
            { Check(parent is not null && category.Category is null, "invalid-category-state"); return null!; }
            Check(category.Category is not null, "invalid-category-state");
            return Resolve(category.Category!.Value, "category");
        }
        private string Effective(TaskRecord task) => task.ExplicitCategoryId ?? projects[task.ProjectId!].CategoryId;
        private System.Collections.ObjectModel.ReadOnlyCollection<string> Associations(IReadOnlyList<ProposalReference> refs)
        {
            Check(refs.Count <= 256, "association-limit");
            var ordered = refs.OrderBy(x => x.ExistingId is null ? 1 : 0)
                .ThenBy(x => (x.ExistingId ?? x.LocalId)!, UnicodeScalarOrdinalComparer.Instance).ToArray();
            var values = ordered.Select(x => Resolve(x, "participant")).ToArray();
            Check(values.Distinct(StringComparer.Ordinal).Count() == values.Length, "duplicate-association");
            return Array.AsReadOnly(values);
        }
        private List<string> Order(string project) => tasks.Values.Where(x => x.ProjectId == project && !binnedTasks.Contains(x.Id))
            .OrderBy(x => x.ProjectPosition).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToList();
        private void SetOrder(string project, List<string> order)
        { for (var i = 0; i < order.Count; i++) tasks[order[i]] = tasks[order[i]] with { ProjectPosition = i }; }
        private long EndPosition(string project) => Order(project).Count;
        public void Execute(ChangeOperation operation)
        {
            Check(operationIds.Add(operation.OperationId), "duplicate-operation-id");
            if (operation.Explanation is { } explanation) Markdown(explanation);
            switch (operation)
            {
                case CreateCategoryOperation x:
                {
                    var validation = CategoryName.Create(x.Name); Check(validation.IsValid, "invalid-category-name");
                    var name = validation.CategoryName!.Value;
                    Check(!categories.Values.Any(c => StringComparer.OrdinalIgnoreCase.Equals(c.Name, name)), "duplicate-category-name");
                    var id = Create(x.LocalId, "category"); categories.Add(id, new(id, name, categories.Count)); Effect("create-category", id, "", name + "; appended to Category order; colour assigned by application"); break;
                }
                case CreateParticipantOperation x:
                {
                    var label = ParticipantLabel.Normalize(x.Label);
                    Check(!participants.Values.Any(p => ParticipantLabel.ComparisonKey(p.Label) == ParticipantLabel.ComparisonKey(label)), "duplicate-participant-label");
                    var id = Create(x.LocalId, "participant"); participants.Add(id, new(id, label)); Effect("create-participant", id, "", label); break;
                }
                case CreateProjectOperation x:
                {
                    var category = Resolve(x.Category, "category"); Markdown(x.Description);
                    var id = Create(x.LocalId, "project"); projects.Add(id, new(id, WorkTitle.Normalize(x.Title), x.Description, category, x.TargetDate, projects.Count));
                    Effect("create-project", id, "", ProjectText(projects[id]) + "; appended to Project order; colour assigned by application", x.Description); break;
                }
                case CreateTaskOperation x:
                {
                    var parent = x.Project is { } reference ? Resolve(reference, "project") : null;
                    var category = Category(x.Category, parent); var associations = Associations(x.Participants); Markdown(x.Description);
                    var position = parent is null ? (long?)null : EndPosition(parent);
                    var id = Create(x.LocalId, "task"); foreach (var existing in tasks.Keys.ToArray()) tasks[existing] = tasks[existing] with { SharedPosition = tasks[existing].SharedPosition + 1 };
                    tasks.Add(id, new(id, parent, WorkTitle.Normalize(x.Title), x.Description, category, x.DueDate, 0, position, ParticipantIds: associations));
                    targetedTasks.Add(id); if (parent is not null) touchedParents.Add(parent); Effect("create-task", id, "", TaskText(tasks[id]), x.Description); break;
                }
                case EditProjectOperation x:
                {
                    var id = Resolve(x.Project, "project"); Write(id, "content"); var before = projects[id];
                    Check(x.Changes.Title.IsSpecified || x.Changes.Description.IsSpecified || x.Changes.TargetDate.IsSpecified, "empty-change");
                    var after = before with { Title = x.Changes.Title.IsSpecified ? WorkTitle.Normalize(x.Changes.Title.Value!) : before.Title,
                        Description = x.Changes.Description.IsSpecified ? x.Changes.Description.Value! : before.Description,
                        TargetDate = x.Changes.TargetDate.IsSpecified ? x.Changes.TargetDate.Value : before.TargetDate };
                    Check(after != before, "no-op"); Markdown(before.Description, false); Markdown(after.Description, x.Changes.Description.IsSpecified); projects[id] = after;
                    Effect("project-content", id, ProjectText(before), ProjectText(after), before.Description, after.Description); break;
                }
                case EditTaskOperation x:
                {
                    var id = Resolve(x.Task, "task"); Write(id, "content"); var before = tasks[id];
                    Check(x.Changes.Title.IsSpecified || x.Changes.Description.IsSpecified || x.Changes.DueDate.IsSpecified, "empty-change");
                    var after = before with { Title = x.Changes.Title.IsSpecified ? WorkTitle.Normalize(x.Changes.Title.Value!) : before.Title,
                        Description = x.Changes.Description.IsSpecified ? x.Changes.Description.Value! : before.Description,
                        DueDate = x.Changes.DueDate.IsSpecified ? x.Changes.DueDate.Value : before.DueDate };
                    Check(after != before, "no-op"); Markdown(before.Description, false); Markdown(after.Description, x.Changes.Description.IsSpecified); tasks[id] = after;
                    Effect("task-content", id, TaskText(before), TaskText(after), before.Description, after.Description); break;
                }
                case SetProjectCategoryOperation x:
                {
                    var id = Resolve(x.Project, "project"); var category = Resolve(x.Category, "category"); Write(id, "category");
                    Check(projects[id].CategoryId != category, "no-op"); var before = projects[id].CategoryId; projects[id] = projects[id] with { CategoryId = category };
                    Effect("project-category", id, Name(before), Name(category));
                    foreach (var child in tasks.Values.Where(t => t.ProjectId == id && t.ExplicitCategoryId is null))
                        Effect(child.IsArchived ? "archived-child-category" : "inherited-task-category", child.Id, Name(before), Name(category));
                    foreach (var child in source.TaskBin.Where(t => t.Task.ProjectId == id && t.Task.ExplicitCategoryId is null))
                        HiddenEffect("binned-child-category", child.Task, Name(before), Name(category));
                    break;
                }
                case SetTaskCategoryOperation x:
                {
                    var id = Resolve(x.Task, "task"); Write(id, "category"); var before = tasks[id]; var category = Category(x.Category, before.ProjectId);
                    Check(before.ExplicitCategoryId != category, "no-op"); tasks[id] = before with { ExplicitCategoryId = category };
                    Effect("task-category", id, CategoryText(before), CategoryText(tasks[id])); break;
                }
                case ReplaceTaskParticipantsOperation x:
                {
                    var id = Resolve(x.Task, "task"); Write(id, "participants"); var next = Associations(x.Participants);
                    Check(!tasks[id].Participants.ToHashSet(StringComparer.Ordinal).SetEquals(next), "no-op");
                    var before = string.Join(", ", tasks[id].Participants.Select(p => participants[p].Label)); tasks[id] = tasks[id] with { ParticipantIds = next };
                    Effect("participants", id, before, string.Join(", ", next.Select(p => participants[p].Label))); break;
                }
                case AttachTaskOperation x: Relate(x.Task, x.Project, x.CategoryChoice, true); break;
                case MoveTaskOperation x: Relate(x.Task, x.Project, x.CategoryChoice, false); break;
                case DetachTaskOperation x:
                {
                    var id = Resolve(x.Task, "task"); Write(id, "relationship", "category"); var task = tasks[id]; Check(task.ProjectId is not null, "no-op");
                    var parent = task.ProjectId!; tasks[id] = task with { ProjectId = null, ProjectPosition = null, ExplicitCategoryId = Effective(task) }; SetOrder(parent, Order(parent));
                    Effect("relationship", id, projects[parent].Title + "; " + CategoryText(task), "Standalone; " + CategoryText(tasks[id])); break;
                }
                case CompleteTaskOperation x: Completion(x.Task, true); break;
                case ReopenTaskOperation x: Completion(x.Task, false); break;
                case ReorderProjectTaskOperation x: Reorder(x); break;
                case ArchiveTaskOperation x: Lifecycle(x.Task, false, false); break;
                case MoveTaskToBinOperation x: Lifecycle(x.Task, false, true); break;
                case ArchiveProjectOperation x: Lifecycle(x.Project, true, false); break;
                case MoveProjectToBinOperation x: Lifecycle(x.Project, true, true); break;
                default: throw new InvalidProposalException("unsupported-operation");
            }
            if (operation.Explanation is { } retained) Effect("explanation", ExplanationTarget(operation), "", retained, retained);
        }
        private string ExplanationTarget(ChangeOperation operation)
        {
            var reference = operation switch
            {
                CreateCategoryOperation x => ProposalReference.Local(x.LocalId),
                CreateParticipantOperation x => ProposalReference.Local(x.LocalId),
                CreateProjectOperation x => ProposalReference.Local(x.LocalId),
                CreateTaskOperation x => ProposalReference.Local(x.LocalId),
                EditProjectOperation x => x.Project, SetProjectCategoryOperation x => x.Project,
                ArchiveProjectOperation x => x.Project, MoveProjectToBinOperation x => x.Project,
                EditTaskOperation x => x.Task, SetTaskCategoryOperation x => x.Task,
                ReplaceTaskParticipantsOperation x => x.Task, AttachTaskOperation x => x.Task,
                DetachTaskOperation x => x.Task, MoveTaskOperation x => x.Task,
                CompleteTaskOperation x => x.Task, ReopenTaskOperation x => x.Task,
                ReorderProjectTaskOperation x => x.Task, ArchiveTaskOperation x => x.Task,
                MoveTaskToBinOperation x => x.Task,
                _ => throw new InvalidProposalException("unsupported-operation"),
            };
            return reference.ExistingId ?? locals[reference.LocalId!].Key;
        }
        private void Relate(ProposalReference reference, ProposalReference parentRef, AttachmentCategoryChoice? choice, bool attach)
        {
            var id = Resolve(reference, "task"); var parent = Resolve(parentRef, "project"); var task = tasks[id];
            Check(attach ? task.ProjectId is null : task.ProjectId is not null, "invalid-relationship");
            Check(task.ProjectId != parent, "no-op"); Write(id, "relationship", "category");
            var category = Effective(task); var match = category == projects[parent].CategoryId;
            Check(match || choice is not null, "category-choice-required");
            Check(choice is null || Enum.IsDefined(choice.Value), "invalid-category-choice");
            var before = task.ProjectId is null ? "Standalone" : projects[task.ProjectId].Title;
            var oldParent = task.ProjectId; touchedParents.Add(parent);
            tasks[id] = task with { ProjectId = parent, ProjectPosition = EndPosition(parent),
                ExplicitCategoryId = match || choice == AttachmentCategoryChoice.AdoptProjectCategory ? null : category };
            if (oldParent is not null) SetOrder(oldParent, Order(oldParent));
            Effect("relationship", id, before + "; " + CategoryText(task), projects[parent].Title + "; " + CategoryText(tasks[id]));
        }
        private void Completion(ProposalReference reference, bool complete)
        {
            var id = Resolve(reference, "task"); Write(id, "completion"); var task = tasks[id]; Check(task.IsComplete != complete, "no-op");
            tasks[id] = task with { CompletedAt = complete ? instant : null, CompletionDate = complete ? date : null,
                TodayLane = complete ? null : task.TodayLane };
            Effect("completion", id, (task.IsComplete ? "Complete" : "Incomplete") + "; completion date: " + DateText(task.CompletionDate),
                (complete ? "Complete" : "Incomplete") + "; completion date: " + DateText(tasks[id].CompletionDate));
            if (complete && task.IsInToday) Effect("today-removal", id, task.TodayLane!.Value.ToString(), "Not in Today");
        }
        private void Reorder(ReorderProjectTaskOperation operation)
        {
            var id = Resolve(operation.Task, "task"); Write(id, "order"); var parent = tasks[id].ProjectId; Check(parent is not null, "invalid-order");
            var all = Order(parent!); var slots = all.Select((key, index) => (key, index)).Where(x => !tasks[x.key].IsArchived).Select(x => x.index).ToArray();
            var included = slots.Select(i => all[i]).ToList(); var before = included.ToArray(); included.Remove(id);
            var placement = operation.Placement; Check(Enum.IsDefined(placement.Kind), "invalid-placement");
            var index = placement.Kind == ProjectTaskPlacementKind.First ? 0 : included.Count;
            if (placement.Kind is ProjectTaskPlacementKind.Before or ProjectTaskPlacementKind.After)
            {
                Check(placement.Anchor is not null, "invalid-anchor"); var anchor = Resolve(placement.Anchor!.Value, "task");
                Check(anchor != id && tasks[anchor].ProjectId == parent && included.Contains(anchor, StringComparer.Ordinal), "invalid-anchor");
                index = included.IndexOf(anchor) + (placement.Kind == ProjectTaskPlacementKind.After ? 1 : 0);
            }
            else Check(placement.Anchor is null, "invalid-anchor");
            included.Insert(index, id); Check(!before.SequenceEqual(included, StringComparer.Ordinal), "no-op");
            for (var i = 0; i < slots.Length; i++) all[slots[i]] = included[i]; SetOrder(parent!, all);
        }
        private void Lifecycle(ProposalReference reference, bool project, bool bin)
        {
            var id = Resolve(reference, project ? "project" : "task"); Write(id, "lifecycle");
            Check(!created.Contains(id), "new-record-lifecycle"); terminal.Add(id);
            if (project)
            {
                terminalProjects.Add(id);
                Effect(bin ? "project-bin" : "project-archive", id, "Active", bin ? "Bin" : "Archived");
                if (bin) binnedProjects.Add(id); else projects[id] = projects[id] with { ArchivedAt = instant, ArchiveDate = date };
                foreach (var task in tasks.Values.Where(t => t.ProjectId == id).ToArray())
                {
                    if (task.IsInToday) Effect("today-removal", task.Id, task.TodayLane!.Value.ToString(), "Not in Today");
                    tasks[task.Id] = task with { TodayLane = null };
                    Effect(bin ? "aggregate-child-bin" : "parent-archive-visibility", task.Id,
                        task.IsArchived ? "Individually archived" : "Active", bin ? "Bin with Project" : "Hidden by archived Project");
                    if (bin) binnedTasks.Add(task.Id);
                }
                if (bin) foreach (var task in source.TaskBin.Where(t => t.Task.ProjectId == id))
                    HiddenEffect("already-binned-child", task.Task, "Separately in Bin", "Remains separately in Bin; restore blocked until parent restored; remains in Bin after parent restore");
            }
            else
            {
                var task = tasks[id]; Check(bin || task.IsComplete, "archive-incomplete-task");
                Effect(bin ? "task-bin" : "task-archive", id, "Active", bin ? "Bin" : "Archived");
                if (task.IsInToday) Effect("today-removal", id, task.TodayLane!.Value.ToString(), "Not in Today");
                tasks[id] = task with { TodayLane = null, ArchivedAt = bin ? task.ArchivedAt : instant, ArchiveDate = bin ? task.ArchiveDate : date };
                if (bin) binnedTasks.Add(id);
            }
        }
        private void HiddenEffect(string kind, TaskRecord task, string before, string after)
        {
            references.TryAdd(task.Id, ProposalReference.Existing(task.Id));
            Effect(kind, task.Id, task.Title + "; " + before, task.Title + "; " + after);
        }
        private string Name(string category) => categories[category].Name;
        private string CategoryText(TaskRecord task) => (task.ExplicitCategoryId is null ? "Inherited: " : "Explicit: ") + Name(Effective(task));
        private string ProjectText(ProjectRecord project) => project.Title + "\n" + project.Description + "\nCategory: " + Name(project.CategoryId) + "\nTarget date: " + DateText(project.TargetDate);
        private string TaskText(TaskRecord task) => task.Title + "\n" + task.Description + "\n" + CategoryText(task) + "\nDue date: " + DateText(task.DueDate)
            + "\nProject: " + (task.ProjectId is null ? "Standalone" : projects[task.ProjectId].Title)
            + "\nParticipants: " + string.Join(", ", task.Participants.Select(p => participants[p].Label));
        private static string DateText(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "None";
        public void Finish()
        {
            Check(!touchedParents.Overlaps(terminalProjects), "aggregate-child-conflict");
            foreach (var id in targetedTasks)
                Check(!terminalProjects.Contains(originalParents.GetValueOrDefault(id) ?? "")
                    && !terminalProjects.Contains(tasks[id].ProjectId ?? ""), "aggregate-child-conflict");
            var snapshot = Snapshot();
            foreach (var project in projects.Values)
            {
                if (binnedProjects.Contains(project.Id)) continue;
                var before = ProjectWorkSummary.From(source.Workspace, project.Id);
                var after = ProjectWorkSummary.From(snapshot, project.Id);
                if (before != after) Effect("project-status", project.Id,
                    SummaryText(before), SummaryText(after));
            }
            foreach (var project in projects.Values)
            {
                var order = Order(project.Id);
                var original = source.Workspace.Tasks.Where(t => t.ProjectId == project.Id).OrderBy(t => t.ProjectPosition).Select(t => t.Id);
                if (!original.SequenceEqual(order, StringComparer.Ordinal))
                    Effect("final-project-order", project.Id, string.Join("\n", original.Select(OrderText)), string.Join("\n", order.Select(OrderText)));
            }
            var originalShared = source.Workspace.Tasks.OrderBy(t => t.SharedPosition).Select(t => t.Id).ToArray();
            var shared = snapshot.Tasks.OrderBy(t => t.SharedPosition).Select(t => t.Id).ToArray();
            if (!originalShared.SequenceEqual(shared, StringComparer.Ordinal))
            {
                var target = shared.FirstOrDefault() ?? originalShared.FirstOrDefault();
                if (target is not null) Effect("final-shared-order", target, string.Join("\n", originalShared.Select(OrderText)), string.Join("\n", shared.Select(OrderText)));
            }
        }
        private string OrderText(string id) => tasks[id].Title + (tasks[id].IsArchived ? " [Individually archived]" : " [Active]");
        private static string SummaryText(ProjectWorkSummary value) => value.Status + "; " + value.CompletedCount.ToString(CultureInfo.InvariantCulture)
            + "/" + value.TaskCount.ToString(CultureInfo.InvariantCulture) + "; completion date: " + DateText(value.CompletionDate);
        public WorkspaceWorkSnapshot Snapshot() => new(
            Array.AsReadOnly(categories.Values.OrderBy(x => x.Position).ToArray()),
            Array.AsReadOnly(projects.Values.Where(x => !binnedProjects.Contains(x.Id)).OrderBy(x => x.Position).ToArray()),
            Array.AsReadOnly(tasks.Values.Where(x => !binnedTasks.Contains(x.Id)).OrderBy(x => x.SharedPosition).ToArray()),
            Array.AsReadOnly(participants.Values.OrderBy(x => x.Id, UnicodeScalarOrdinalComparer.Instance).ToArray()));
    }
}
