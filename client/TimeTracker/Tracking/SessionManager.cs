using TimeTracker.Storage;

namespace TimeTracker.Tracking;

public enum WorkState
{
    Stopped,
    Running
}

/// <summary>
/// Состояние Stopped/Running и вся логика вокруг него. Единственный способ
/// перейти в Running — явный вызов StartWork() (кнопка). Никакого автозапуска
/// сессии по включению компьютера и никакого детекта бездействия — по
/// требованию заказчика кнопка сама по себе и есть механизм доверия.
/// </summary>
public class SessionManager
{
    private readonly LocalDb _db;
    private readonly EmployeeConfig _employee;
    private readonly string _machineId;
    private readonly Action _requestImmediateSync;

    private readonly System.Windows.Forms.Timer _heartbeatTimer;
    private int _ticksSinceLastQueuedHeartbeat;
    private const int HeartbeatTickSeconds = 60;
    private const int QueuedHeartbeatEveryNTicks = 5; // ~5 минут

    public WorkState State { get; private set; } = WorkState.Stopped;
    public string? CurrentSessionId { get; private set; }
    public DateTime? CurrentSessionStart { get; private set; }

    public event Action? StateChanged;

    public SessionManager(LocalDb db, EmployeeConfig employee, string machineId, Action requestImmediateSync)
    {
        _db = db;
        _employee = employee;
        _machineId = machineId;
        _requestImmediateSync = requestImmediateSync;

        _heartbeatTimer = new System.Windows.Forms.Timer { Interval = HeartbeatTickSeconds * 1000 };
        _heartbeatTimer.Tick += (_, _) => OnHeartbeatTick();
    }

    /// <summary>
    /// Вызывается один раз при старте приложения — до того, как что-либо
    /// ещё трогает состояние. Закрывает "осиротевшую" сессию, если предыдущий
    /// запуск не завершился штатно (крэш процесса, BSOD, отключение питания —
    /// случаи, когда ShutdownWatcher не успевает сработать). Приложение всегда
    /// стартует в Stopped: сотрудник обязан заново нажать "Начать работу".
    /// </summary>
    public void RecoverOrphanedSessionIfAny()
    {
        var orphan = _db.FindOrphanedOpenSession();
        if (orphan is null) return;

        _db.InsertEvent(new TrackedEvent
        {
            SessionId = orphan.SessionId,
            EventType = EventType.Stop,
            ClientTimestamp = orphan.LastHeartbeat,
            EmployeeId = orphan.EmployeeId,
            EmployeeName = orphan.EmployeeName,
            MachineId = orphan.MachineId,
            StopStatus = Storage.StopStatus.RecoveredAfterCrash
        });

        _requestImmediateSync();
    }

    public void StartWork()
    {
        if (State == WorkState.Running) return;

        var sessionId = Guid.NewGuid().ToString();
        var now = DateTime.Now;

        _db.InsertEvent(new TrackedEvent
        {
            SessionId = sessionId,
            EventType = EventType.Start,
            ClientTimestamp = now,
            EmployeeId = _employee.EmployeeId,
            EmployeeName = _employee.EmployeeName,
            MachineId = _machineId
        });

        CurrentSessionId = sessionId;
        CurrentSessionStart = now;
        State = WorkState.Running;
        _ticksSinceLastQueuedHeartbeat = 0;
        _heartbeatTimer.Start();

        _requestImmediateSync();
        StateChanged?.Invoke();
    }

    /// <summary>Остановка вручную, кнопкой из трей-меню.</summary>
    public void StopWork() => StopInternal(Storage.StopStatus.Button);

    /// <summary>Остановка при выключении/выходе из Windows (см. ShutdownWatcher).</summary>
    public void StopFromShutdown() => StopInternal(Storage.StopStatus.Shutdown);

    private void StopInternal(string stopStatus)
    {
        if (State != WorkState.Running || CurrentSessionId is null) return;

        _db.InsertEvent(new TrackedEvent
        {
            SessionId = CurrentSessionId,
            EventType = EventType.Stop,
            ClientTimestamp = DateTime.Now,
            EmployeeId = _employee.EmployeeId,
            EmployeeName = _employee.EmployeeName,
            MachineId = _machineId,
            StopStatus = stopStatus
        });

        _heartbeatTimer.Stop();
        CurrentSessionId = null;
        CurrentSessionStart = null;
        State = WorkState.Stopped;

        _requestImmediateSync();
        StateChanged?.Invoke();
    }

    private void OnHeartbeatTick()
    {
        if (State != WorkState.Running || CurrentSessionId is null) return;

        var now = DateTime.Now;
        _db.TouchHeartbeat(CurrentSessionId, now);

        _ticksSinceLastQueuedHeartbeat++;
        if (_ticksSinceLastQueuedHeartbeat >= QueuedHeartbeatEveryNTicks)
        {
            _ticksSinceLastQueuedHeartbeat = 0;
            _db.InsertEvent(new TrackedEvent
            {
                SessionId = CurrentSessionId,
                EventType = EventType.Heartbeat,
                ClientTimestamp = now,
                EmployeeId = _employee.EmployeeId,
                EmployeeName = _employee.EmployeeName,
                MachineId = _machineId
            });
            _requestImmediateSync();
        }
    }
}
