using Content.Server.Explosion.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Radio;
using Content.Shared.Emp;
using Content.Shared.Examine;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Content.Shared._NF.Emp.Components; // Frontier
using Robust.Server.GameStates; // Frontier: EMP Blast PVS
using Robust.Shared.Configuration; // Frontier: EMP Blast PVS
using Robust.Shared; // Frontier: EMP Blast PVS

namespace Content.Server.Emp;

public sealed class EmpSystem : SharedEmpSystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly PvsOverrideSystem _pvs = default!; // Frontier: EMP Blast PVS
    [Dependency] private readonly IConfigurationManager _cfg = default!; // Frontier: EMP Blast PVS

    public const string EmpPulseEffectPrototype = "EffectEmpBlast"; // Frontier: EffectEmpPulse

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<EmpDisabledComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<EmpOnTriggerComponent, TriggerEvent>(HandleEmpTrigger);

        SubscribeLocalEvent<EmpDisabledComponent, RadioSendAttemptEvent>(OnRadioSendAttempt);
        SubscribeLocalEvent<EmpDisabledComponent, RadioReceiveAttemptEvent>(OnRadioReceiveAttempt);
    }

    /// <summary>
    ///   Triggers an EMP pulse at the given location, by first raising an <see cref="EmpAttemptEvent"/>, then a raising <see cref="EmpPulseEvent"/> on all entities in range.
    /// </summary>
    /// <param name="coordinates">The location to trigger the EMP pulse at.</param>
    /// <param name="range">The range of the EMP pulse.</param>
    /// <param name="energyConsumption">The amount of energy consumed by the EMP pulse.</param>
    /// <param name="duration">The duration of the EMP effects.</param>
    public void EmpPulse(MapCoordinates coordinates, float range, float energyConsumption, float duration)
    {
        foreach (var uid in _lookup.GetEntitiesInRange(coordinates, range))
        {
            TryEmpEffects(uid, energyConsumption, duration);
        }

        var empBlast = Spawn(EmpPulseEffectPrototype, coordinates); // Frontier: Added visual effect
        EnsureComp<EmpBlastComponent>(empBlast, out var empBlastComp); // Frontier
        empBlastComp.VisualRange = range; // Frontier

        if (range > _cfg.GetCVar(CVars.NetMaxUpdateRange)) // Frontier
            _pvs.AddGlobalOverride(empBlast); // Frontier

        Dirty(empBlast, empBlastComp); // Frontier
    }

    /// <summary>
    ///    Attempts to apply the effects of an EMP pulse onto an entity by first raising an <see cref="EmpAttemptEvent"/>, followed by raising a <see cref="EmpPulseEvent"/> on it.
    /// </summary>
    /// <param name="uid">The entity to apply the EMP effects on.</param>
    /// <param name="energyConsumption">The amount of energy consumed by the EMP.</param>
    /// <param name="duration">The duration of the EMP effects.</param>
    public void TryEmpEffects(EntityUid uid, float energyConsumption, float duration)
    {
        var attemptEv = new EmpAttemptEvent();
        RaiseLocalEvent(uid, attemptEv);
        if (attemptEv.Cancelled)
            return;

        DoEmpEffects(uid, energyConsumption, duration);
    }

    /// <summary>
    ///    Applies the effects of an EMP pulse onto an entity by raising a <see cref="EmpPulseEvent"/> on it.
    /// </summary>
    /// <param name="uid">The entity to apply the EMP effects on.</param>
    /// <param name="energyConsumption">The amount of energy consumed by the EMP.</param>
    /// <param name="duration">The duration of the EMP effects.</param>
    public void DoEmpEffects(EntityUid uid, float energyConsumption, float duration)
    {
        var ev = new EmpPulseEvent(energyConsumption, false, false, TimeSpan.FromSeconds(duration));
        RaiseLocalEvent(uid, ref ev);
        if (ev.Affected)
        {
            Spawn(EmpDisabledEffectPrototype, Transform(uid).Coordinates);
        }
        if (ev.Disabled)
        {
            var disabled = EnsureComp<EmpDisabledComponent>(uid);
            // couldnt use null-coalescing operator here sadge
            if (disabled.DisabledUntil == TimeSpan.Zero)
            {
                disabled.DisabledUntil = Timing.CurTime;
            }
            disabled.DisabledUntil = disabled.DisabledUntil + TimeSpan.FromSeconds(duration);

            /// i tried my best to go through the Pow3r server code but i literally couldn't find in relation to PowerNetworkBatteryComponent that uses the event system
            /// the code is otherwise too esoteric for my innocent eyes
            if (TryComp<PowerNetworkBatteryComponent>(uid, out var powerNetBattery))
            {
                powerNetBattery.CanCharge = false;
            }
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<EmpDisabledComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.DisabledUntil < Timing.CurTime)
            {
                RemComp<EmpDisabledComponent>(uid);
                var ev = new EmpDisabledRemoved();
                RaiseLocalEvent(uid, ref ev);

                if (TryComp<PowerNetworkBatteryComponent>(uid, out var powerNetBattery))
                {
                    powerNetBattery.CanCharge = true;
                }
            }
        }
    }

    private void OnExamine(EntityUid uid, EmpDisabledComponent component, ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("emp-disabled-comp-on-examine"));
    }

    private void HandleEmpTrigger(EntityUid uid, EmpOnTriggerComponent comp, TriggerEvent args)
    {
        EmpPulse(_transform.GetMapCoordinates(uid), comp.Range, comp.EnergyConsumption, comp.DisableDuration);
        args.Handled = true;
    }

    private void OnRadioSendAttempt(EntityUid uid, EmpDisabledComponent component, ref RadioSendAttemptEvent args)
    {
        args.Cancelled = true;
    }

    private void OnRadioReceiveAttempt(EntityUid uid, EmpDisabledComponent component, ref RadioReceiveAttemptEvent args)
    {
        args.Cancelled = true;
    }
}

/// <summary>
/// Raised on an entity before <see cref="EmpPulseEvent"/>. Cancel this to prevent the emp event being raised.
/// </summary>
public sealed partial class EmpAttemptEvent : CancellableEntityEventArgs
{
}

[ByRefEvent]
public record struct EmpPulseEvent(float EnergyConsumption, bool Affected, bool Disabled, TimeSpan Duration);

[ByRefEvent]
public record struct EmpDisabledRemoved();
