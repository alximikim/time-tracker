using System.Reflection;
using TimeTracker.Storage;
using TimeTracker.Sync;
using TimeTracker.Tracking;

namespace TimeTracker;

/// <summary>
/// Приложение живёт только в трее — без видимого главного окна. NotifyIcon +
/// ContextMenuStrip держат весь UI: старт/стоп, текущий сотрудник, выход.
/// </summary>
public class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _employeeLabelItem;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private readonly LocalDb _db;
    private readonly ApiClient _apiClient;
    private readonly SyncService _syncService;
    private readonly SessionManager _sessionManager;
    private readonly ShutdownWatcher _shutdownWatcher;
    private readonly EmployeeConfig _employee;

    private readonly Icon _iconStopped = BuildTrayIcon(badgeColor: null);
    private readonly Icon _iconRunning = BuildTrayIcon(badgeColor: Color.ForestGreen);

    public TrayAppContext()
    {
        _employee = Config.Load() ?? PromptForEmployee(initial: null);
        Config.Save(_employee);

        AutoStart.Ensure();

        _db = new LocalDb(Config.DataDbPath);
        _apiClient = new ApiClient();
        _syncService = new SyncService(_db, _apiClient);
        _sessionManager = new SessionManager(_db, _employee, Environment.MachineName, _syncService.RequestSyncNow);
        _shutdownWatcher = new ShutdownWatcher(_sessionManager);

        _startItem = new ToolStripMenuItem("Начать работу", null, (_, _) => _sessionManager.StartWork());
        _stopItem = new ToolStripMenuItem("Закончить работу", null, (_, _) => _sessionManager.StopWork());
        _employeeLabelItem = new ToolStripMenuItem($"Сотрудник: {_employee.EmployeeName}") { Enabled = false };
        var changeEmployeeItem = new ToolStripMenuItem("Сменить сотрудника", null, (_, _) => ChangeEmployee());
        var exitItem = new ToolStripMenuItem("Выход", null, (_, _) => ExitApp());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_employeeLabelItem);
        menu.Items.Add(changeEmployeeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconStopped,
            Text = "Учёт времени — остановлено",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleWork();

        _sessionManager.StateChanged += UpdateUi;

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => UpdateUi();
        _uiTimer.Start();

        _sessionManager.RecoverOrphanedSessionIfAny();
        _syncService.Start();

        UpdateUi();
    }

    private void ToggleWork()
    {
        if (_sessionManager.State == WorkState.Running)
            _sessionManager.StopWork();
        else
            _sessionManager.StartWork();
    }

    private void ChangeEmployee()
    {
        var updated = PromptForEmployee(_employee.EmployeeName);
        _employee.EmployeeId = updated.EmployeeId;
        _employee.EmployeeName = updated.EmployeeName;
        Config.Save(_employee);
        _employeeLabelItem.Text = $"Сотрудник: {_employee.EmployeeName}";
    }

    private static EmployeeConfig PromptForEmployee(string? initial)
    {
        using var form = new FirstRunForm(initial ?? "");
        while (form.ShowDialog() != DialogResult.OK)
        {
            // Пустое имя форма сама не даёт подтвердить (см. FirstRunForm),
            // а Cancel для самого первого запуска не имеет смысла — без
            // имени сотрудника события отправлять некуда.
        }

        return new EmployeeConfig
        {
            EmployeeName = form.EmployeeName,
            EmployeeId = Config.DefaultEmployeeIdSlug()
        };
    }

    private void UpdateUi()
    {
        if (_sessionManager.State == WorkState.Running && _sessionManager.CurrentSessionStart is { } start)
        {
            var elapsed = DateTime.Now - start;
            _notifyIcon.Icon = _iconRunning;
            _notifyIcon.Text = Truncate($"Работаю: {elapsed:hh\\:mm\\:ss}");
        }
        else
        {
            _notifyIcon.Icon = _iconStopped;
            _notifyIcon.Text = "Учёт времени — остановлено";
        }

        _startItem.Enabled = _sessionManager.State != WorkState.Running;
        _stopItem.Enabled = _sessionManager.State == WorkState.Running;
    }

    // NotifyIcon.Text не может быть длиннее 63 символов.
    private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];

    /// <summary>
    /// Базовая иконка (секундомер на фиолетовом градиенте) + зелёный бейдж
    /// в углу, когда сессия активна. В состоянии "остановлено" — просто сама
    /// иконка без бейджа, статус всегда виден и по тултипу/пункту меню.
    /// </summary>
    private static Icon BuildTrayIcon(Color? badgeColor)
    {
        using var baseImage = LoadEmbeddedIcon();
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(baseImage, 0, 0, 32, 32);

            if (badgeColor is { } color)
            {
                var badgeRect = new Rectangle(19, 19, 12, 12);
                using var badgeBrush = new SolidBrush(color);
                using var borderPen = new Pen(Color.White, 1.5f);
                g.FillEllipse(badgeBrush, badgeRect);
                g.DrawEllipse(borderPen, badgeRect);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static Bitmap LoadEmbeddedIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("TimeTracker.Assets.icon.png")
            ?? throw new InvalidOperationException("Не найден embedded resource TimeTracker.Assets.icon.png");
        return new Bitmap(stream);
    }

    private void ExitApp()
    {
        _sessionManager.StopWork();
        _notifyIcon.Visible = false;

        _uiTimer.Dispose();
        _shutdownWatcher.Dispose();
        _syncService.Dispose();
        _apiClient.Dispose();
        _iconStopped.Dispose();
        _iconRunning.Dispose();

        Application.Exit();
    }
}
