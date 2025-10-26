using System.Numerics;
using Content.Shared._RMC14.Areas;
using Content.Shared._RMC14.BlurredVision;
using Content.Shared._RMC14.Chat;
using Content.Shared._RMC14.Deafness;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Pulling;
using Content.Shared._RMC14.Slow;
using Content.Shared._RMC14.Stamina;
using Content.Shared._RMC14.Stun;
using Content.Shared._RMC14.Synth;
using Content.Shared._RMC14.Xenonids.Construction.Nest;
using Content.Shared._RMC14.Xenonids.Parasite;
using Content.Shared.ActionBlocker;
using Content.Shared.Chat;
using Content.Shared.Coordinates;
using Content.Shared.Damage;
using Content.Shared.Drugs;
using Content.Shared.Drunk;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Jittering;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Random.Helpers;
using Content.Shared.Rejuvenate;
using Content.Shared.Speech.EntitySystems;
using Content.Shared.StatusEffect;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Teargas;

public abstract class SharedTeargasSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly EntityLookupSystem _entityLookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly RMCStaminaSystem _stamina = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;
    [Dependency] private readonly SharedSlurredSystem _slurred = default!;
    [Dependency] private readonly SharedStutteringSystem _stutter = default!;
    [Dependency] private readonly RMCDazedSystem _daze = default!;
    [Dependency] private readonly SharedJitteringSystem _jitter = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!; //It's how this fakes movement
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly RMCPullingSystem _rmcPulling = default!;
    [Dependency] private readonly RMCSlowSystem _slow = default!;
    [Dependency] private readonly SharedDeafnessSystem _deafness = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedCMChatSystem _rmcChat = default!;
    [Dependency] private readonly ISharedPlayerManager _player = default!;
    [Dependency] private readonly AreaSystem _area = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private readonly HashSet<Entity<MarineComponent>> _marines = new();
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TeargasComponent, RejuvenateEvent>(OnRejuvenate);
        SubscribeLocalEvent<TeargasInjectorComponent, ProjectileHitEvent>(OnProjectileHit);
    }

    private void OnRejuvenate(Entity<TeargasComponent> ent, ref RejuvenateEvent args)
    {
        RemCompDeferred<TeargasComponent>(ent);
    }

    private void OnProjectileHit(Entity<TeargasInjectorComponent> ent, ref ProjectileHitEvent args)
    {
        if (!HasComp<MarineComponent>(args.Target))
            return;

        if (!ent.Comp.AffectsDead && _mobState.IsDead(args.Target))
            return;

        if (!ent.Comp.AffectsInfectedNested &&
                    HasComp<XenoNestedComponent>(args.Target) &&
                    HasComp<VictimInfectedComponent>(args.Target))
        {
            return;
        }

        var time = _timing.CurTime;

        if (!EnsureComp<TeargasComponent>(args.Target, out var gas))
        {
            gas.LastMessage = time;
            gas.LastAccentTime = time;
            gas.LastStumbleTime = time;
        }

        _statusEffects.TryAddStatusEffect<RMCBlindedComponent>(args.Target, "Blinded", gas.BlurTime * 6, true);
        _daze.TryDaze(ent, ent.Comp.DazeTime, true, stutter: true);
        gas.TeargasAmount += ent.Comp.GasPerSecond;
    }


    public override void Update(float frameTime)
    {
        if (_net.IsClient)
            return;

        var time = _timing.CurTime;
        var gastoxinInjectorQuery = EntityQueryEnumerator<TeargasInjectorComponent>();

        while (gastoxinInjectorQuery.MoveNext(out var uid, out var gasGas))
        {
            if (!gasGas.InjectInContact)
                continue;

            _marines.Clear();
            _entityLookup.GetEntitiesInRange(uid.ToCoordinates(), 0.5f, _marines);

            foreach (var marine in _marines)
            {
                if (!gasGas.AffectsDead && _mobState.IsDead(marine))
                    continue;

                if (!gasGas.AffectsInfectedNested &&
                    HasComp<XenoNestedComponent>(marine) &&
                    HasComp<VictimInfectedComponent>(marine))
                {
                    continue;
                }

                if (!EnsureComp<TeargasComponent>(marine, out var builtTeargas))
                {
                    builtTeargas.LastMessage = time;
                    builtTeargas.LastAccentTime = time;
                    builtTeargas.LastStumbleTime = time;
                    builtTeargas.NextGasInjectionAt = time;
                    builtTeargas.NextNeuroEffectAt = time;
                }

                if (time < builtTeargas.NextGasInjectionAt)
                    continue;

                _statusEffects.TryAddStatusEffect<RMCBlindedComponent>(marine, "Blinded", builtTeargas.BlurTime * 12, true);
                _daze.TryDaze(marine, gasGas.DazeTime, true, stutter: true);
                builtTeargas.TeargasAmount += gasGas.GasPerSecond;
                builtTeargas.NextGasInjectionAt = time + gasGas.TimeBetweenGasInjects;
            }
        }

        var gasToxinQuery = EntityQueryEnumerator<TeargasComponent>();

        while (gasToxinQuery.MoveNext(out var uid, out var gas))
        {
            if (time < gas.NextNeuroEffectAt)
                continue;

            gas.TeargasAmount -= gas.DepletionPerTick;

            gas.NextNeuroEffectAt = time + gas.UpdateEvery;

            if (gas.TeargasAmount <= 0 || HasComp<SynthComponent>(uid))
            {
                RemCompDeferred<TeargasComponent>(uid);
                continue;
            }

            if (_mobState.IsDead(uid))
                continue;

            //Basic Effects
            _stamina.DoStaminaDamage(uid, gas.StaminaDamagePerTick, visual: false);
            _statusEffects.TryAddStatusEffect<DrunkComponent>(uid, "Drunk", gas.DizzyStrength, true);

            TeargasNonStackingEffects(uid, gas, time, out var coughChance, out var stumbleChance);
            TeargasStackingEffects(uid, gas, time);

            if (_random.Prob(stumbleChance) && time - gas.LastStumbleTime >= gas.MinimumDelayBetweenEvents)
            {
                gas.LastStumbleTime = time;
                // This is how we randomly move them - by throwing
                if (_blocker.CanMove(uid))
                {
                    _rmcPulling.TryStopPullsOn(uid);
                    _physics.SetLinearVelocity(uid, Vector2.Zero);
                    _physics.SetAngularVelocity(uid, 0f);
                    _throwing.TryThrow(uid, _random.NextAngle().ToVec().Normalized() / 10, 10, animated: false, playSound: false, doSpin: false, compensateFriction: true);
                }
                _popup.PopupEntity(Loc.GetString("rmc-stumble-others", ("victim", uid)), uid, Filter.PvsExcept(uid), true, PopupType.SmallCaution);
                _popup.PopupEntity(Loc.GetString("rmc-stumble"), uid, uid, PopupType.MediumCaution);
                _daze.TryDaze(uid, gas.DazeLength * 5, true, stutter: true);
                _jitter.DoJitter(uid, gas.StumbleJitterTime, true);
                _statusEffects.TryAddStatusEffect<DrunkComponent>(uid, "Drunk", gas.DizzyStrengthOnStumble, true);
                var ev = new TeargasEmoteEvent() { Emote = gas.PainId };
                RaiseLocalEvent(uid, ev);
            }

            if (_random.Prob(coughChance))
            {
                _slow.TrySlowdown(uid, gas.BloodCoughDuration);
                _popup.PopupEntity(Loc.GetString("rmc-bloodcough"), uid, uid, PopupType.MediumCaution);
                var ev = new TeargasEmoteEvent() { Emote = gas.CoughId };
                RaiseLocalEvent(uid, ev);
            }

        }

        var gasHallucinationQuery = EntityQueryEnumerator<TeargasLingeringHallucinationComponent>();

        while (gasHallucinationQuery.MoveNext(out var uid, out var hallu))
        {
            if (hallu.Hallucinations.Count == 0)
            {
                RemCompDeferred<TeargasLingeringHallucinationComponent>(uid);
                continue;
            }

            List<(NeuroHallucinations, int, TimeSpan, EntityCoordinates?)> toRemove = new();
            List<(NeuroHallucinations, int, TimeSpan, EntityCoordinates?)> toAdd = new();

            foreach (var entry in hallu.Hallucinations)
            {
                if (entry.Item3 > time)
                    continue;

                var newEntry = ProcessHallucination(uid, hallu, entry);

                toRemove.Add(entry);

                if (newEntry != null)
                    toAdd.Add(newEntry.Value);
            }

            hallu.Hallucinations.RemoveAll(a => toRemove.Contains(a));

            hallu.Hallucinations.AddRange(toAdd);
        }

    }

    private void TeargasNonStackingEffects(EntityUid victim, TeargasComponent gastoxin, TimeSpan time, out float coughChance, out float stumbleChance)
    {
        string message = "rmc-gas-tired";
        PopupType poptype = PopupType.Small;
        coughChance = 0;
        stumbleChance = 0;
        if (gastoxin.TeargasAmount <= 9)
        {
            //Do nothing, the intial conditions are already set
        }
        else if (gastoxin.TeargasAmount <= 14)
        {
            message = "rmc-gas-numb";
            poptype = PopupType.SmallCaution;
            coughChance = 0.10f;
        }
        else if (gastoxin.TeargasAmount <= 19)
        {
            int chance = _random.Next(4);
            if (chance == 0)
            {
                message = "rmc-gas-where";
                poptype = PopupType.Large;
            }
            else
            {
                message = _random.Pick(new List<string> {"rmc-gas-very-numb", "rmc-gas-erratic", "rmc-gas-panic"});
                poptype = PopupType.MediumCaution;
            }
            coughChance = 0.10f;
            stumbleChance = 0.05f;
        }
        else if (gastoxin.TeargasAmount <= 24)
        {
            message = "rmc-gas-sting";
            poptype = PopupType.MediumCaution;
            coughChance = 0.25f;
            stumbleChance = 0.25f;

        }
        else
        {
            int chance = _random.Next(7);
            if (chance == 0)
            {
                message = "rmc-gas-what";
                poptype = PopupType.Large;
            }
            else if (chance == 1)
            {
                message = "rmc-gas-hearing";
                poptype = PopupType.MediumCaution;
            }
            else
            {
                message = _random.Pick(new List<string> { "rmc-gas-pain", "rmc-gas-agh", "rmc-gas-so-numb", "rmc-gas-limbs", "rmc-gas-think"});
                poptype = PopupType.LargeCaution;
            }
            coughChance = 0.25f;
            stumbleChance = 0.25f;
        }

        if (time - gastoxin.LastMessage >= gastoxin.TimeBetweenMessages)
        {
            gastoxin.LastMessage = time;
            _popup.PopupEntity(Loc.GetString(message), victim, victim, poptype);
        }
    }

    private void TeargasStackingEffects(EntityUid victim, TeargasComponent gastoxin, TimeSpan currTime)
    {
        if (gastoxin.TeargasAmount >= 10)
        {
            _statusEffects.TryAddStatusEffect<RMCBlindedComponent>(victim, "Blinded", gastoxin.BlurTime, true);
            if (currTime - gastoxin.LastAccentTime >= gastoxin.MinimumDelayBetweenEvents)
            {
                gastoxin.LastAccentTime = currTime;
                if (_random.Prob(0.5f))
                    _slurred.DoSlur(victim, gastoxin.AccentTime);
                else
                    _stutter.DoStutter(victim, gastoxin.AccentTime, true);
            }
        }

        if (gastoxin.TeargasAmount >= 15)
        {
            // TODO RMC14 Agony effect - gives fake damage, pain needs this too so maybe then
            _jitter.DoJitter(victim, gastoxin.JitterTime, true);
            if (currTime >= gastoxin.NextHallucination)
            {
                gastoxin.NextHallucination = currTime + _random.Next(gastoxin.HallucinationEveryMin, gastoxin.HallucinationEveryMax);
                DoNeuroHallucination(victim, gastoxin);
            }
        }

        if (gastoxin.TeargasAmount >= 20)
        {
            _statusEffects.TryAddStatusEffect<TemporaryBlindnessComponent>(victim, "TemporaryBlindness", gastoxin.BlindTime, true);
        }

        if (gastoxin.TeargasAmount >= 27)
        {
            _daze.TryDaze(victim, gastoxin.DazeLength, true, stutter: true);
            _deafness.TryDeafen(victim, gastoxin.DeafenTime, true, ignoreProtection: true);
        }
    }

    private void DoNeuroHallucination(EntityUid victim, TeargasComponent gastoxin)
    {
        var hallucination = SharedRandomExtensions.Pick(gastoxin.Hallucinations, _random.GetRandom());
        //Note event times are hardcoded for now since thers alot of them
        switch (hallucination)
        {
            case NeuroHallucinations.AlienAttack:
                _audio.PlayStatic(gastoxin.Pounce, victim, victim.ToCoordinates());
                _stun.TryParalyze(victim, gastoxin.PounceDownTime, true);
                var lingering = EnsureComp<TeargasLingeringHallucinationComponent>(victim);
                lingering.Hallucinations.Add((NeuroHallucinations.AlienAttack, 0, _timing.CurTime + TimeSpan.FromSeconds(1), null));
                break;
            case NeuroHallucinations.OB:
                //Little extra to confuse the player
                //TODO RMC14 replace if it gets a locId
                if (_player.TryGetSessionByEntity(victim, out var session))
                {
                    var msg = "[font size=16][color=red]Orbital bombardment launch command detected![/color][/font]";
                    msg = $"[bold][font size=24][color=red]\n{msg}\n[/color][/font][/bold]";
                    _rmcChat.ChatMessageToOne(ChatChannel.Radio, msg, msg, default, false, session.Channel, recordReplay: true);

                    if (_area.TryGetArea(victim.ToCoordinates(), out _, out var areaProto))
                    {
                        var warhead = _random.Pick(gastoxin.WarheadTypes);

                        if (_proto.TryIndex(warhead, out var warHeadProto))
                        {
                            msg = $"[color=red]Launch command informs {warHeadProto.Name}. Estimated impact area: {areaProto.Name}[/color]";
                            _rmcChat.ChatMessageToOne(ChatChannel.Radio, msg, msg, default, false, session.Channel, recordReplay: true);
                        }
                    }
                }
                _audio.PlayGlobal(gastoxin.OBAlert, victim);
                lingering = EnsureComp<TeargasLingeringHallucinationComponent>(victim);
                lingering.Hallucinations.Add((NeuroHallucinations.OB, 0, _timing.CurTime + TimeSpan.FromSeconds(2), null));
                break;
            case NeuroHallucinations.Screech:
                _audio.PlayStatic(gastoxin.Screech, victim, HallucinationSoundOffset(victim, 3));
                _stun.TryParalyze(victim, gastoxin.ScreechDownTime, true);
                break;
            case NeuroHallucinations.CAS:
                var position = HallucinationSoundOffset(victim, 7);
                _audio.PlayStatic(gastoxin.FiremissionStart, victim, position);
                lingering = EnsureComp<TeargasLingeringHallucinationComponent>(victim);
                lingering.Hallucinations.Add((NeuroHallucinations.CAS, 0, _timing.CurTime + TimeSpan.FromSeconds(3.5), position));
                break;
            case NeuroHallucinations.Giggle:
                var ev = new TeargasEmoteEvent() { Emote = gastoxin.GiggleId };
                RaiseLocalEvent(victim, ev);
                //TODO RMC14 hallucination status - more in depth than gas
                _statusEffects.TryAddStatusEffect<SeeingRainbowsStatusEffectComponent>(victim, "StatusEffectSeeingRainbow", gastoxin.RainbowDuration, true);
                break;
            case NeuroHallucinations.Mortar:
                position = HallucinationSoundOffset(victim, 7);
                FakeWarning(position, victim, "rmc-mortar-shell-impact-warning", "rmc-mortar-shell-impact-warning-above");
                lingering = EnsureComp<TeargasLingeringHallucinationComponent>(victim);
                lingering.Hallucinations.Add((NeuroHallucinations.Mortar, 0, _timing.CurTime + TimeSpan.FromSeconds(1), position));
                break;
            case NeuroHallucinations.Sounds:
                var sound = _random.Pick(gastoxin.HallucinationRandomSounds);
                //Random offset to make it spookier if it's real or not
                _audio.PlayStatic(sound, victim, HallucinationSoundOffset(victim, 7));
                break;
        }
    }

    //Returns true if the hallucination is done.
    private (NeuroHallucinations, int, TimeSpan, EntityCoordinates?)? ProcessHallucination(EntityUid victim, TeargasLingeringHallucinationComponent lingering, (NeuroHallucinations, int, TimeSpan, EntityCoordinates?) hallucination)
    {
        switch (hallucination.Item1)
        {
            case NeuroHallucinations.AlienAttack:
                if (hallucination.Item2 == 0)
                {
                    _audio.PlayStatic(lingering.XenoClaw, victim, victim.ToCoordinates());
                    _audio.PlayStatic(lingering.BoneBreak, victim, victim.ToCoordinates());
                    hallucination.Item2 = 1;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 < 3)
                {
                    _audio.PlayStatic(lingering.XenoClaw, victim, victim.ToCoordinates());
                    hallucination.Item2 += 1;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else
                {
                    _audio.PlayStatic(lingering.BoneBreak, victim, victim.ToCoordinates());
                    // TODO RMC14 Agony
                    var ev = new TeargasEmoteEvent() { Emote = lingering.PainEmote };
                    RaiseLocalEvent(victim, ev);
                }
                break;

            case NeuroHallucinations.OB:
                _audio.PlayStatic(lingering.OBTravel, victim, HallucinationSoundOffset(victim, 7));
                break;

            case NeuroHallucinations.CAS: //Very long unfortunately
                if (hallucination.Item2 == 0)
                {
                    FakeWarning(hallucination.Item4 ?? victim.ToCoordinates(), victim, "rmc-dropship-firemission-warning", "rmc-dropship-firemission-warning-above");
                    hallucination.Item2 = 1;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 == 1)
                {
                    _audio.PlayStatic(lingering.RocketFire, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 2;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 == 2)
                {
                    _audio.PlayStatic(lingering.GauFire, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 3;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 == 3)
                {
                    _audio.PlayStatic(lingering.RocketFire, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 4;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(1);
                    return hallucination;
                }
                else if (hallucination.Item2 == 4)
                {
                    _audio.PlayStatic(lingering.Explosion, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 5;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(1);
                    return hallucination;
                }
                else if (hallucination.Item2 == 5)
                {
                    _audio.PlayStatic(lingering.RocketFire, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 6;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(1);
                    return hallucination;
                }
                else if (hallucination.Item2 == 6)
                {
                    _audio.PlayStatic(lingering.Explosion, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 7;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 == 7)
                {
                    _audio.PlayStatic(lingering.BigExplosion, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 8;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 == 8)
                {
                    _audio.PlayStatic(lingering.RocketFire, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 9;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 == 9)
                {
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.Explosion, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 10;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else if (hallucination.Item2 == 10)
                {
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    hallucination.Item2 = 11;
                    hallucination.Item3 = _timing.CurTime + TimeSpan.FromSeconds(0.5);
                    return hallucination;
                }
                else
                {
                    _audio.PlayStatic(lingering.Explosion, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    _audio.PlayStatic(lingering.GauHit, victim, HallucinationSoundOffset(hallucination.Item4 ?? victim.ToCoordinates(), 7));
                    var ev = new TeargasEmoteEvent() { Emote = lingering.PainEmote };
                    RaiseLocalEvent(victim, ev);
                }
                break;

            case NeuroHallucinations.Mortar:
                _audio.PlayStatic(lingering.MortarTravel, victim, hallucination.Item4 ?? victim.ToCoordinates());
                break;
        }
        return null;
    }

    private EntityCoordinates HallucinationSoundOffset(EntityUid victim, float maxDistance)
    {
        var randomOffset =
        new Vector2
        (
            _random.NextFloat(-maxDistance, maxDistance + 0.01f),
            _random.NextFloat(-maxDistance, maxDistance + 0.01f)
        );

        var newCoords = Transform(victim).Coordinates.Offset(randomOffset);

        return newCoords;
    }

    private EntityCoordinates HallucinationSoundOffset(EntityCoordinates coords, float maxDistance)
    {
        var randomOffset =
        new Vector2
        (
            _random.NextFloat(-maxDistance, maxDistance + 0.01f),
            _random.NextFloat(-maxDistance, maxDistance + 0.01f)
        );

        var newCoords = coords.Offset(randomOffset);

        return newCoords;
    }

    private void FakeWarning(EntityCoordinates coords, EntityUid player, LocId directionWarning, LocId aboveWarning)
    {
        var distanceVec = _transform.GetMapCoordinates(player).Position - _transform.ToMapCoordinates(coords).Position;
        var distance = distanceVec.Length();

        var direction = distanceVec.GetDir().ToString().ToUpperInvariant();

        var msg = distance < 1
        ? Loc.GetString(aboveWarning)
        : Loc.GetString(directionWarning, ("direction", direction));

        _popup.PopupEntity(msg, player, player, PopupType.LargeCaution);

        if (_player.TryGetSessionByEntity(player, out var session))
        {
            msg = $"[bold][font size=24][color=red]\n{msg}\n[/color][/font][/bold]";
            _rmcChat.ChatMessageToOne(ChatChannel.Radio, msg, msg, default, false, session.Channel, recordReplay: true);
        }
    }
}
