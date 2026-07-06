namespace TimeTracker;

internal static class Program
{
    private const string MutexName = "Global\\TimeTracker.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Учёт времени уже запущен — смотрите иконку в трее.",
                "Учёт времени", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
    }
}
