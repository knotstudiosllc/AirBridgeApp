namespace AirBridge;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // One instance; a second launch just brings the existing window forward.
        using var mutex = new Mutex(true, @"Global\AirBridge.SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\AirBridge.Show");
        if (!first)
        {
            showSignal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        var settings = Settings.Load();
        using var engine = new Engine(settings);
        var form = new MainForm(engine, startHidden: args.Contains("--background"));

        var listener = new Thread(() =>
        {
            while (showSignal.WaitOne())
                form.BeginInvoke(form.ShowWindow);
        }) { IsBackground = true };
        listener.Start();

        Application.Run(form);
    }
}
