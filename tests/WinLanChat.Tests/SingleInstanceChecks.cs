using System.Diagnostics;
using System.IO;
using WinLanChat.App.Services;

internal static class SingleInstanceChecks
{
    internal static int RunWorker(string name)
    {
        using var instance = SingleInstanceService.TryAcquire(name);
        Console.WriteLine(instance is null ? "secondary" : "primary");
        if (instance is not null)
        {
            instance.ListenForActivation(() => Console.WriteLine("activated"));
            Console.ReadLine();
        }
        return 0;
    }

    internal static int RunDuplicateApplication()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new WinLanChat.App.App();
                application.InitializeComponent();
                application.Startup += (_, _) =>
                    throw new Exception("A second application reached UI startup.");
                application.Run();
                if (application.MainWindow is not null || application.SystemTray is not null)
                    throw new Exception("A second application created a window or tray icon.");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
        return 0;
    }

    internal static async Task LifecycleAsync()
    {
        string name = @"Global\WinLanChat.Tests." + Guid.NewGuid().ToString("N");
        using var first = StartWorker(name);
        try
        {
            await ExpectLineAsync(first, "primary");
            using (var second = StartWorker(name))
            {
                await ExpectLineAsync(second, "secondary");
                await ExpectExitAsync(second);
                await ExpectLineAsync(first, "activated");
            }
            await first.StandardInput.WriteLineAsync("exit");
            await ExpectExitAsync(first);

            using var restarted = StartWorker(name);
            try
            {
                await ExpectLineAsync(restarted, "primary");
                // Keep the kernel object alive to exercise abandoned mutex recovery after a crash.
                using var keeper = new Mutex(false, name);
                restarted.Kill();
                await restarted.WaitForExitAsync();
                using var recovered = StartWorker(name);
                try
                {
                    await ExpectLineAsync(recovered, "primary");
                    await recovered.StandardInput.WriteLineAsync("exit");
                    await ExpectExitAsync(recovered);
                }
                finally { Stop(recovered); }
            }
            finally { Stop(restarted); }
        }
        finally { Stop(first); }

        Process[] contenders = Enumerable.Range(0, 4).Select(_ => StartWorker(name)).ToArray();
        try
        {
            string?[] results = await Task.WhenAll(contenders.Select(p => p.StandardOutput.ReadLineAsync()));
            if (results.Count(result => result == "primary") != 1 ||
                results.Count(result => result == "secondary") != 3)
                throw new Exception("Simultaneous launches did not select exactly one primary instance.");
            for (int i = 0; i < contenders.Length; i++)
                if (results[i] == "primary")
                    await contenders[i].StandardInput.WriteLineAsync("exit");
            await Task.WhenAll(contenders.Select(ExpectExitAsync));
        }
        finally
        {
            foreach (Process contender in contenders)
            {
                Stop(contender);
                contender.Dispose();
            }
        }
    }

    internal static Process StartDuplicateApplication() => Start("--duplicate-app");

    private static Process StartWorker(string name) => Start("--single-instance-worker", name);

    private static Process Start(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(SingleInstanceChecks).Assembly.Location);
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new Exception("Could not start an instance check.");
    }

    private static async Task ExpectLineAsync(Process process, string expected)
    {
        string? actual = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (actual != expected)
            throw new Exception($"Expected {expected}, received {actual}.");
    }

    internal static async Task ExpectExitAsync(Process process)
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (process.ExitCode != 0)
            throw new Exception($"Instance check failed: {await process.StandardError.ReadToEndAsync()}");
    }

    internal static void Stop(Process process)
    {
        if (!process.HasExited)
            process.Kill();
    }
}
