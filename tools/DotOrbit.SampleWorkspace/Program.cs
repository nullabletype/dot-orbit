namespace DotOrbit.SampleWorkspace;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = SampleWorkspaceOptions.Parse(
                args,
                Environment.CurrentDirectory,
                DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime));
            var summary = SampleWorkspaceGenerator.Generate(options.OutputPath, options.AnchorDate);
            Console.WriteLine(
                $"sample-workspace: result=passed output={summary.OutputPath} schema={summary.SchemaVersion} "
                + $"categories={summary.CategoryCount} projects={summary.ProjectCount} "
                + $"tasks={summary.TaskCount} participants={summary.ParticipantCount}");
            return 0;
        }
        catch (SampleWorkspaceException exception)
        {
            Console.Error.WriteLine($"sample-workspace: result=failed reason={exception.Reason}");
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("sample-workspace: result=failed reason=unexpected-error");
            return 1;
        }
    }
}
