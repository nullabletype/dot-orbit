namespace DotOrbit.Verification;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = VerificationOptions.Parse(args);
        if (options is null)
        {
            return 2;
        }

        var repositoryRoot = RepositoryRoot.Find(AppContext.BaseDirectory);
        var output = new ConsoleVerificationOutput();
        var runner = new ProcessRunner(output);
        var repository = new GitRepositoryInspector(repositoryRoot, runner);
        var workspaces = new GitVerificationWorkspaceProvider(
            repositoryRoot,
            runner,
            reportDiagnostic: output.WriteError);
        var gate = new VerificationGate(repositoryRoot, repository, workspaces, runner, output);
        try
        {
            return await gate.RunAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("verify: result=failed reason=unexpected-error");
            return 1;
        }
    }
}
