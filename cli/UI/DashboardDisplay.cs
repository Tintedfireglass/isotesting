using Spectre.Console;

namespace Pramaan.CLI.UI;

public static class DashboardDisplay
{
    private static readonly object Gate = new();

    public static void Refresh(LiveDisplayContext? context, DashboardState state)
    {
        if (context == null) return;
        lock (Gate)
        {
            context.UpdateTarget(DashboardRenderer.Build(state));
            context.Refresh();
        }
    }

    public static bool Navigate(ConsoleKey key, DashboardState state)
    {
        switch (key)
        {
            case ConsoleKey.Tab: state.SelectedView = (state.SelectedView + 1) % DashboardRenderer.Views.Length; state.Page = 0; return true;
            case ConsoleKey.PageUp: state.Page = Math.Max(0, state.Page - 1); return true;
            case ConsoleKey.PageDown: state.Page++; return true;
            default: return false;
        }
    }

    public static async Task RunAsync(DashboardState state, Func<Task> operation)
    {
        await AnsiConsole.Live(DashboardRenderer.Build(state)).AutoClear(true).StartAsync(async context =>
        {
            state.IsTesting = true;
            var start = DateTime.UtcNow;
            // Diagnostics include synchronous system probes; run them off the UI loop.
            var work = Task.Run(operation);
            try
            {
                while (!work.IsCompleted)
                {
                    while (Console.KeyAvailable) Navigate(Console.ReadKey(true).Key, state);
                    state.Elapsed = (DateTime.UtcNow - start).ToString(@"hh\:mm\:ss");
                    Refresh(context, state);
                    await Task.Delay(100);
                }
                await work;
                Refresh(context, state);
            }
            finally
            {
                try { await work; }
                finally { state.IsTesting = false; }
            }
        });
    }
}
