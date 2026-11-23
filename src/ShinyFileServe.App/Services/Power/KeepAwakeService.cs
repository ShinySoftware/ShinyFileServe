using Microsoft.Extensions.Logging;
using ShinyFileServe.Core.Documents;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Server.Power;
using ShinyFileServe.Shared;

namespace ShinyFileServe.App.Services.Power;

/// <summary>
/// Keeps the machine out of idle sleep while the app runs, per <see cref="ServerSettings.KeepAwake"/>.
/// Independent of whether the server is serving: the setting is about the app being up, and a stopped
/// server the operator can restart from the tray is no use on a machine that has gone to sleep.
///
/// Unplugging is noticed by polling rather than by subscribing - each platform has its own
/// notification for it, and half a minute late is close enough for a machine that would otherwise
/// take minutes of idle to fall asleep.
/// </summary>
public sealed class KeepAwakeService : IKeepAwake, IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    readonly ISettingsManager settings;
    readonly ILogger<KeepAwakeService> logger;
    readonly IPowerPlatform platform = IPowerPlatform.Create();
    readonly Lock gate = new();
    Timer? timer;
    KeepAwakeInfo current;

    public KeepAwakeService(ISettingsManager settings, ILogger<KeepAwakeService> logger)
    {
        this.settings = settings;
        this.logger = logger;
        this.current = new(settings.Current.KeepAwake, this.platform.IsSupported, false, false, false);
        this.settings.Changed += (_, _) => this.Refresh();
    }

    public KeepAwakeInfo Current
    {
        get
        {
            lock (this.gate)
                return this.current;
        }
    }

    public void Start()
    {
        this.Refresh();
        this.timer ??= new Timer(_ => this.Refresh(), null, PollInterval, PollInterval);
    }

    public void Refresh()
    {
        lock (this.gate)
        {
            try
            {
                var mode = this.settings.Current.KeepAwake;
                var power = this.platform.ReadPowerSource();

                // Without a battery there is no "on battery", so the last two choices are the same one.
                var want = mode switch
                {
                    KeepAwakeMode.Always => true,
                    KeepAwakeMode.WhenPluggedIn => !power.IsOnBattery,
                    _ => false
                };

                var wasHolding = this.current.IsHolding;
                var holding = this.platform.IsSupported && this.platform.SetHold(want);

                if (holding != wasHolding)
                    this.logger.LogInformation(holding ? "Keeping this machine awake" : "Letting this machine sleep normally");

                this.current = new(mode, this.platform.IsSupported, power.HasBattery, power.IsOnBattery, holding);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Keep-awake check failed");
            }
        }
    }

    public void Dispose()
    {
        this.timer?.Dispose();
        lock (this.gate)
            this.platform.Dispose();
    }
}
