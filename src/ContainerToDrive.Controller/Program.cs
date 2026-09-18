using System.Text.Json;
using ContainerToDrive.Core;
using ContainerToDrive.Windows;
using ContainerToDrive.Controller;

var root = AppPaths.ResolveDataRoot(args);
try
{
    if (args.Contains("--import-sas"))
    {
        if (AppPaths.IsElevated) { Console.Error.WriteLine("Run credential import in a normal, non-administrator terminal."); return 2; }
        Console.WriteLine("Paste the container SAS (hidden; never logged), then press Enter:");
        var secret = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            // Some terminal hosts leave the command-launch Enter in the input
            // buffer. Ignore empty submissions so the hidden prompt remains live.
            if (key.Key == ConsoleKey.Enter)
            {
                if (secret.Length == 0) continue;
                break;
            }
            if (key.Key == ConsoleKey.Escape) return 1;
            if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
            else if (!char.IsControl(key.KeyChar) && secret.Length < 16384) secret.Append(key.KeyChar);
        }
        Console.WriteLine();
        var sas = secret.ToString(); secret.Clear();
        var info = Validation.ParseSas(sas);
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(x => ((char)x).ToString()).First(x => !DriveInfo.GetDrives().Any(d => d.Name.StartsWith(x + ":", StringComparison.OrdinalIgnoreCase)));
        var profile = new Profile
        {
            Name = "Azure test container",
            Endpoint = info.Endpoint,
            Container = info.Container,
            ExpiresAt = info.ExpiresAt,
            DriveLetter = letter,
            AuthenticationKind = AuthenticationKind.ContainerSas
        };
        var client = new ControllerClient(root);
        await client.EnsureStartedAsync();
        var saved = await client.SendAsync(new Request
        {
            Operation = "Save",
            Profile = profile,
            Credential = CredentialSubmission.ContainerSas(sas)
        });
        sas = "";
        if (!saved.Success) { Console.Error.WriteLine(saved.Message); return 1; }
        var result = await client.SendAsync(new Request { Operation = "Mount", ProfileId = profile.Id });
        Console.WriteLine(result.Message);
        return result.Success ? 0 : 1;
    }
    if (args.Contains("--status"))
    {
        var result = await new ControllerClient(root).SendAsync(new Request { Operation = "Status" });
        Console.WriteLine(JsonSerializer.Serialize(result, Wire.Json)); return result.Success ? 0 : 1;
    }
    using var controller = new MountController(root);
    AppPaths.SecureDirectory(Path.Combine(root, "runtime"));
    FileStream singleton;
    try { singleton = new FileStream(Path.Combine(root, "runtime", "controller-" + AppPaths.SessionKey(root) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { return 0; }
    using (singleton)
    {
        await controller.AutoMountAsync();
        using var statisticsStop = new CancellationTokenSource();
        var statisticsTask = controller.RunStatisticsAsync(statisticsStop.Token);
        try
        {
        while (!controller.ShutdownRequested)
        {
            using var pipe = LocalPipe.CreateServer(root);
            await pipe.WaitForConnectionAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(85));
            try
            {
                LocalPipe.ValidateClient(pipe);
                var request = await PipeWire.ReadAsync<Request>(pipe, timeout.Token);
                if (request.Operation == "RelocateData") timeout.CancelAfter(TimeSpan.FromMinutes(30));
                var response = await controller.HandleAsync(request, timeout.Token);
                await PipeWire.WriteAsync(pipe, response, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or JsonException)
            { /* Never echo unauthenticated/malformed client data. */ }
        }
        }
        finally
        {
            await statisticsStop.CancelAsync();
            await statisticsTask;
        }
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(Redaction.Clean(ex.Message));
    return 1;
}