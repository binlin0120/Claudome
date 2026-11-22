using System.IO;

namespace ClaudeSetup;

// Synthetic command runner only: never launches VBoxManage or creates a VM.
public static class ReleaseReviewTests
{
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(Func<string, Func<Task>, Task> check, string root)
    {
        foreach (var directory in new[] { false, true })
            await check("vbox-copy-target-" + (directory ? "directory" : "renamed-file"), async () => {
                var state = new SessionState { VmId = "11111111-1111-1111-1111-111111111111", Options = new() { Root = Path.Combine(root, "copy-" + directory) } };
                var source = Path.Combine(root, "random-private-input.json");
                var target = @"C:\Users\ClaudeUser\AppData\Local\ClaudeEnvironmentSetup\" + (directory ? "" : "payload.json");
                var runner = new CopyRunner();
                await new VirtualBox(runner, "fixture-only").CopyTo(state, new() { VmPassword = "fictional-password-only" }, source, target, CancellationToken.None);
                var args = runner.Arguments!;
                Assert(args.Take(3).SequenceEqual(new[] { "guestcontrol", state.VmId, "copyto" }), "wrong VM/operation");
                Assert(!args.Contains("fictional-password-only") && args.Contains("--passwordfile"), "password exposed in command line");
                Assert(directory ? args.Contains("--target-directory") && args[^1] == source : !args.Contains("--target-directory") && args[^2] == source && args[^1] == target,
                    "named target file treated as a directory, or source filename not preserved for directory copy");
            });
        await check("vbox-creates-the-guest-directory-as-a-native-path-argument", async () => {
            var state = new SessionState
            {
                VmId = "11111111-1111-1111-1111-111111111111",
                Options = new()
                {
                    Root = Path.Combine(root, "guest-directory"),
                    UsesMicrosoftGuestAccount = true,
                    GuestLocalAppData = @"C:\Users\Guest Folder\AppData\Local"
                }
            };
            var target = state.Options.GuestSetupDirectory + @"\pwsh";
            var runner = new CopyRunner();
            var credentials = new Secrets { VmPassword = "fictional-password", VmMicrosoftAddress = "test@example.invalid" };
            await new VirtualBox(runner, "fixture-only").EnsureGuestDirectory(state, credentials, target, CancellationToken.None);
            var args = runner.Arguments!;
            Assert(args.Take(3).SequenceEqual(["guestcontrol", state.VmId, "mkdir"]) &&
                args[^2] == "--parents" && args[^1] == target, "Guest path was split or sent through cmd.exe");
            Assert(args.Contains("--passwordfile") && !args.Contains(credentials.VmPassword) &&
                args.Contains(credentials.VmMicrosoftAddress), "Directory creation did not use the synchronized account safely");
        });
        await check("vbox-direct-program-arguments-do-not-duplicate-argv-zero", async () => {
            var state = new SessionState
            {
                VmId = "11111111-1111-1111-1111-111111111111",
                Options = new() { Root = Path.Combine(root, "guest-exe-arguments"), UsesMicrosoftGuestAccount = true }
            };
            var runner = new CopyRunner();
            await using var box = new VirtualBox(runner, "fixture-only");
            var credentials = new Secrets { VmPassword = "fictional-password", VmMicrosoftAddress = "test@example.invalid" };
            const string directory = @"C:\Users\Guest Folder\AppData\Local\ClaudeEnvironmentSetup";
            string[] tarArguments = ["-xf", directory + @"\PowerShell.zip", "-C", directory + @"\pwsh"];
            string[] scriptArguments = ["-NoLogo", "-NoProfile", "-File", directory + @"\Configure.ps1", "-PayloadPath", directory + @"\payload.json"];
            foreach (var (executable, arguments) in new[]
            {
                (@"C:\Windows\System32\tar.exe", tarArguments),
                (directory + @"\pwsh\pwsh.exe", scriptArguments)
            })
            {
                await box.GuestRun(state, credentials, executable, arguments, CancellationToken.None);
                var command = runner.Arguments!;
                var separator = Array.IndexOf(command, "--");
                Assert(separator >= 0 && command[Array.IndexOf(command, "--exe") + 1] == executable &&
                    command[(separator + 1)..].SequenceEqual(arguments), "An executable was repeated or its arguments were split");
                Assert(command.Contains("--profile") && command.Contains("--passwordfile") && !command.Contains(credentials.VmPassword),
                    "Guest command did not use the verified account safely");
            }
        });
        await check("resume-rejects-other-workspace-and-moved-root", () => {
            var state = new SessionState { Options = new() { Root = Path.Combine(root, "original-task") } };
            var attempts = new[] { new SetupOptions { Root = state.Options.Root }, new SetupOptions { WorkspaceId = state.Options.WorkspaceId, Root = Path.Combine(root, "other-task") } };
            foreach (var o in attempts) {
                var rejected = false; try { state.RequireResumeIdentity(o); } catch (SetupException) { rejected = true; }
                Assert(rejected, "mixed workspace identity was accepted");
            }
            state.RequireResumeIdentity(new() { WorkspaceId = state.Options.WorkspaceId, Root = state.Options.Root + "\\" });
            return Task.CompletedTask;
        });
    }
    private sealed class CopyRunner : ICommandRunner
    {
        public string[]? Arguments;
        public Task<string> Run(string exe, IEnumerable<string> args, CancellationToken token, int seconds = 120)
        { Arguments = args.ToArray(); return Task.FromResult(""); }
    }
}
